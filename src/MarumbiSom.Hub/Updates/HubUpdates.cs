using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using MarumbiSom.Hub.Panel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Hub.Updates;

/// <summary>
/// Atualização automática do Hub (D32). O Hub guarda o pacote dele já conferido em <c>data\updates\download</c>
/// (baixado do GitHub ou enviado no painel), pede a troca gravando <c>data\updates\pending.json</c> (no botão ou às
/// 03:00) e acompanha pelo <c>updater\state.json</c> e <c>updater\result.json</c> que o Updater grava. A troca em si
/// é da tarefa agendada como SYSTEM: o Hub não para o próprio serviço nem grava em <c>app\</c>. Antes de abrir o
/// banco, a versão nova guarda uma cópia dele e, se a versão voltar, a anterior devolve essa cópia.
/// </summary>
public sealed partial class HubUpdates : BackgroundService
{
    public const string Topic = "hub";
    public const string AutoKey = "hub.update.auto";
    public const string LastKey = "hub.update.last";

    /// <summary>Sem o atualizador pegar o pedido nesse tempo, o painel avisa que a tarefa do Windows pode não existir.</summary>
    public static readonly TimeSpan UpdaterLateAfter = TimeSpan.FromMinutes(3);

    /// <summary>O mesmo prazo do Updater (<c>HubUpdateRunner.MaxRequestAge</c>): depois disso o pedido é desfeito.</summary>
    public static readonly TimeSpan RequestExpiresAfter = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan AutoWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(1);
    public const int DatabaseBackupsKept = 3;

    /// <summary>O que o pacote precisa ter para a tarefa instalar e para a próxima atualização funcionar (D32 a).</summary>
    private static readonly string[] RequiredFiles =
        [HubUpdatePaths.HubExe, HubUpdatePaths.HubDll, "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName, "wwwroot/index.html"];

    private static readonly JsonSerializerOptions Indented = new(ContractJson.Options) { WriteIndented = true };

    private readonly IServiceScopeFactory _scopes;
    private readonly HubPaths _paths;
    private readonly IOptions<HubOptions> _options;
    private readonly PanelHub _panel;
    private readonly TimeProvider _time;
    private readonly ILogger<HubUpdates> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly Lock _gate = new();
    private GitHubDownload? _download;
    private string? _message;
    private bool _problem;
    private HubUpdateRequest? _confirm;

    public HubUpdates(
        IServiceScopeFactory scopes, HubPaths paths, IOptions<HubOptions> options, PanelHub panel, IHostApplicationLifetime lifetime, TimeProvider time,
        ILogger<HubUpdates> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lifetime);
        _scopes = scopes;
        _paths = paths;
        _options = options;
        _panel = panel;
        _time = time;
        _logger = logger;
        Files = new HubUpdatePaths(Path.GetDirectoryName(paths.Data) ?? paths.Data);
        // A tarefa do atualizador conhece só a instalação padrão (C:\MarumbiSom\Hub com app, data e updater).
        Supported = string.Equals(Files.Data, paths.Data, StringComparison.OrdinalIgnoreCase);
        lifetime.ApplicationStarted.Register(Confirm);
    }

