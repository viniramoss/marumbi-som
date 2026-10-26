using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Library;
using MarumbiSom.Hub.Manifests;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// WebSocket do painel (<c>/ws/panel</c>, D23 d): envia o resumo de uma loja sempre que ele muda (status, conexão,
/// versão nova) e avisos de tópicos alterados para o painel recarregar só o que precisa. A cada 30 s os resumos são
/// recalculados para os alertas por tempo (seção 6.6) aparecerem mesmo sem mensagem nova da loja.
/// </summary>
public sealed class PanelHub(
    IServiceScopeFactory scopes,
    AgentConnections agents,
    ManifestPublisher publisher,
    IngestionWorker ingestion,
    StoreStatusReader status,
    TimeProvider time,
    ILogger<PanelHub> logger) : BackgroundService
{
    public const string Topics = "topics";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private readonly ConcurrentDictionary<int, string> _lastSent = new();
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });

    public int ClientCount => _clients.Count;

    /// <summary>Recalcula e envia o resumo da loja.</summary>
    public void StoreChanged(int storeId) => _signals.Writer.TryWrite(new Signal(storeId, []));

    /// <summary>Avisa que dados de um ou mais tópicos mudaram (ex.: <c>tracks</c>, <c>playlists</c>, <c>stores</c>).</summary>
    public void Changed(params string[] topics) => _signals.Writer.TryWrite(new Signal(null, topics));

    /// <summary>Mantém a conexão do painel aberta até ele fechar; o painel não envia nada além do fechamento.</summary>
    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var id = Guid.NewGuid();
        var client = new Client(socket);
        _clients[id] = client;
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fechada pelo painel", CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            logger.LogDebug("Conexão do painel encerrada: {Reason}", ex.Message);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agents.MessageHandled += OnAgentMessage;
        agents.QueueChanged += OnQueueChanged;
        agents.ConnectionChanged += OnConnectionChanged;
        publisher.Published += OnPublished;
        ingestion.Changed += OnIngestionChanged;
        try
        {
            using var refresh = new PeriodicTimer(RefreshInterval, time);
            var tick = refresh.WaitForNextTickAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                var read = _signals.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var done = await Task.WhenAny(read, tick);
                if (done == tick)
                {
                    await tick;
                    tick = refresh.WaitForNextTickAsync(stoppingToken).AsTask();
                    if (!_clients.IsEmpty)
                    {
                        await RunSafelyAsync(() => SendStoresAsync(null, onlyIfChanged: true, stoppingToken));
                    }

                    continue;
                }

                if (!await read)
                {
                    break;
                }

                // Junta os sinais que chegaram de uma vez (ex.: status + track.started) num envio só.
                var stores = new HashSet<int>();
                var topics = new HashSet<string>(StringComparer.Ordinal);
                while (_signals.Reader.TryRead(out var signal))
                {
                    if (signal.StoreId is { } storeId)
                    {
                        stores.Add(storeId);
                    }

                    topics.UnionWith(signal.Topics);
                }

                if (_clients.IsEmpty)
                {
                    _lastSent.Clear();
                    continue;
                }

                if (stores.Count > 0)
                {
                    await RunSafelyAsync(() => SendStoresAsync(stores, onlyIfChanged: false, stoppingToken));
                }

                if (topics.Count > 0)
                {
                    await BroadcastAsync(new PanelChangedMessage([.. topics.Order(StringComparer.Ordinal)]), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            agents.MessageHandled -= OnAgentMessage;
            agents.QueueChanged -= OnQueueChanged;
            agents.ConnectionChanged -= OnConnectionChanged;
            publisher.Published -= OnPublished;
            ingestion.Changed -= OnIngestionChanged;
        }
    }

    public override void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        base.Dispose();
    }

    private void OnAgentMessage(object? sender, (int StoreId, AgentMessage Message) e)
    {
        StoreChanged(e.StoreId);
        var topic = e.Message switch
        {
            AgentEventMessage => $"events:{e.StoreId}",
            TrackStartedMessage => $"history:{e.StoreId}",
            StatusMessage => null,
            _ => null,
        };
        if (topic is not null)
        {
            Changed(topic);
        }
    }

    private void OnConnectionChanged(object? sender, int storeId) => StoreChanged(storeId);

    // A fila só vem no detalhe da loja (GET /api/stores/{id}), não no resumo enviado a cada status.
    private void OnQueueChanged(object? sender, int storeId) => Changed($"store:{storeId}");

    private void OnPublished(object? sender, PublishedManifest e) => StoreChanged(e.StoreId);

    private void OnIngestionChanged(object? sender, IngestionJob job) =>
        Changed(job.Status is IngestionStatus.Done ? ["uploads", "tracks"] : ["uploads"]);

    private async Task SendStoresAsync(HashSet<int>? storeIds, bool onlyIfChanged, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var summaries = storeIds is { Count: 1 }
            ? await status.GetAsync(db, storeIds.Single(), cancellationToken) is { } one ? [one] : []
            : (await status.ListAsync(db, cancellationToken)).Where(s => storeIds is null || storeIds.Contains(s.Id)).ToList();
        foreach (var summary in summaries)
        {
            var json = JsonSerializer.Serialize(summary, HubJson.Options);
            if (onlyIfChanged && _lastSent.TryGetValue(summary.Id, out var previous) && previous == json)
            {
                continue;
            }

            _lastSent[summary.Id] = json;
            await BroadcastAsync(new PanelStoreMessage(summary), cancellationToken);
        }
    }

    private async Task BroadcastAsync(PanelMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, HubJson.Options);
        foreach (var (id, client) in _clients)
        {
            try
            {
                await client.SendAsync(payload, cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException)
            {
                logger.LogDebug("Falha ao enviar ao painel: {Error}", ex.Message);
                _clients.TryRemove(id, out _);
            }
        }
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao atualizar o painel ao vivo");
        }
    }

    private sealed record Signal(int? StoreId, string[] Topics);

    private sealed class Client(WebSocket socket) : IDisposable
    {
        private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                // Um navegador travado não segura os envios para os outros.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SendTimeout);
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}

/// <summary>Mensagens do <c>/ws/panel</c>; o esquema vai para o OpenAPI para o painel ter os tipos gerados.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PanelStoreMessage), "store")]
[JsonDerivedType(typeof(PanelChangedMessage), "changed")]
public abstract record PanelMessage;

/// <summary>Resumo atualizado de uma loja.</summary>
public sealed record PanelStoreMessage(StoreSummary Store) : PanelMessage;

/// <summary>Tópicos com dados novos: <c>stores</c>, <c>tracks</c>, <c>uploads</c>, <c>playlists</c>, <c>events:{id}</c>, <c>history:{id}</c>.</summary>
public sealed record PanelChangedMessage(IReadOnlyList<string> Topics) : PanelMessage;
