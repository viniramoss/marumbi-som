using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>Limite de banda dos downloads em kbit/s (seção 8.3: padrão 2 Mbps). 0 = sem limite.</summary>
    public int BandwidthKbps { get; set; } = 2000;
}

/// <summary>
/// Sincronização (seção 8.3): busca o manifesto (WebSocket avisa, polling a cada 60 s garante), grava como pendente,
/// baixa um arquivo por vez com retomada (Range) e limite de banda, confere o SHA-256 e só então promove o pendente
/// a ativo (regra 2). Também recupera arquivos do ativo que sumiram ou estavam corrompidos.
/// </summary>
public sealed class SyncEngine : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan RetryAfterBadHash = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    public const int MaxAttempts = 3;

    private readonly HubLink _link;
    private readonly IHubTransport _transport;
    private readonly ManifestStore _manifests;
    private readonly CacheManager _cache;
    private readonly AgentDataPaths _paths;
    private readonly IOptions<SyncOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncEngine> _logger;
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Attempts, DateTimeOffset RetryAt)> _failures = new(StringComparer.Ordinal);
    private volatile bool _recheckManifest = true;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _consecutiveErrors;
    private string? _lastLoggedError;

    public SyncEngine(
        HubLink link, IHubTransport transport, ManifestStore manifests, CacheManager cache, AgentDataPaths paths, IOptions<SyncOptions> options,
        TimeProvider time, ILogger<SyncEngine> logger)
    {
        _link = link;
        _transport = transport;
        _manifests = manifests;
        _cache = cache;
        _paths = paths;
        _options = options;
        _time = time;
        _logger = logger;
        link.Changed += (_, _) => Trigger();
        cache.FileRemoved += (_, _) => Wake();
    }

    /// <summary>Manifesto aplicado (para o <c>manifest.applied</c>).</summary>
    public event EventHandler<long>? Applied;

    /// <summary>Progresso do download do manifesto pendente.</summary>
    public SyncProgress? Progress { get; private set; }

    public DateTimeOffset? LastCheckAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Hub avisou versão nova (<c>manifest.changed</c>) ou a vinculação mudou.</summary>
    public void Trigger()
    {
        _recheckManifest = true;
        Wake();
    }

    /// <summary>Acorda o laço (ex.: arquivo corrompido apagado do cache precisa ser baixado de novo).</summary>
    public void Wake() => Interlocked.Exchange(ref _signal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await VerifyCacheAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            // Lido antes do trabalho: um aviso que chegar durante a sincronização acorda a próxima espera na hora.
            var signal = Volatile.Read(ref _signal).Task;
            var delay = PollInterval;
            if (_link.Current is { } hub)
            {
                try
                {
                    await SyncOnceAsync(hub, stoppingToken);
                    _consecutiveErrors = 0;
                    LastError = null;
                    _lastLoggedError = null;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TimeoutException
                    || (ex is OperationCanceledException && !stoppingToken.IsCancellationRequested))
                {
                    // Hub fora do ar ou rede caída: a loja segue tocando o que tem (regra 1).
                    LastError = ex.Message;
                    if (ex.Message != _lastLoggedError)
                    {
                        _logger.LogWarning("Sincronização com o Hub falhou: {Error}", ex.Message);
                        _lastLoggedError = ex.Message;
                    }

                    _consecutiveErrors++;
                    delay = TimeSpan.FromSeconds(Math.Min(PollInterval.TotalSeconds, 5 * Math.Pow(2, Math.Min(_consecutiveErrors - 1, 4))));
                }
            }

            try
            {
                await signal.WaitAsync(delay, _time, stoppingToken);
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

    internal async Task SyncOnceAsync(HubLinkInfo hub, CancellationToken cancellationToken)
    {
        await CheckManifestAsync(hub, cancellationToken);
        await DownloadMissingAsync(hub, cancellationToken);
        await PromoteIfCompleteAsync(hub, cancellationToken);
    }

    /// <summary>Confere o SHA-256 dos arquivos do ativo e do pendente; corrompido é apagado e baixado de novo.</summary>
    internal async Task VerifyCacheAsync(CancellationToken cancellationToken)
    {
        var hashes = Referenced().Where(_cache.Has).ToList();
        var corrupted = 0;
        foreach (var hash in hashes)
        {
            if (await _cache.VerifyAsync(hash, cancellationToken))
            {
                _verified.Add(hash);
            }
            else
            {
                corrupted++;
            }
        }

        if (hashes.Count > 0)
        {
            _logger.LogInformation("Cache conferido: {Count} arquivo(s), {Corrupted} corrompido(s)", hashes.Count, corrupted);
        }
    }

    private async Task CheckManifestAsync(HubLinkInfo hub, CancellationToken cancellationToken)
    {
        if (!_recheckManifest && LastCheckAt is { } last && _time.GetUtcNow() - last < PollInterval)
        {
            return;
        }

        // Zerado antes do pedido: um aviso que chegar durante a consulta vale para a próxima volta.
        _recheckManifest = false;
        try
        {
            await FetchManifestAsync(hub, cancellationToken);
            LastCheckAt = _time.GetUtcNow();
        }
        catch
        {
            _recheckManifest = true;
            throw;
        }
    }

    private async Task FetchManifestAsync(HubLinkInfo hub, CancellationToken cancellationToken)
    {
        var known = NewestKnownVersion(hub.StoreId);
        using var client = _transport.CreateClient(hub.Url, TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, "agent/manifest");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hub.Token);
        if (known is { } version)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("o Hub recusou o token desta loja; vincule a loja de novo na página Configurar Som");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HttpRequestException("o Hub ainda não tem configuração para esta loja; confira horários e playlists no painel");
        }

        EnsureSuccess(response, "a configuração");
        var manifest = await response.Content.ReadFromJsonAsync<Manifest>(ContractJson.Options, cancellationToken)
            ?? throw new JsonException("Manifesto vazio.");
        if (manifest.StoreId != hub.StoreId)
        {
            throw new JsonException($"Manifesto da loja {manifest.StoreId}, mas esta é {hub.StoreId}.");
        }

        if (manifest.ContentHash != ManifestHasher.ComputeContentHash(manifest))
        {
            throw new JsonException($"Manifesto versão {manifest.Version} com contentHash que não confere.");
        }

        if (known is null || manifest.Version > known)
        {
            if (_manifests.SavePending(manifest))
            {
                var missing = _cache.Missing(manifest).Count;
                _logger.LogInformation("Manifesto versão {Version} recebido; faltam {Missing} arquivo(s)", manifest.Version, missing);
            }
        }
    }

    private async Task DownloadMissingAsync(HubLinkInfo hub, CancellationToken cancellationToken)
    {
        var pending = _manifests.Pending;
        var now = _time.GetUtcNow();
        var targets = new List<(string Hash, long Size)>();
        foreach (var manifest in new[] { pending, _manifests.Active })
        {
            if (manifest is null || manifest.StoreId != hub.StoreId)
            {
                continue;
            }

            foreach (var hash in _cache.Missing(manifest))
            {
                if (targets.All(t => t.Hash != hash) && !(_failures.TryGetValue(hash, out var failure) && failure.RetryAt > now))
                {
                    targets.Add((hash, manifest.Tracks[hash].Size));
                }
            }
        }

        UpdateProgress(pending);
        foreach (var (hash, size) in targets)
        {
            if (_recheckManifest)
            {
                // Versão mais nova anunciada: troca o pendente e continua aproveitando o que já baixou (seção 8.3, item 9).
                return;
            }

            await DownloadAsync(hub, hash, size, cancellationToken);
            UpdateProgress(pending);
        }
    }

    private async Task PromoteIfCompleteAsync(HubLinkInfo hub, CancellationToken cancellationToken)
    {
        // Pendente de outra loja (vinculação trocada) nunca é aplicado; o da loja nova o substitui.
        if (_manifests.Pending is not { } pending || pending.StoreId != hub.StoreId || pending.Tracks.Keys.Any(h => !_cache.Has(h)))
        {
            return;
        }

        // Regra 2: 100% dos arquivos no cache e com SHA-256 conferido.
        foreach (var hash in pending.Tracks.Keys.Where(h => !_verified.Contains(h)))
        {
            if (!await _cache.VerifyAsync(hash, cancellationToken))
            {
                return;
            }

            _verified.Add(hash);
        }

        var promoted = _manifests.PromotePending();
        Progress = null;
        DeleteStalePartials();
        Applied?.Invoke(this, promoted.Version);
    }

    /// <summary>Downloads interrompidos de arquivos que nenhum manifesto usa mais.</summary>
    private void DeleteStalePartials()
    {
        if (!Directory.Exists(_paths.PartialDirectory))
        {
            return;
        }

        var referenced = Referenced().ToHashSet(StringComparer.Ordinal);
        foreach (var part in Directory.EnumerateFiles(_paths.PartialDirectory, "*.part"))
        {
            if (!referenced.Contains(Path.GetFileNameWithoutExtension(part)))
            {
                try
                {
                    File.Delete(part);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Não foi possível apagar {Part}", part);
                }
            }
        }
    }

    private async Task DownloadAsync(HubLinkInfo hub, string hash, long expectedSize, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.PartialDirectory);
        var part = Path.Combine(_paths.PartialDirectory, hash + ".part");
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            File.Delete(part);
            existing = 0;
        }

        if (expectedSize <= 0 || existing < expectedSize)
        {
            using var client = _transport.CreateClient(hub.Url, Timeout.InfiniteTimeSpan);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"agent/files/{hash}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hub.Token);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersTimeout.CancelAfter(StallTimeout);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _failures[hash] = (MaxAttempts, _time.GetUtcNow() + RetryAfterBadHash);
                _logger.LogError("O Hub não tem o arquivo {Hash}; nova tentativa em {Delay}", hash, RetryAfterBadHash);
                return;
            }

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(part);
                throw new IOException($"Download de {hash[..12]} recomeçará do início.");
            }

            EnsureSuccess(response, $"o arquivo {hash[..12]}");
            var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
            if (existing > 0 && !append)
            {
                _logger.LogInformation("O Hub não aceitou retomar {Hash}; baixando do início", hash[..12]);
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await CopyThrottledAsync(body, file, cancellationToken);
            }

            if (existing > 0 && append)
            {
                _logger.LogInformation("Download de {Hash} retomado a partir de {Bytes} bytes", hash[..12], existing);
            }
        }

        var actual = await CacheManager.ComputeHashAsync(part, cancellationToken);
        if (actual != hash)
        {
            File.Delete(part);
            var attempts = (_failures.TryGetValue(hash, out var failure) ? failure.Attempts : 0) + 1;
            var retryAt = attempts >= MaxAttempts ? _time.GetUtcNow() + RetryAfterBadHash : _time.GetUtcNow();
            _failures[hash] = (attempts >= MaxAttempts ? 0 : attempts, retryAt);
            if (attempts >= MaxAttempts)
            {
                _logger.LogError("Arquivo {Hash} chegou corrompido {Attempts} vezes; nova tentativa em {Delay}", hash, attempts, RetryAfterBadHash);
            }
            else
            {
                _logger.LogWarning("Arquivo {Hash} chegou com SHA-256 diferente ({Actual}); baixando de novo", hash, actual[..12]);
            }

            return;
        }

        var destination = _cache.PathFor(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(part, destination, overwrite: true);
        _verified.Add(hash);
        _failures.Remove(hash);
        _logger.LogInformation("Baixado e conferido: {Hash}", hash[..12]);
    }

    /// <summary>Copia respeitando o limite de banda; sem receber nada por 60 s, desiste (a retomada continua depois).</summary>
    private Task CopyThrottledAsync(Stream source, Stream destination, CancellationToken cancellationToken) =>
        ThrottledCopy.CopyAsync(source, destination, _options.Value.BandwidthKbps * 1000.0 / 8, StallTimeout, _time, null, cancellationToken);

    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"o Hub respondeu {(int)response.StatusCode} ao pedir {what}", null, response.StatusCode);
        }
    }

    private void UpdateProgress(Manifest? pending) =>
        Progress = pending is null ? null : new SyncProgress(pending.Tracks.Keys.Count(_cache.Has), pending.Tracks.Count);

    private long? NewestKnownVersion(string storeId)
    {
        if (_manifests.Pending is { } pending && pending.StoreId == storeId)
        {
            return pending.Version;
        }

        return _manifests.Active is { } active && active.StoreId == storeId ? active.Version : null;
    }

    private IEnumerable<string> Referenced() =>
        new[] { _manifests.Active, _manifests.Pending }.Where(m => m is not null).SelectMany(m => m!.Tracks.Keys).Distinct(StringComparer.Ordinal);
}