    /// <summary>"0.6.41" (sem o commit que o SDK acrescenta à versão informativa).</summary>
    public static string CurrentVersion { get; } = ReleaseSigning.ShortVersion(
        typeof(HubUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HubUpdates).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public HubUpdatePaths Files { get; }

    public bool Supported { get; }

    /// <summary>
    /// Antes de abrir o banco (<see cref="HubApp.InitializeDatabaseAsync"/>): se o Updater voltou a versão, devolve a
    /// cópia do banco feita pela versão que não subiu; se esta é a versão nova subindo pelo Updater, guarda a cópia e
    /// confirma depois de iniciar.
    /// </summary>
    public void PrepareBeforeDatabase()
    {
        if (!Supported || ReadPending() is not { } pending)
        {
            return;
        }

        if (ReadUpdaterResult() is { } result && result.RequestId == pending.RequestId)
        {
            var backup = Files.DatabaseBackup(pending.RequestId);
            if (result.Outcome == UpdateStages.RolledBack && pending.Version != CurrentVersion && Directory.Exists(backup))
            {
                RestoreDatabase(backup);
                _logger.LogWarning("A versão {Version} do Hub não subiu e o atualizador voltou para a {Current}; banco devolvido para a cópia de antes da atualização", pending.Version, CurrentVersion);
            }

            return;
        }

        if (pending.Version == CurrentVersion && ReadUpdaterState() is { Stage: HubUpdateStages.Confirming } state && state.RequestId == pending.RequestId)
        {
            BackupDatabase(pending.RequestId);
            _confirm = pending;
            _logger.LogInformation("Versão {Version} do Hub subindo pelo atualizador; cópia do banco guardada", CurrentVersion);
        }
    }

    public async Task<HubUpdateStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var auto = await AutoAsync(cancellationToken);
        var last = await LastAsync(cancellationToken);
        HubUpdatePendingInfo? pending = null;
        if (Supported && ReadPending() is { } request)
        {
            var state = ReadUpdaterState();
            var stage = state is not null && state.RequestId == request.RequestId ? state.Stage : HubUpdatePendingInfo.Waiting;
            pending = new HubUpdatePendingInfo(
                request.Version, request.CreatedAt, request.Automatic, stage,
                stage == HubUpdatePendingInfo.Waiting && _time.GetUtcNow() - request.CreatedAt > UpdaterLateAfter);
        }

        var available = Supported ? Available() : null;
        lock (_gate)
        {
            return new HubUpdateStatus(
                CurrentVersion, Supported, Supported && File.Exists(Files.UpdaterExe),
                available is null ? null : new HubPackageInfo(available.Version, available.SizeBytes, available.DownloadedAt),
                _download, _message, _problem, auto, _options.Value.HubUpdateTime, pending, last);
        }
    }

