using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Contracts.Releases;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent.Updates;

/// <summary>Onde o agente está instalado e em que versão (D27 c).</summary>
/// <param name="PublicKeyFile">O <c>release-public-key.txt</c> da pasta do programa, onde só administradores escrevem.</param>
public sealed record UpdateEnvironment(string ProgramDirectory, string PublicKeyFile, string CurrentVersion, string ServiceName)
{
    public const string AgentServiceName = "MarumbiSom.Agent";
    public const string UpdaterFileName = "MarumbiSom.Updater.exe";

    public static UpdateEnvironment Installed(string? publicKeyFile = null) => new(
        AppContext.BaseDirectory, publicKeyFile ?? Path.Combine(AppContext.BaseDirectory, ReleaseSigning.PublicKeyFileName), HubLink.AgentVersion,
        AgentServiceName);
}

/// <summary>
/// Atualização remota do lado do agente (seção 12.2, D27 e): responde ao comando na hora, baixa o pacote em segundo
/// plano (com retomada e o limite de banda da sincronização), confere tamanho, SHA-256 e a assinatura Ed25519 com a
/// chave pública da pasta do programa e entrega a instalação ao <c>MarumbiSom.Updater</c>. O andamento vai no
/// <c>status</c>; o resultado, nos eventos <c>update.*</c>, inclusive o que o Updater deixar para o agente que subir.
/// </summary>
public sealed class AgentUpdater : BackgroundService
{
    public static readonly TimeSpan ResultCheckInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Pedido ao Updater sem resultado depois disso: o Updater morreu no meio (ex.: PC desligado).</summary>
    public static readonly TimeSpan PendingGiveUpAfter = TimeSpan.FromMinutes(15);

    public const int MaxAttempts = 5;

    /// <summary>Pacote, staging e backup cabem no disco com folga.</summary>
    public const int FreeSpaceFactor = 3;

    private readonly HubLink _link;
    private readonly IHubTransport _transport;
    private readonly UpdatePaths _paths;
    private readonly UpdateEnvironment _environment;
    private readonly IUpdaterLauncher _launcher;
    private readonly IOptions<SyncOptions> _sync;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentUpdater> _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private volatile UpdateProgress? _progress;

    public AgentUpdater(
        HubLink link, IHubTransport transport, AgentDataPaths paths, UpdateEnvironment environment, IUpdaterLauncher launcher, IOptions<SyncOptions> sync,
        TimeProvider time, ILogger<AgentUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _link = link;
        _transport = transport;
        _paths = paths.Updates;
        _environment = environment;
        _launcher = launcher;
        _sync = sync;
        _time = time;
        _logger = logger;
    }

    /// <summary>Resultado de uma atualização (<c>update.done</c>, <c>update.failed</c>, <c>update.rolledBack</c>).</summary>
    public event EventHandler<AgentEventMessage>? Reported;

    /// <summary>Andamento para o <c>status</c>; nulo sem atualização em curso.</summary>
    public UpdateProgress? Progress => _progress;

    /// <summary>Tarefa do download em curso (para os testes).</summary>
    internal Task? Running { get; private set; }

    /// <summary>Confere o pedido e começa a baixar. Devolve o motivo da recusa, ou nulo se começou.</summary>
    public string? TryStart(AgentReleaseInfo? release)
    {
        if (release is null || !ReleaseSigning.IsVersion(release.Version) || release.Sha256 is not { Length: 64 } || release.SizeBytes <= 0
            || string.IsNullOrWhiteSpace(release.Signature))
        {
            return "Pedido de atualização inválido.";
        }

        lock (_gate)
        {
            if (_progress is { } current)
            {
                return $"Já há uma atualização em andamento (versão {current.Version}).";
            }

            var (problem, report) = Check(release);
            if (problem is not null)
            {
                _logger.LogWarning("Atualização para a versão {Version} recusada: {Reason}", release.Version, problem);
                if (report)
                {
                    Report(AgentEvents.UpdateFailed, release.Version, problem);
                }

                return problem;
            }

            _logger.LogInformation("Atualização para a versão {Version} pedida pelo Hub ({Size} bytes)", release.Version, release.SizeBytes);
            _progress = new UpdateProgress(release.Version, UpdateStages.Downloading, 0, release.SizeBytes);
            Running = Task.Run(() => RunAsync(release, _stopping.Token));
            return null;
        }
    }

