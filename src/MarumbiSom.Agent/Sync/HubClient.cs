using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Sync;

/// <summary>
/// Canal ao vivo com o Hub (seções 8.2 e 11.4): WebSocket iniciado pela loja (regra 5), reconexão com espera
/// exponencial de 1 s a 60 s com variação aleatória, status a cada 30 s e logo após qualquer mudança, eventos do
/// player e comandos ao vivo. O WebSocket é atalho: sem ele o <see cref="SyncEngine"/> segue pelo polling.
/// </summary>
public sealed class HubClient : BackgroundService
{
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ChangeCheckInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);
    private const int MaxMessageBytes = 64 * 1024;

    private readonly HubLink _link;
    private readonly IHubTransport _transport;
    private readonly ManifestStore _manifests;
    private readonly SyncEngine _sync;
    private readonly PlayerEngine _player;
    private readonly IBluetoothConnection _bluetooth;
    private readonly AgentDataPaths _paths;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _time;
    private readonly ILogger<HubClient> _logger;
    private readonly Channel<AgentMessage> _outbox = Channel.CreateBounded<AgentMessage>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly long _startedAt;
    private TaskCompletionSource _linkChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HubClient(
        HubLink link, IHubTransport transport, ManifestStore manifests, SyncEngine sync, PlayerEngine player, IBluetoothConnection bluetooth,
        IPlaybackEvents events, AgentDataPaths paths, IHostApplicationLifetime lifetime, TimeProvider time, ILogger<HubClient> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        _link = link;
        _transport = transport;
        _manifests = manifests;
        _sync = sync;
        _player = player;
        _bluetooth = bluetooth;
        _paths = paths;
        _lifetime = lifetime;
        _time = time;
        _logger = logger;
        _startedAt = time.GetTimestamp();
        events.Published += (_, message) => Enqueue(message);
        sync.Applied += (_, version) => Enqueue(new ManifestAppliedMessage(version));
        link.Changed += (_, _) => Interlocked.Exchange(ref _linkChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    public bool Connected { get; private set; }

    public DateTimeOffset? ConnectedSince { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Põe uma mensagem na fila de envio. Status com o Hub desconectado é descartado (logo fica velho).</summary>
    public void Enqueue(AgentMessage message)
    {
        if (message is StatusMessage && !Connected)
        {
            return;
        }

        _outbox.Writer.TryWrite(message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var linkChanged = Volatile.Read(ref _linkChanged).Task;
            var hub = _link.Current;
            var delay = TimeSpan.FromMinutes(5);
            if (hub is not null)
            {
                var connectedAt = _time.GetTimestamp();
                try
                {
                    await RunSessionAsync(hub, linkChanged, stoppingToken);
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or JsonException or TimeoutException
                    || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
                {
                    if (ex.Message != LastError)
                    {
                        _logger.LogWarning("Canal ao vivo com o Hub indisponível: {Error}", ex.Message);
                    }

                    LastError = ex.Message;
                }
                finally
                {
                    if (Connected)
                    {
                        _logger.LogInformation("Desconectado do Hub");
                    }

                    Connected = false;
                    ConnectedSince = null;
                }

                if (_time.GetElapsedTime(connectedAt) > MaxBackoff)
                {
                    backoff = TimeSpan.FromSeconds(1);
                }

                // Espera exponencial com ±20% de variação, para as lojas não reconectarem todas juntas.
                delay = backoff * (0.8 + (Random.Shared.NextDouble() * 0.4));
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }

            try
            {
                await linkChanged.WaitAsync(delay, _time, stoppingToken);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunSessionAsync(HubLinkInfo hub, Task linkChanged, CancellationToken stoppingToken)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectTimeout.CancelAfter(ConnectTimeout);
        using var socket = await _transport.ConnectAsync(SocketUri(hub.Url), hub.Token, connectTimeout.Token);
        Connected = true;
        ConnectedSince = _time.GetUtcNow();
        LastError = null;
        _logger.LogInformation("Conectado ao Hub {Url} como {Store}", hub.Url, hub.StoreId);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var active = _manifests.Active;
        await SendAsync(socket, new HelloMessage(hub.StoreId, HubLink.AgentVersion, active?.StoreId == hub.StoreId ? active.Version : 0), session.Token);
        await SendAsync(socket, BuildStatus(), session.Token);
        var tasks = new[]
        {
            ReceiveLoopAsync(socket, session.Token),
            SendLoopAsync(socket, session.Token),
            TelemetryLoopAsync(session.Token),
            linkChanged.WaitAsync(session.Token),
        };
        var finished = await Task.WhenAny(tasks);
        await session.CancelAsync();
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agente encerrando", closeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            socket.Abort();
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception) when (session.IsCancellationRequested)
        {
            // Os outros laços terminam cancelados; o motivo real vem da tarefa que terminou primeiro.
        }

        await finished;
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxMessageBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    if (length >= MaxMessageBytes)
                    {
                        throw new WebSocketException("Mensagem do Hub grande demais.");
                    }

                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, MaxMessageBytes - length), cancellationToken);
                    length += result.Count;
                }
                while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException($"O Hub fechou a conexão: {result.CloseStatusDescription}");
                }

                HubMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<HubMessage>(buffer.AsSpan(0, length), ContractJson.Options);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Mensagem do Hub ignorada: {Error}", ex.Message);
                    continue;
                }

                switch (message)
                {
                    case ManifestChangedMessage changed:
                        _logger.LogInformation("Hub avisou manifesto novo: versão {Version}", changed.Version);
                        _sync.Trigger();
                        break;
                    case CommandMessage command:
                        _ = HandleCommandAsync(command, cancellationToken);
                        break;
                    default:
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await SendAsync(socket, message, cancellationToken);
            }
            catch
            {
                // Não se perde a mensagem que estava saindo (ex.: track.started) quando a conexão cai.
                if (message is not StatusMessage)
                {
                    _outbox.Writer.TryWrite(message);
                }

                throw;
            }
        }
    }

    private async Task TelemetryLoopAsync(CancellationToken cancellationToken)
    {
        var lastKey = StatusKey(BuildStatus());
        var lastSent = _time.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(ChangeCheckInterval, _time, cancellationToken);
            var status = BuildStatus();
            var key = StatusKey(status);
            if (key != lastKey || _time.GetElapsedTime(lastSent) >= StatusInterval)
            {
                Enqueue(status);
                lastKey = key;
                lastSent = _time.GetTimestamp();
            }
        }
    }

    internal async Task HandleCommandAsync(CommandMessage command, CancellationToken cancellationToken)
    {
        PlayerCommandResult result;
        try
        {
            switch (command.Command)
            {
                case CommandTypes.ReconnectBluetooth:
                    _bluetooth.RequestReconnect();
                    result = PlayerCommandResult.Success;
                    break;
                case CommandTypes.RestartAgent:
                    Enqueue(new CommandResultMessage(command.Id, true));
                    _logger.LogWarning("Reiniciando o agente a pedido do Hub");
                    await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken);
                    // Código de saída diferente de zero: a recuperação do serviço (seção 8.1) sobe o agente de novo.
                    Environment.ExitCode = 1;
                    _lifetime.StopApplication();
                    return;
                case CommandTypes.UpdateAgent:
                    result = PlayerCommandResult.Fail("Atualização remota ainda não disponível nesta versão.");
                    break;
                default:
                    var playerCommand = PlayerCommand.FromHub(command);
                    if (playerCommand is null)
                    {
                        result = PlayerCommandResult.Fail($"Comando desconhecido: {command.Command}");
                        break;
                    }

                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeout.CancelAfter(CommandTimeout);
                        result = await _player.ExecuteAsync(playerCommand, timeout.Token);
                    }

                    break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = PlayerCommandResult.Fail("O player não respondeu a tempo.");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            result = PlayerCommandResult.Fail(ex.Message);
        }

        _logger.LogInformation("Comando do Hub {Command}: {Result}", command.Command, result.Ok ? "ok" : result.Error);
        Enqueue(new CommandResultMessage(command.Id, result.Ok, result.Error));
    }

    internal StatusMessage BuildStatus()
    {
        var snapshot = _player.Snapshot;
        return new StatusMessage
        {
            Player = snapshot.Status,
            NowPlaying = snapshot.NowPlayingHash,
            PositionMs = (long)((snapshot.PositionS ?? 0) * 1000),
            Volume = snapshot.Volume,
            Bluetooth = _bluetooth.Status,
            PendingVersion = _manifests.Pending?.Version,
            SyncProgress = _sync.Progress,
            DiskFreeMb = DiskFreeMb(),
            UptimeS = (long)_time.GetElapsedTime(_startedAt).TotalSeconds,
        };
    }

    private static string StatusKey(StatusMessage status) =>
        $"{status.Player}|{status.NowPlaying}|{status.Volume}|{status.Bluetooth}|{status.PendingVersion}|{status.SyncProgress?.Done}/{status.SyncProgress?.Total}";

    private long DiskFreeMb()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.Root));
            return root is null ? 0 : new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static Task SendAsync(WebSocket socket, AgentMessage message, CancellationToken cancellationToken) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    internal static Uri SocketUri(Uri hubUrl)
    {
        ArgumentNullException.ThrowIfNull(hubUrl);
        var builder = new UriBuilder(new Uri(hubUrl, "agent/ws"))
        {
            Scheme = hubUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return builder.Uri;
    }
}
