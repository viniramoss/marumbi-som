using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Agents;

/// <summary>
/// WebSockets das lojas (seção 11.4): uma conexão por loja (a nova substitui a antiga), mensagens do agente gravadas
/// no banco e comandos ao vivo com espera pelo <c>command.result</c>.
/// </summary>
public sealed class AgentConnections(IServiceScopeFactory scopes, TimeProvider time, ILogger<AgentConnections> logger)
{
    public const int MaxMessageBytes = 64 * 1024;

    /// <summary>Evento gravado quando o <c>uptimeS</c> do status diminui (D23 e).</summary>
    public const string AgentRestartedEvent = "agent.restarted";

    /// <summary>Sem nenhuma mensagem nesse tempo (o status vem a cada 30 s), a conexão é considerada morta.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResultMessage>> _pending = new(StringComparer.Ordinal);

    /// <summary>Disparado depois de gravar cada mensagem recebida (usado pelo painel ao vivo e pelos testes).</summary>
    public event EventHandler<(int StoreId, AgentMessage Message)>? MessageHandled;

    /// <summary>Disparado quando uma loja conecta ou desconecta.</summary>
    public event EventHandler<int>? ConnectionChanged;

    /// <summary>A fila informada pela loja mudou (ex.: músicas novas na playlist); o detalhe da loja no painel é relido.</summary>
    public event EventHandler<int>? QueueChanged;

    public bool IsOnline(int storeId) => _connections.ContainsKey(storeId);

    public async Task RunAsync(int storeId, string slug, WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var connection = new Connection(socket);
        if (_connections.TryGetValue(storeId, out var previous))
        {
            logger.LogInformation("Loja {Store} reconectou; a conexão anterior será fechada", slug);
            await previous.CloseAsync("substituída por nova conexão");
        }

        _connections[storeId] = connection;
        logger.LogInformation("Loja {Store} conectada", slug);
        ConnectionChanged?.Invoke(this, storeId);
        try
        {
            await ReceiveLoopAsync(storeId, slug, connection, cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            logger.LogInformation("Conexão da loja {Store} encerrada: {Reason}", slug, ex.Message);
        }
        finally
        {
            var removed = _connections.TryRemove(new KeyValuePair<int, Connection>(storeId, connection));
            connection.Dispose();
            logger.LogInformation("Loja {Store} desconectada", slug);
            if (removed)
            {
                ConnectionChanged?.Invoke(this, storeId);
            }
        }
    }

    public Task NotifyManifestChangedAsync(int storeId, long version, CancellationToken cancellationToken) =>
        SendAsync(storeId, new ManifestChangedMessage(version), cancellationToken);

    /// <summary>Envia um comando e espera o resultado por até <paramref name="wait"/>. Nulo = sem resposta a tempo.</summary>
    public async Task<CommandResultMessage?> SendCommandAsync(int storeId, CommandMessage command, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var completion = new TaskCompletionSource<CommandResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[command.Id] = completion;
        try
        {
            if (!await SendAsync(storeId, command, cancellationToken))
            {
                return null;
            }

            return await completion.Task.WaitAsync(wait, time, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(command.Id, out _);
        }
    }

    /// <summary>Fecha a conexão da loja (ex.: token revogado).</summary>
    public async Task DisconnectAsync(int storeId, string reason)
    {
        if (_connections.TryRemove(storeId, out var connection))
        {
            await connection.CloseAsync(reason);
            ConnectionChanged?.Invoke(this, storeId);
        }
    }

    private async Task<bool> SendAsync(int storeId, HubMessage message, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(storeId, out var connection))
        {
            return false;
        }

        try
        {
            await connection.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
        {
            logger.LogWarning("Falha ao enviar {Type} para a loja {StoreId}: {Error}", message.GetType().Name, storeId, ex.Message);
            return false;
        }
    }

    private async Task ReceiveLoopAsync(int storeId, string slug, Connection connection, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxMessageBytes);
        try
        {
            while (connection.Socket.State == WebSocketState.Open)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (length >= MaxMessageBytes)
                    {
                        await connection.CloseAsync("mensagem grande demais", WebSocketCloseStatus.MessageTooBig);
                        return;
                    }

                    result = await connection.Socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, MaxMessageBytes - length), idle.Token);
                    length += result.Count;
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.CloseAsync("fechada pela loja");
                    return;
                }

                AgentMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<AgentMessage>(buffer.AsSpan(0, length), ContractJson.Options);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning("Mensagem inválida da loja {Store}: {Error}", slug, ex.Message);
                    continue;
                }

                if (message is not null)
                {
                    await HandleAsync(storeId, slug, message, cancellationToken);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal async Task HandleAsync(int storeId, string slug, AgentMessage message, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        long? announceVersion = null;
        var queueChanged = false;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            var state = await db.StoreStates.SingleOrDefaultAsync(s => s.StoreId == storeId, cancellationToken);
            if (state is null)
            {
                state = new StoreState { StoreId = storeId };
                db.StoreStates.Add(state);
            }

            state.LastSeenAt = now;
            switch (message)
            {
                case HelloMessage hello:
                    if (!string.Equals(hello.StoreId, slug, StringComparison.Ordinal) && hello.StoreId.Length > 0)
                    {
                        logger.LogWarning("Loja {Store} se apresentou como {Claimed}", slug, hello.StoreId);
                    }

                    state.AgentVersion = hello.AgentVersion;
                    state.AppliedVersion = hello.ActiveVersion;
                    var latest = await ManifestPublisher.LatestAsync(db, storeId, cancellationToken);
                    if (latest is not null && latest.Version > hello.ActiveVersion)
                    {
                        announceVersion = latest.Version;
                    }

                    break;
                case StatusMessage status:
                    if (state.UptimeS is { } previousUptime && status.UptimeS < previousUptime)
                    {
                        db.Events.Add(new StoreEvent { StoreId = storeId, Type = AgentRestartedEvent, CreatedAt = now });
                    }

                    var bluetooth = Wire(status.Bluetooth);
                    if (state.BtState != bluetooth || state.BtChangedAt is null)
                    {
                        state.BtChangedAt = now;
                    }

                    if (state.PendingVersion != status.PendingVersion || state.SyncDone != status.SyncProgress?.Done || state.SyncChangedAt is null)
                    {
                        state.SyncChangedAt = now;
                    }

                    state.PlayerState = Wire(status.Player);
                    state.NowPlayingHash = status.NowPlaying;
                    state.PositionMs = status.PositionMs;
                    state.PositionAt = now;
                    state.Volume = status.Volume;
                    state.BtState = bluetooth;
                    state.PendingVersion = status.PendingVersion;
                    state.SyncDone = status.SyncProgress?.Done;
                    state.SyncTotal = status.SyncProgress?.Total;
                    state.DiskFreeMb = status.DiskFreeMb;
                    state.UptimeS = status.UptimeS;
                    state.PlaylistKey = status.PlaylistId;
                    var queue = status.Queue is null ? null : JsonSerializer.Serialize(status.Queue.Take(10));
                    queueChanged = !string.Equals(queue, state.QueueJson, StringComparison.Ordinal);
                    state.QueueJson = queue;
                    break;
                case CommandResultMessage result:
                    if (int.TryParse(result.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var commandId)
                        && await db.Commands.SingleOrDefaultAsync(c => c.Id == commandId && c.StoreId == storeId, cancellationToken) is { } command)
                    {
                        command.Result = result.Ok ? "ok" : "error";
                        command.Error = result.Error;
                    }

                    if (_pending.TryGetValue(result.Id, out var completion))
                    {
                        completion.TrySetResult(result);
                    }

                    break;
                case ManifestAppliedMessage applied:
                    state.AppliedVersion = applied.Version;
                    if (state.PendingVersion <= applied.Version)
                    {
                        state.PendingVersion = null;
                    }

                    logger.LogInformation("Loja {Store} aplicou o manifesto {Version}", slug, applied.Version);
                    break;
                case AgentEventMessage agentEvent:
                    db.Events.Add(new StoreEvent { StoreId = storeId, Type = agentEvent.Event, DataJson = agentEvent.Data?.ToJsonString(), CreatedAt = now });
                    break;
                case TrackStartedMessage track:
                    state.NowPlayingHash = track.Hash;
                    state.PositionMs = 0;
                    state.PositionAt = track.At.ToUniversalTime();
                    db.PlayHistory.Add(new PlayHistory { StoreId = storeId, TrackHash = track.Hash, StartedAt = track.At.ToUniversalTime() });
                    break;
                case SpotStartedMessage spot:
                    db.OfferPlays.Add(new OfferPlay
                    {
                        StoreId = storeId,
                        OfferId = ManifestGenerator.ParseKey(spot.OfferId, "of-"),
                        PlacementId = ManifestGenerator.ParseKey(spot.PlacementId, "pl-"),
                        StartedAt = spot.At.ToUniversalTime(),
                    });
                    break;
                case SpotFinishedMessage finished:
                    var offerId = ManifestGenerator.ParseKey(finished.OfferId, "of-");
                    var play = await db.OfferPlays.Where(p => p.StoreId == storeId && p.OfferId == offerId && p.Result == null)
                        .OrderByDescending(p => p.StartedAt).FirstOrDefaultAsync(cancellationToken);
                    if (play is not null)
                    {
                        play.Result = Wire(finished.Result);
                    }

                    break;
                case SpotSkippedMessage skipped:
                    db.OfferPlays.Add(new OfferPlay
                    {
                        StoreId = storeId,
                        OfferId = ManifestGenerator.ParseKey(skipped.OfferId, "of-"),
                        PlacementId = ManifestGenerator.ParseKey(skipped.PlacementId, "pl-"),
                        StartedAt = now,
                        Result = "skipped",
                        Reason = skipped.Reason,
                    });
                    break;
                default:
                    break;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        if (announceVersion is { } version)
        {
            await NotifyManifestChangedAsync(storeId, version, cancellationToken);
        }

        MessageHandled?.Invoke(this, (storeId, message));
        if (queueChanged)
        {
            QueueChanged?.Invoke(this, storeId);
        }
    }

    private static string Wire<T>(T value)
        where T : struct, Enum => JsonSerializer.Serialize(value, ContractJson.Options).Trim('"');

    private sealed class Connection(WebSocket socket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocket Socket { get; } = socket;

        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await Socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task CloseAsync(string reason, WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
        {
            try
            {
                if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await Socket.CloseOutputAsync(status, reason, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                Socket.Abort();
            }
        }

        public void Dispose()
        {
            _sendLock.Dispose();
            Socket.Dispose();
        }
    }
}