    /// <summary>Chamado logo depois do hello ao Hub: avisa o Updater que esta versão subiu e conversa com o Hub (D27 f).</summary>
    public void Confirm()
    {
        try
        {
            if (Read<PendingUpdate>(_paths.Pending) is { } pending && pending.Version == _environment.CurrentVersion && !File.Exists(_paths.Confirmed))
            {
                AtomicFile.WriteAllText(_paths.Confirmed, JsonSerializer.Serialize(new UpdateConfirmation(pending.Version, _time.GetUtcNow()), ContractJson.Options));
                _logger.LogInformation("Versão {Version} confirmada para o atualizador", pending.Version);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Não foi possível confirmar a versão ao atualizador: {Error}", ex.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Agente novo esperando a confirmação: o painel segue mostrando "instalando" até o resultado.
        if (Read<PendingUpdate>(_paths.Pending) is { } pending && pending.Version == _environment.CurrentVersion && !File.Exists(_paths.Result))
        {
            _progress = new UpdateProgress(pending.Version, UpdateStages.Installing, 0, 0);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            CheckResult();
            try
            {
                await Task.Delay(ResultCheckInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Resultado deixado pelo Updater (ou pedido abandonado) vira evento para o Hub.</summary>
    internal void CheckResult()
    {
        try
        {
            if (Read<UpdateResult>(_paths.Result) is { } result)
            {
                var type = result.Outcome switch
                {
                    UpdateStages.Done => AgentEvents.UpdateDone,
                    UpdateStages.RolledBack => AgentEvents.UpdateRolledBack,
                    _ => AgentEvents.UpdateFailed,
                };
                _logger.Log(
                    type == AgentEvents.UpdateDone ? LogLevel.Information : LogLevel.Error, "Atualização para a versão {Version}: {Outcome} {Error}",
                    result.Version, result.Outcome, result.Error);
                Finish(type, result.Version, result.Error, result.PreviousVersion);
                return;
            }

            if (Read<PendingUpdate>(_paths.Pending) is { } pending && _time.GetUtcNow() - pending.CreatedAt > PendingGiveUpAfter)
            {
                var installed = pending.Version == _environment.CurrentVersion;
                _logger.LogError("O atualizador não terminou a instalação da versão {Version}", pending.Version);
                Finish(
                    installed ? AgentEvents.UpdateDone : AgentEvents.UpdateFailed, pending.Version,
                    installed ? null : $"O atualizador não terminou; o agente continua na versão {_environment.CurrentVersion}.", pending.PreviousVersion);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Não foi possível ler o resultado da atualização: {Error}", ex.Message);
        }
    }

    private void Finish(string type, string version, string? error, string? previousVersion)
    {
        foreach (var file in new[] { _paths.Result, _paths.Pending, _paths.Confirmed })
        {
            File.Delete(file);
        }

        _progress = null;
        Report(type, version, error, previousVersion);
    }

    private (string? Problem, bool Report) Check(AgentReleaseInfo release)
    {
        string? key;
        try
        {
            key = ReleaseSigning.ReadPublicKey(File.ReadAllText(_environment.PublicKeyFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            key = null;
        }

        if (key is null)
        {
            return ($"Este agente não tem a chave pública de atualização ({ReleaseSigning.PublicKeyFileName}). Reinstale o agente com um pacote que traga a chave.", true);
        }

        if (!ReleaseSigning.Verify(key, release.Version, release.Sha256, release.Signature))
        {
            return ("Assinatura inválida: o pacote não foi assinado com a chave desta loja. Atualização recusada.", true);
        }

        if (ReleaseSigning.CompareVersions(release.Version, _environment.CurrentVersion) <= 0)
        {
            return ($"O agente já está na versão {_environment.CurrentVersion}. Só dá para atualizar para uma versão mais nova.", false);
        }

        var free = FreeBytes();
        if (free is { } bytes && bytes < release.SizeBytes * FreeSpaceFactor)
        {
            return ($"Pouco espaço em disco para atualizar ({bytes / (1024 * 1024)} MB livres).", true);
        }

        return (null, false);
    }

    private async Task RunAsync(AgentReleaseInfo release, CancellationToken cancellationToken)
    {
        var package = _paths.PackagePath(release.Version);
        var part = package + ".part";
        var keepPart = false;
        try
        {
            Directory.CreateDirectory(_paths.DownloadDirectory);
            foreach (var old in Directory.GetFiles(_paths.DownloadDirectory).Where(f => !string.Equals(f, part, StringComparison.OrdinalIgnoreCase)))
            {
                File.Delete(old);
            }

            try
            {
                await DownloadAsync(release, part, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                // Rede: o pedaço baixado fica para o próximo pedido continuar de onde parou.
                keepPart = true;
                throw;
            }

            _progress = new UpdateProgress(release.Version, UpdateStages.Verifying, release.SizeBytes, release.SizeBytes);
            var length = new FileInfo(part).Length;
            if (length != release.SizeBytes)
            {
                throw new UpdateException($"O pacote chegou com {length} bytes; o esperado era {release.SizeBytes}.");
            }

            // A assinatura (versão + SHA-256) foi conferida no pedido; aqui o pacote precisa ser o que foi assinado.
            var sha = await CacheManager.ComputeHashAsync(part, cancellationToken);
            if (!string.Equals(sha, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException("O pacote baixado não é o que foi assinado (SHA-256 diferente). Atualização recusada.");
            }

            File.Move(part, package, overwrite: true);
            _progress = new UpdateProgress(release.Version, UpdateStages.Installing, release.SizeBytes, release.SizeBytes);
            Install(release, sha, package);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress = null;
        }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or JsonException)
        {
            if (!keepPart)
            {
                File.Delete(part);
                File.Delete(package);
            }

            _logger.LogError("Atualização para a versão {Version} falhou: {Error}", release.Version, ex.Message);
            _progress = null;
            Report(AgentEvents.UpdateFailed, release.Version, ex is UpdateException or InvalidOperationException ? ex.Message : $"Falha ao baixar ou instalar: {ex.Message}");
        }
    }

    private void Install(AgentReleaseInfo release, string sha, string package)
    {
        File.Delete(_paths.Confirmed);
        File.Delete(_paths.Result);
        var pending = new PendingUpdate(
            release.Version, _environment.CurrentVersion, package, sha, _environment.ProgramDirectory, _environment.ServiceName, _time.GetUtcNow());
        AtomicFile.WriteAllText(_paths.Pending, JsonSerializer.Serialize(pending, ContractJson.Options));
        _logger.LogWarning("Instalando a versão {Version}: o atualizador vai parar o serviço, trocar os arquivos e iniciar de novo", release.Version);
        try
        {
            _launcher.Launch(_environment.ProgramDirectory, _paths);
        }
        catch
        {
            File.Delete(_paths.Pending);
            throw;
        }
    }

    private async Task DownloadAsync(AgentReleaseInfo release, string part, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(release, part, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is HttpRequestException or IOException or TimeoutException)
            {
                _logger.LogWarning("Download da versão {Version} interrompido ({Error}); tentativa {Attempt} de {Max}", release.Version, ex.Message, attempt + 1, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), _time, cancellationToken);
            }
        }
    }

    private async Task DownloadOnceAsync(AgentReleaseInfo release, string part, CancellationToken cancellationToken)
    {
        var hub = _link.Current ?? throw new UpdateException("A loja não está vinculada a um Hub.");
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing > release.SizeBytes)
        {
            File.Delete(part);
            existing = 0;
        }

        if (existing == release.SizeBytes)
        {
            return;
        }

        using var client = _transport.CreateClient(hub.Url, Timeout.InfiniteTimeSpan);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"agent/releases/{Uri.EscapeDataString(release.Version)}/package");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hub.Token);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(StallTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                throw new UpdateException($"O Hub não tem mais a versão {release.Version}.");
            case HttpStatusCode.Unauthorized:
                throw new UpdateException("O Hub recusou o token desta loja.");
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                File.Delete(part);
                throw new IOException("O download recomeçará do início.");
            case var code when !response.IsSuccessStatusCode:
                throw new HttpRequestException($"o Hub respondeu {(int)code} ao pedir o pacote", null, code);
            default:
                break;
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        var offset = append ? existing : 0;
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await ThrottledCopy.CopyAsync(
            body, file, _sync.Value.BandwidthKbps * 1000.0 / 8, StallTimeout, _time,
            copied =>
            {
                var done = offset + copied;
                if (done > release.SizeBytes)
                {
                    throw new UpdateException("O pacote é maior que o anunciado pelo Hub. Atualização recusada.");
                }

                _progress = new UpdateProgress(release.Version, UpdateStages.Downloading, done, release.SizeBytes);
            },
            cancellationToken);
    }

    private void Report(string type, string version, string? error, string? previousVersion = null)
    {
        var data = new JsonObject { ["version"] = version };
        if (previousVersion is not null)
        {
            data["previousVersion"] = previousVersion;
        }

        if (error is not null)
        {
            data["error"] = error;
        }

        Reported?.Invoke(this, new AgentEventMessage(type, data));
    }

    private T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Arquivo de atualização ilegível ({Path}): {Error}; apagado", path, ex.Message);
            File.Delete(path);
            return null;
        }
    }

    private long? FreeBytes()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_paths.Root));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class UpdateException(string message) : Exception(message);
}