    /// <summary>Pede a troca para a versão baixada (botão ou madrugada). Devolve o motivo se não pedir.</summary>
    public async Task<string?> RequestAsync(bool automatic, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        if (!Supported)
        {
            return NotSupported;
        }

        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            if (ReadPending() is not null)
            {
                return "Já há uma atualização do Hub pedida.";
            }

            if (Available() is not { } package)
            {
                return "Nenhuma versão nova do Hub baixada ainda.";
            }

            // O arquivo pode ter mudado no disco desde que foi baixado.
            var sha = await ShaAsync(package.ZipPath, cancellationToken);
            if (!string.Equals(sha, package.Signature.Sha256, StringComparison.OrdinalIgnoreCase)
                || ReadPublicKey() is not { } publicKey
                || !ReleaseSigning.Verify(publicKey, package.Version, sha, package.Signature.Signature, ReleaseProducts.Hub))
            {
                DeletePackages();
                return "O pacote baixado não confere mais com a assinatura e foi apagado; o Hub baixa de novo na próxima procura.";
            }

            var request = new HubUpdateRequest(
                Guid.NewGuid().ToString("N"), package.Version, CurrentVersion, sha, package.Signature.Signature, _time.GetUtcNow(), automatic);
            File.Delete(Files.Confirmed);
            WriteJson(Files.Pending, request);
            await AuditAsync(user, "hub.update.request", package.Version, new { automatic }, cancellationToken);
            _logger.LogInformation("Atualização do Hub para a versão {Version} pedida ({Who})", package.Version, automatic ? "madrugada" : "painel");
            _panel.Changed(Topic);
            return null;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>Desfaz o pedido enquanto o atualizador ainda não começou.</summary>
    public async Task<string?> CancelAsync(ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        if (!Supported || ReadPending() is not { } pending)
        {
            return "Não há atualização do Hub pedida.";
        }

        if (ReadUpdaterState() is { } state && state.RequestId == pending.RequestId)
        {
            return "O atualizador já começou a instalar; espere terminar.";
        }

        File.Delete(Files.Pending);
        await AuditAsync(user, "hub.update.cancel", pending.Version, null, cancellationToken);
        _panel.Changed(Topic);
        return null;
    }

    public async Task SetAutoAsync(bool enabled, ClaimsPrincipal? user, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        await SaveSettingAsync(db, AutoKey, enabled ? "true" : "false", cancellationToken);
        db.Audit(user, _time, "hub.update.auto", null, new { enabled });
        await db.SaveChangesAsync(cancellationToken);
        _panel.Changed(Topic);
    }

    /// <summary>
    /// Guarda um pacote do Hub conferindo tamanho, SHA-256, assinatura e conteúdo; substitui o que já estava baixado.
    /// Usado pela procura no GitHub e pelo envio no painel.
    /// </summary>
    public async Task<(HubPackageInfo? Package, string? Problem)> ImportAsync(
        Stream content, ReleaseSignature signature, Action<long>? progress, ClaimsPrincipal? user, string auditAction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(signature);
        if (!Supported)
        {
            return (null, NotSupported);
        }

        if (ReadPending() is not null)
        {
            return (null, "Há uma atualização do Hub em andamento; espere terminar.");
        }

        if (ReleaseSigning.CompareVersions(signature.Version, CurrentVersion) <= 0)
        {
            return (null, $"A versão {signature.Version} do Hub não é mais nova que a instalada ({CurrentVersion}).");
        }

        if (ReadPublicKey() is not { } publicKey)
        {
            return (null, $"O Hub não tem a chave pública das atualizações ({ReleaseSigning.PublicKeyFileName}); reinstale com o install-hub.ps1.");
        }

        Directory.CreateDirectory(Files.DownloadDirectory);
        var temp = Path.Combine(Files.DownloadDirectory, $".baixando-{Guid.NewGuid():N}.zip");
        try
        {
            string sha;
            long size = 0;
            await using (var target = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > ReleasesEndpoints.MaxPackageBytes)
                    {
                        return (null, "Pacote maior que 300 MB.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Invoke(size);
                }

                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(sha, signature.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "O pacote não confere com a assinatura (SHA-256 diferente). Use o .zip e o .sig.json gerados juntos.");
            }

            if (!ReleaseSigning.Verify(publicKey, signature.Version, sha, signature.Signature, ReleaseProducts.Hub))
            {
                return (null, "Assinatura inválida: o pacote não foi assinado pela chave das atualizações do Hub.");
            }

            if (CheckContents(temp) is { } problem)
            {
                return (null, problem);
            }

            DeletePackages();
            File.Move(temp, Files.PackagePath(signature.Version));
            WriteJson(Files.SignaturePath(signature.Version), new ReleaseSignature(signature.Version, sha, signature.Signature.Trim()));
            await AuditAsync(user, auditAction, signature.Version, new { sha256 = sha, sizeBytes = size }, cancellationToken);
            _logger.LogInformation("Versão {Version} do Hub guardada e conferida ({Size} bytes)", signature.Version, size);
            _panel.Changed(Topic);
            return (new HubPackageInfo(signature.Version, size, _time.GetUtcNow()), null);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Versão do Hub já baixada e conferida (para a procura no GitHub não baixar de novo).</summary>
    public string? AvailableVersion => Supported ? Available()?.Version : null;

    /// <summary>Há um pedido de troca ainda sem resultado.</summary>
    public bool UpdateInProgress => Supported && ReadPending() is not null;

    public void SetDownload(GitHubDownload? download)
    {
        lock (_gate)
        {
            _download = download;
        }

        _panel.Changed(Topic);
    }

    /// <summary>Resultado da última procura do pacote do Hub no GitHub.</summary>
    public void SetMessage(string? message, bool problem)
    {
        lock (_gate)
        {
            _message = message;
            _problem = problem;
        }

        _panel.Changed(Topic);
    }

    /// <summary>Uma volta do acompanhamento: resultado do Updater, pedido vencido e a atualização da madrugada.</summary>
    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            await TickOnceAsync(cancellationToken);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    private async Task TickOnceAsync(CancellationToken cancellationToken)
    {
        if (ReadPending() is { } pending)
        {
            if (ReadUpdaterResult() is { } result && result.RequestId == pending.RequestId)
            {
                await CompleteAsync(pending, result, cancellationToken);
            }
            else if (ReadUpdaterState() is not { } state || state.RequestId != pending.RequestId)
            {
                if (_time.GetUtcNow() - pending.CreatedAt > RequestExpiresAfter)
                {
                    await CompleteAsync(pending, new HubUpdateResult(
                        pending.RequestId, pending.Version, CurrentVersion, UpdateStages.Failed,
                        "O atualizador do Windows não pegou o pedido em 30 minutos. Rode uma vez, como administrador, o install-hub.ps1 desta versão (ele cria a tarefa do atualizador) e peça de novo.",
                        _time.GetUtcNow()), cancellationToken);
                }
            }

            return;
        }

        // Madrugada: a versão nova já conferida, uma vez por versão (uma que falhou só vai pelo botão).
        if (await AutoAsync(cancellationToken) && Available() is { } package && InAutoWindow()
            && (await LastAsync(cancellationToken))?.Version != package.Version)
        {
            if (await RequestAsync(automatic: true, user: null, cancellationToken) is { } problem)
            {
                _logger.LogWarning("Atualização automática do Hub não pedida: {Problem}", problem);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Supported)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao acompanhar a atualização do Hub");
            }

            try
            {
                await Task.Delay(ReadPending() is null ? IdleInterval : BusyInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override void Dispose()
    {
        _requestLock.Dispose();
        _tickLock.Dispose();
        base.Dispose();
    }

    private const string NotSupported = "A atualização automática do Hub só funciona na instalação padrão (install-hub.ps1, em C:\\MarumbiSom\\Hub).";

    private async Task CompleteAsync(HubUpdateRequest pending, HubUpdateResult result, CancellationToken cancellationToken)
    {
        var last = new HubUpdateLastInfo(
            pending.Version, string.IsNullOrEmpty(result.PreviousVersion) ? pending.PreviousVersion : result.PreviousVersion,
            result.Outcome, result.Error, result.At, pending.Automatic);
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            await SaveSettingAsync(db, LastKey, JsonSerializer.Serialize(last, HubJson.Options), cancellationToken);
            db.Audit(null, _time, $"hub.update.{result.Outcome}", pending.Version, new { result.Error, pending.Automatic });
            await db.SaveChangesAsync(cancellationToken);
        }

        File.Delete(Files.Pending);
        File.Delete(Files.Confirmed);
        if (result.Outcome == UpdateStages.Done)
        {
            Cleanup();
            _logger.LogInformation("Hub atualizado de {Previous} para {Version}", last.PreviousVersion, pending.Version);
        }
        else
        {
            _logger.LogWarning("Atualização do Hub para a versão {Version}: {Outcome} ({Error})", pending.Version, result.Outcome, result.Error);
        }

        _panel.Changed(Topic);
    }

    /// <summary>Depois de instalada: some o pacote (versão igual ou mais antiga) e ficam só as últimas cópias do banco.</summary>
    private void Cleanup()
    {
        try
        {
            if (Available() is null)
            {
                DeletePackages();
            }

            if (Directory.Exists(Files.DatabaseBackups))
            {
                foreach (var old in new DirectoryInfo(Files.DatabaseBackups).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(DatabaseBackupsKept))
                {
                    old.Delete(recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Arquivos da atualização do Hub não apagados: {Error}", ex.Message);
        }
    }

    private void Confirm()
    {
        if (_confirm is not { } request)
        {
            return;
        }

        try
        {
            WriteJson(Files.Confirmed, new HubUpdateConfirmation(request.RequestId, request.Version, _time.GetUtcNow()));
            _logger.LogInformation("Versão {Version} do Hub confirmada ao atualizador", request.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Não foi possível confirmar a versão nova ao atualizador; ele vai voltar a anterior");
        }
    }

    /// <summary>Banco, WAL e índice compartilhado juntos, com o banco ainda fechado (nenhum Hub rodando).</summary>
    private void BackupDatabase(string requestId)
    {
        var target = Files.DatabaseBackup(requestId);
        if (Directory.Exists(target))
        {
            return;
        }

        var temp = target + ".tmp";
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, recursive: true);
        }

        Directory.CreateDirectory(temp);
        foreach (var file in DatabaseFiles(_paths.Database).Where(File.Exists))
        {
            File.Copy(file, Path.Combine(temp, Path.GetFileName(file)));
        }

        Directory.Move(temp, target);
    }

    private void RestoreDatabase(string backup)
    {
        foreach (var file in DatabaseFiles(_paths.Database))
        {
            File.Delete(file);
        }

        foreach (var file in Directory.GetFiles(backup))
        {
            File.Copy(file, Path.Combine(_paths.Data, Path.GetFileName(file)));
        }

        // Uma vez só: um reinício depois disso não pode jogar fora o que mudou no banco desde a volta.
        Directory.Delete(backup, recursive: true);
    }

    private static string[] DatabaseFiles(string database) => [database, database + "-wal", database + "-shm"];

    private bool InAutoWindow()
    {
        var local = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), ManifestGenerator.ResolveZone(_options.Value.Timezone));
        var start = _options.Value.HubUpdateTime.ToTimeSpan();
        var offset = local.TimeOfDay - start;
        if (offset < TimeSpan.Zero)
        {
            offset += TimeSpan.FromDays(1);
        }

        return offset < AutoWindow;
    }

    private StoredPackage? Available()
    {
        if (!Directory.Exists(Files.DownloadDirectory))
        {
            return null;
        }

        return Directory.GetFiles(Files.DownloadDirectory, "MarumbiSom.Hub-*.zip")
            .Select(path => (Path: path, Match: PackageName().Match(Path.GetFileName(path))))
            .Where(x => x.Match.Success && ReleaseSigning.CompareVersions(x.Match.Groups[1].Value, CurrentVersion) > 0)
            .Select(x => (x.Path, Version: x.Match.Groups[1].Value, Signature: ReadSignature(Files.SignaturePath(x.Match.Groups[1].Value))))
            .Where(x => x.Signature is not null && x.Signature.Version == x.Version)
            .OrderByDescending(x => x.Version, Comparer<string>.Create(ReleaseSigning.CompareVersions))
            .Select(x =>
            {
                var file = new FileInfo(x.Path);
                return new StoredPackage(x.Version, x.Path, x.Signature!, file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .FirstOrDefault();
    }

    private void DeletePackages()
    {
        if (!Directory.Exists(Files.DownloadDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(Files.DownloadDirectory, "MarumbiSom.Hub-*"))
        {
            File.Delete(file);
        }
    }

    private string? ReadPublicKey()
    {
        var file = string.IsNullOrWhiteSpace(_options.Value.UpdatePublicKeyFile)
            ? Path.Combine(AppContext.BaseDirectory, ReleaseSigning.PublicKeyFileName)
            : _options.Value.UpdatePublicKeyFile;
        return File.Exists(file) ? ReleaseSigning.ReadPublicKey(File.ReadAllText(file)) : null;
    }

    /// <summary>O .zip abre, tem o que a tarefa precisa e nenhum caminho que saia da pasta.</summary>
    private static string? CheckContents(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Any(e => e.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(e.FullName)))
            {
                return "O pacote tem caminhos inválidos.";
            }

            var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = RequiredFiles.Where(f => !names.Contains(f)).ToList();
            return missing.Count == 0 ? null : $"O pacote não tem {string.Join(", ", missing)}. Use o pacote do Hub gerado pelo CI ou pelo sign-release -HubDir.";
        }
        catch (InvalidDataException)
        {
            return "O arquivo enviado não é um .zip válido.";
        }
    }

    private HubUpdateRequest? ReadPending() => ReadJson<HubUpdateRequest>(Files.Pending);

    private HubUpdateState? ReadUpdaterState() => ReadJson<HubUpdateState>(Files.State);

    private HubUpdateResult? ReadUpdaterResult() => ReadJson<HubUpdateResult>(Files.Result);

    private static ReleaseSignature? ReadSignature(string path) => ReadJson<ReleaseSignature>(path);

    private static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Indented), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<string> ShaAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private async Task<bool> AutoAsync(CancellationToken cancellationToken) =>
        await ReadSettingAsync(AutoKey, cancellationToken) is not "false";

    private async Task<HubUpdateLastInfo?> LastAsync(CancellationToken cancellationToken)
    {
        var text = await ReadSettingAsync(LastKey, cancellationToken);
        try
        {
            return text is null ? null : JsonSerializer.Deserialize<HubUpdateLastInfo>(text, HubJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(cancellationToken);
    }

    private static async Task SaveSettingAsync(HubDb db, string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
    }

    private async Task AuditAsync(ClaimsPrincipal? user, string action, string target, object? data, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        db.Audit(user, _time, action, target, data);
        await db.SaveChangesAsync(cancellationToken);
    }

    [GeneratedRegex(@"^MarumbiSom\.Hub-(\d+\.\d+\.\d+)\.zip$")]
    private static partial Regex PackageName();

    private sealed record StoredPackage(string Version, string ZipPath, ReleaseSignature Signature, long SizeBytes, DateTimeOffset DownloadedAt);
}

/// <param name="Supported">Instalação padrão, em que a tarefa do atualizador funciona.</param>
/// <param name="UpdaterInstalled">O <c>install-hub.ps1</c> desta geração já rodou (há a cópia do Updater em <c>updater\</c>).</param>
/// <param name="Available">Versão nova já baixada e conferida, pronta para instalar.</param>
/// <param name="Download">Andamento do download do GitHub, enquanto baixa.</param>
/// <param name="Message">Resultado da última procura do pacote do Hub no GitHub.</param>
/// <param name="AutoUpdateTime">Hora local da atualização automática.</param>
public sealed record HubUpdateStatus(
    string CurrentVersion, bool Supported, bool UpdaterInstalled, HubPackageInfo? Available, GitHubDownload? Download, string? Message, bool Problem,
    bool AutoUpdate, TimeOnly AutoUpdateTime, HubUpdatePendingInfo? Pending, HubUpdateLastInfo? Last);

public sealed record HubPackageInfo(string Version, long SizeBytes, DateTimeOffset DownloadedAt);

/// <param name="Stage"><c>waiting</c> até o atualizador pegar o pedido; depois uma das etapas de <see cref="HubUpdateStages"/>.</param>
/// <param name="UpdaterLate">Esperando há mais de 3 minutos: a tarefa do atualizador provavelmente não existe.</param>
public sealed record HubUpdatePendingInfo(string Version, DateTimeOffset RequestedAt, bool Automatic, string Stage, bool UpdaterLate)
{
    public const string Waiting = "waiting";
}

/// <param name="Outcome"><c>done</c>, <c>failed</c> ou <c>rolledBack</c>.</param>
public sealed record HubUpdateLastInfo(string Version, string PreviousVersion, string Outcome, string? Error, DateTimeOffset At, bool Automatic);

public sealed record HubAutoUpdateInput(bool Enabled);
