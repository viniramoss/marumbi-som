using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Updates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Versões do agente publicadas pelo GitHub Actions (D29): procura a Release mais nova com o pacote assinado a cada
/// <see cref="HubOptions.ReleaseCheckHours"/> horas (ou quando o painel pede), baixa com andamento e a deixa como versão de
/// distribuição. Nenhuma loja atualiza sem o administrador mandar; a assinatura continua sendo conferida pelo agente.
/// Na mesma procura baixa o pacote do Hub mais novo que o instalado, que o <see cref="HubUpdates"/> confere e instala (D32 b).
/// </summary>
public sealed partial class GitHubReleaseWatcher(
    IHttpClientFactory httpClients,
    IServiceScopeFactory scopes,
    HubPaths paths,
    IOptions<HubOptions> options,
    IDataProtectionProvider protection,
    PanelHub panel,
    HubUpdates hubUpdates,
    TimeProvider time,
    ILogger<GitHubReleaseWatcher> logger) : BackgroundService
{
    public const string HttpClientName = "github";
    public const string TokenKey = "github.token";
    public const string ImportedKey = "github.imported";

    /// <summary>Tópico do painel ao vivo para a situação da procura (o download avisa a cada meio segundo).</summary>
    public const string Topic = "github";
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    /// <summary>Limite do download do pacote (~110 MB); a 1 Mbps leva uns 15 minutos.</summary>
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromHours(1);

    private readonly IDataProtector _protector = protection.CreateProtector("MarumbiSom.Hub.GitHubToken");
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly SemaphoreSlim _requested = new(0, 1);
    private readonly Lock _gate = new();
    private DateTimeOffset? _checkedAt;
    private string? _message;
    private bool _problem;
    private GitHubDownload? _download;
    private bool _running;

    public string? Repository => string.IsNullOrWhiteSpace(options.Value.GitHubRepository) ? null : options.Value.GitHubRepository.Trim();

    /// <summary>Pede uma procura agora (botão "Procurar agora"); pedidos repetidos durante a procura viram um só.</summary>
    public void RequestCheck()
    {
        if (_requested.CurrentCount == 0)
        {
            try
            {
                _requested.Release();
            }
            catch (SemaphoreFullException)
            {
                // Já havia um pedido na fila.
            }
        }
    }

    public async Task<GitHubStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var tokenConfigured = await ReadSettingAsync(TokenKey, cancellationToken) is not null;
        lock (_gate)
        {
            return new GitHubStatus(Repository, tokenConfigured, options.Value.ReleaseCheckHours, _checkedAt, _message, _problem, _running, _download);
        }
    }

    /// <summary>Guarda o token (cifrado com a Data Protection do Hub) ou o apaga com <c>null</c>.</summary>
    public async Task SaveTokenAsync(string? token, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == TokenKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            if (setting is not null)
            {
                db.Settings.Remove(setting);
            }
        }
        else if (setting is null)
        {
            db.Settings.Add(new Setting { Key = TokenKey, Value = _protector.Protect(token.Trim()) });
        }
        else
        {
            setting.Value = _protector.Protect(token.Trim());
        }

        db.Audit(null, time, "releases.github.token", null, new { configured = !string.IsNullOrWhiteSpace(token) });
        await db.SaveChangesAsync(cancellationToken);
        panel.Changed(Topic);
    }

    /// <summary>Uma procura completa: lista as Releases, escolhe a versão mais nova e baixa se ela ainda não veio.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        if (Repository is not { } repository)
        {
            return;
        }

        await _checking.WaitAsync(cancellationToken);
        try
        {
            SetState(running: true);
            string message;
            bool problem;
            try
            {
                (message, problem) = await RunCheckAsync(repository, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falha inesperada ao procurar versão nova no GitHub");
                (message, problem) = ($"Falha inesperada ao procurar versão nova: {ex.Message}", true);
            }

            lock (_gate)
            {
                _checkedAt = time.GetUtcNow();
                _message = message;
                _problem = problem;
            }

            if (problem)
            {
                logger.LogWarning("Procura de versão nova no GitHub: {Message}", message);
            }
        }
        finally
        {
            lock (_gate)
            {
                _download = null;
                _running = false;
            }

            _checking.Release();
            panel.Changed("releases", Topic);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Repository is null)
        {
            return;
        }

        var wait = FirstCheckDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var delay = Task.Delay(wait, time, timer.Token);
                var requested = _requested.WaitAsync(timer.Token);
                await Task.WhenAny(delay, requested);
                await timer.CancelAsync();
                stoppingToken.ThrowIfCancellationRequested();
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha inesperada ao procurar versão nova no GitHub");
            }

            wait = TimeSpan.FromHours(Math.Clamp(options.Value.ReleaseCheckHours, 1, 168));
        }
    }

    private async Task<(string Message, bool Problem)> RunCheckAsync(string repository, CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(cancellationToken);
        var client = httpClients.CreateClient(HttpClientName);
        List<GitHubRelease>? releases;
        try
        {
            using var list = Request(HttpMethod.Get, $"repos/{repository}/releases?per_page=20", token, "application/vnd.github+json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var response = await client.SendAsync(list, timeout.Token);
            if (Explain(response.StatusCode, token is not null) is { } refused)
            {
                return (refused, true);
            }

            releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(GitHubJson.Default.ListGitHubRelease, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ($"Não foi possível falar com o GitHub: {(ex is OperationCanceledException ? "sem resposta em 30 s" : ex.Message)}", true);
        }

        var agent = await CheckAgentAsync(client, token, releases ?? [], cancellationToken);
        await CheckHubAsync(client, token, releases ?? [], cancellationToken);
        return agent;
    }

    private async Task<(string Message, bool Problem)> CheckAgentAsync(
        HttpClient client, string? token, IReadOnlyList<GitHubRelease> releases, CancellationToken cancellationToken)
    {
        var newest = releases
            .Where(r => !r.Draft)
            .SelectMany(r => r.Assets.Select(a => (Asset: a, Match: PackageName().Match(a.Name), Release: r)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Asset, Version: x.Match.Groups[1].Value, Signature: x.Release.Assets.FirstOrDefault(a => a.Name == $"MarumbiSom.Agent-{x.Match.Groups[1].Value}.sig.json")))
            .Where(x => x.Signature is not null)
            .OrderByDescending(x => x.Version, Comparer<string>.Create(ReleaseSigning.CompareVersions))
            .FirstOrDefault();
        if (newest.Asset is null)
        {
            return ("Nenhuma versão do agente publicada no GitHub ainda.", false);
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        if (await db.AgentReleases.AnyAsync(r => r.Version == newest.Version, cancellationToken))
        {
            return ($"A versão mais nova do GitHub ({newest.Version}) já está no Hub.", false);
        }

        var imported = await ReadSettingAsync(ImportedKey, cancellationToken);
        if (imported is not null && ReleaseSigning.CompareVersions(newest.Version, imported) <= 0)
        {
            return ($"A versão {newest.Version} do GitHub foi removida do Hub e não será baixada de novo.", false);
        }

        if (newest.Asset.Size > ReleasesEndpoints.MaxPackageBytes)
        {
            return ($"O pacote da versão {newest.Version} no GitHub passa de 300 MB.", true);
        }

        // O tempo limite do HttpClient não cobre a leitura do corpo; um download parado não pode prender a procura.
        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadTimeout.CancelAfter(DownloadTimeout);
        var downloadToken = downloadTimeout.Token;
        try
        {
            ReleaseSignature? signature;
            using (var request = Request(HttpMethod.Get, newest.Signature!.Url, token, "application/octet-stream"))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken))
            {
                if (Explain(response.StatusCode, token is not null) is { } refused)
                {
                    return (refused, true);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(downloadToken);
                signature = await ReleasesEndpoints.ReadSignatureAsync(new LimitedStream(stream, ReleasesEndpoints.MaxSignatureBytes), downloadToken);
            }

            if (signature is null || signature.Version != newest.Version)
            {
                return ($"A assinatura da versão {newest.Version} no GitHub não está no formato esperado.", true);
            }

            logger.LogInformation("Baixando a versão {Version} do agente do GitHub ({Size} bytes)", newest.Version, newest.Asset.Size);
            SetDownload(new GitHubDownload(newest.Version, 0, newest.Asset.Size));
            using var package = Request(HttpMethod.Get, newest.Asset.Url, token, "application/octet-stream");
            using var download = await client.SendAsync(package, HttpCompletionOption.ResponseHeadersRead, downloadToken);
            if (Explain(download.StatusCode, token is not null) is { } denied)
            {
                return (denied, true);
            }

            await using var content = await download.Content.ReadAsStreamAsync(downloadToken);
            var lastReport = time.GetTimestamp();
            var (release, problem) = await ReleasesEndpoints.ImportAsync(
                db, paths, content, signature, user: null, time, "releases.github", done =>
                {
                    if (time.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(500))
                    {
                        lastReport = time.GetTimestamp();
                        SetDownload(new GitHubDownload(newest.Version, done, newest.Asset.Size));
                    }
                },
                downloadToken);
            if (release is null)
            {
                return ($"A versão {newest.Version} do GitHub foi recusada: {problem}", true);
            }

            await SaveSettingAsync(db, ImportedKey, release.Version, cancellationToken);
            logger.LogInformation("Versão {Version} do agente baixada do GitHub e marcada para distribuição", release.Version);
            return ($"Versão {release.Version} baixada do GitHub e pronta para atualizar as lojas.", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ($"O download da versão {newest.Version} do GitHub falhou: {(ex is OperationCanceledException ? "passou de 1 hora" : ex.Message)}. O Hub tenta de novo na próxima procura.", true);
        }
    }

    /// <summary>Pacote do Hub (D32 b): a versão mais nova que a instalada, se ainda não foi baixada; a conferência é do <see cref="HubUpdates"/>.</summary>
    private async Task CheckHubAsync(HttpClient client, string? token, IReadOnlyList<GitHubRelease> releases, CancellationToken cancellationToken)
    {
        if (!hubUpdates.Supported || hubUpdates.UpdateInProgress)
        {
            return;
        }

        var newest = releases
            .Where(r => !r.Draft)
            .SelectMany(r => r.Assets.Select(a => (Asset: a, Match: HubPackageName().Match(a.Name), Release: r)))
            .Where(x => x.Match.Success)
            .Select(x => (x.Asset, Version: x.Match.Groups[1].Value, Signature: x.Release.Assets.FirstOrDefault(a => a.Name == $"MarumbiSom.Hub-{x.Match.Groups[1].Value}.sig.json")))
            .Where(x => x.Signature is not null)
            .OrderByDescending(x => x.Version, Comparer<string>.Create(ReleaseSigning.CompareVersions))
            .FirstOrDefault();
        if (newest.Asset is null)
        {
            hubUpdates.SetMessage("Nenhum pacote do Hub assinado no GitHub ainda.", problem: false);
            return;
        }

        if (ReleaseSigning.CompareVersions(newest.Version, HubUpdates.CurrentVersion) <= 0)
        {
            hubUpdates.SetMessage($"O Hub já está na versão mais nova do GitHub ({HubUpdates.CurrentVersion}).", problem: false);
            return;
        }

        if (hubUpdates.AvailableVersion == newest.Version)
        {
            hubUpdates.SetMessage($"A versão {newest.Version} do Hub já foi baixada e conferida.", problem: false);
            return;
        }

        if (newest.Asset.Size > ReleasesEndpoints.MaxPackageBytes)
        {
            hubUpdates.SetMessage($"O pacote da versão {newest.Version} do Hub no GitHub passa de 300 MB.", problem: true);
            return;
        }

        using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadTimeout.CancelAfter(DownloadTimeout);
        var downloadToken = downloadTimeout.Token;
        try
        {
            ReleaseSignature? signature;
            using (var request = Request(HttpMethod.Get, newest.Signature!.Url, token, "application/octet-stream"))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken))
            {
                if (Explain(response.StatusCode, token is not null) is { } refused)
                {
                    hubUpdates.SetMessage(refused, problem: true);
                    return;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(downloadToken);
                signature = await ReleasesEndpoints.ReadSignatureAsync(new LimitedStream(stream, ReleasesEndpoints.MaxSignatureBytes), downloadToken);
            }

            if (signature is null || signature.Version != newest.Version)
            {
                hubUpdates.SetMessage($"A assinatura da versão {newest.Version} do Hub no GitHub não está no formato esperado.", problem: true);
                return;
            }

            logger.LogInformation("Baixando a versão {Version} do Hub do GitHub ({Size} bytes)", newest.Version, newest.Asset.Size);
            hubUpdates.SetDownload(new GitHubDownload(newest.Version, 0, newest.Asset.Size));
            using var package = Request(HttpMethod.Get, newest.Asset.Url, token, "application/octet-stream");
            using var download = await client.SendAsync(package, HttpCompletionOption.ResponseHeadersRead, downloadToken);
            if (Explain(download.StatusCode, token is not null) is { } denied)
            {
                hubUpdates.SetMessage(denied, problem: true);
                return;
            }

            await using var content = await download.Content.ReadAsStreamAsync(downloadToken);
            var lastReport = time.GetTimestamp();
            var (_, problem) = await hubUpdates.ImportAsync(
                content, signature, done =>
                {
                    if (time.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(500))
                    {
                        lastReport = time.GetTimestamp();
                        hubUpdates.SetDownload(new GitHubDownload(newest.Version, done, newest.Asset.Size));
                    }
                },
                user: null, "hub.update.github", downloadToken);
            hubUpdates.SetMessage(
                problem is null ? $"Versão {newest.Version} do Hub baixada do GitHub e conferida." : $"A versão {newest.Version} do Hub no GitHub foi recusada: {problem}",
                problem is not null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            hubUpdates.SetMessage(
                $"O download da versão {newest.Version} do Hub falhou: {(ex is OperationCanceledException ? "passou de 1 hora" : ex.Message)}. O Hub tenta de novo na próxima procura.",
                problem: true);
        }
        finally
        {
            hubUpdates.SetDownload(null);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? token, string accept)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (token is not null)
        {
            // O .NET não repassa este cabeçalho no redirecionamento para o armazenamento do GitHub.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static string? Explain(HttpStatusCode status, bool hasToken) => status switch
    {
        HttpStatusCode.OK => null,
        HttpStatusCode.Unauthorized => "O GitHub recusou o token (vencido ou inválido). Gere outro e cadastre em Configurações.",
        HttpStatusCode.NotFound when !hasToken => "O GitHub não mostrou o repositório. Como ele é privado, cadastre o token do GitHub em Configurações.",
        HttpStatusCode.NotFound => "O GitHub não mostrou o repositório: confira se o token dá acesso de leitura ao marumbi-som.",
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "O GitHub limitou as consultas agora; o Hub tenta de novo mais tarde.",
        _ => $"O GitHub respondeu {(int)status}.",
    };

    private async Task<string?> ReadTokenAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadSettingAsync(TokenKey, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Chaves da Data Protection perdidas (ex.: Hub restaurado sem data\keys): o token precisa ser cadastrado de novo.
            return null;
        }
    }

    private async Task<string?> ReadSettingAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
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

        await db.SaveChangesAsync(cancellationToken);
    }

    private void SetState(bool running)
    {
        lock (_gate)
        {
            _running = running;
        }

        panel.Changed(Topic);
    }

    private void SetDownload(GitHubDownload download)
    {
        lock (_gate)
        {
            _download = download;
        }

        panel.Changed(Topic);
    }

    public override void Dispose()
    {
        _checking.Dispose();
        _requested.Dispose();
        base.Dispose();
    }

    [GeneratedRegex(@"^MarumbiSom\.Agent-(\d+\.\d+\.\d+)\.zip$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^MarumbiSom\.Hub-(\d+\.\d+\.\d+)\.zip$")]
    private static partial Regex HubPackageName();

    /// <summary>Lê no máximo <c>limit</c> bytes (o .sig.json é pequeno; um arquivo enorme não vai para a memória).</summary>
    private sealed class LimitedStream(Stream inner, int limit) : Stream
    {
        private int _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= limit)
            {
                throw new IOException("Arquivo de assinatura grande demais.");
            }

            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, limit - _read)], cancellationToken);
            _read += read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <param name="Checking">Procura em andamento.</param>
/// <param name="Download">Andamento do download da versão nova, enquanto baixa.</param>
public sealed record GitHubStatus(
    string? Repository, bool TokenConfigured, int CheckIntervalHours, DateTimeOffset? CheckedAt, string? Message, bool Problem, bool Checking,
    GitHubDownload? Download);

public sealed record GitHubDownload(string Version, long DoneBytes, long TotalBytes);

public sealed record GitHubTokenInput(string? Token);

internal sealed record GitHubRelease(
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("url")] string Url);

[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class GitHubJson : JsonSerializerContext;
