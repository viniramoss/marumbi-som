using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Versões do agente para distribuição (seção 12.2, D27 d). O Hub guarda o pacote e confere o SHA-256 e o conteúdo; a
/// assinatura só o agente confere, com a chave pública que ele já tem.
/// </summary>
public static class ReleasesEndpoints
{
    public const long MaxPackageBytes = 300L * 1024 * 1024;

    public const int MaxSignatureBytes = 16 * 1024;

    /// <summary>O que um pacote do agente precisa ter na raiz para o Updater instalar e o agente novo atualizar de novo.</summary>
    private static readonly string[] RequiredFiles = ["MarumbiSom.Agent.exe", "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName];

    public static void MapReleases(this RouteGroupBuilder api)
    {
        api.MapGet("/releases", ListAsync).Returns<IReadOnlyList<ReleaseInfo>>();
        api.MapPost("/releases", UploadAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxPackageBytes + (1024 * 1024)), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxPackageBytes + (1024 * 1024) })
            .Accepts<IFormFileCollection>("multipart/form-data")
            .Returns<ReleaseInfo>(StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
        api.MapPut("/releases/current", SetCurrentAsync).Returns<ReleaseInfo>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapDelete("/releases/{version}", DeleteAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapGet("/releases/github", (GitHubReleaseWatcher github, CancellationToken cancellationToken) => github.StatusAsync(cancellationToken))
            .Returns<GitHubStatus>();
        api.MapPost("/releases/github/check", CheckGitHubAsync).Returns<GitHubStatus>(StatusCodes.Status202Accepted, StatusCodes.Status400BadRequest);
        api.MapPut("/releases/github/token", SaveGitHubTokenAsync).Returns<GitHubStatus>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest);
    }

    public static string PackagePath(HubPaths paths, AgentRelease release)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(release);
        return Path.Combine(paths.Releases, release.FileName);
    }

    public static AgentReleaseInfo ToAgentInfo(AgentRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new AgentReleaseInfo(release.Version, release.Sha256, release.SizeBytes, release.Signature);
    }

    private static async Task<IResult> CheckGitHubAsync(GitHubReleaseWatcher github, CancellationToken cancellationToken)
    {
        if (github.Repository is null)
        {
            return ApiResults.BadRequest("A procura de versões no GitHub está desligada (Hub:GitHubRepository vazio).");
        }

        github.RequestCheck();
        return Results.Json(await github.StatusAsync(cancellationToken), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>O token nunca volta para o painel: a resposta só diz se há um cadastrado.</summary>
    private static async Task<IResult> SaveGitHubTokenAsync(GitHubTokenInput input, GitHubReleaseWatcher github, CancellationToken cancellationToken)
    {
        var token = input.Token?.Trim();
        if (!string.IsNullOrEmpty(token) && (token.Length > 300 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
        {
            return ApiResults.BadRequest("Token inválido: cole só o token gerado no GitHub, sem espaços.");
        }

        await github.SaveTokenAsync(token, cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            github.RequestCheck();
        }

        return Results.Ok(await github.StatusAsync(cancellationToken));
    }

    private static async Task<IResult> ListAsync(HubDb db, CancellationToken cancellationToken) =>
        Results.Ok(await ReadAsync(db, cancellationToken));

    private static async Task<List<ReleaseInfo>> ReadAsync(HubDb db, CancellationToken cancellationToken)
    {
        var releases = await db.AgentReleases.AsNoTracking().ToListAsync(cancellationToken);
        // O agente informa a versão com o commit (0.6.41+abc1234).
        var versions = (await db.StoreStates.AsNoTracking().Where(s => s.AgentVersion != null).Select(s => s.AgentVersion!).ToListAsync(cancellationToken))
            .Select(ReleaseSigning.ShortVersion).ToList();
        return releases
            .OrderByDescending(r => r.Version, Comparer<string>.Create(ReleaseSigning.CompareVersions))
            .Select(r => new ReleaseInfo(r.Version, r.Sha256, r.SizeBytes, r.CreatedAt, r.IsCurrent, versions.Count(v => v == r.Version)))
            .ToList();
    }

    private static async Task<IResult> UploadAsync(
        HttpRequest request, HubDb db, HubPaths paths, PanelHub panel, ClaimsPrincipal user, TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return ApiResults.BadRequest("Envie o pacote como multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var package = form.Files.FirstOrDefault(f => f.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var signatureFile = form.Files.FirstOrDefault(f => f.FileName.EndsWith(".sig.json", StringComparison.OrdinalIgnoreCase));
        if (package is null || signatureFile is null)
        {
            return ApiResults.BadRequest("Envie os dois arquivos gerados pelo sign-release: o pacote .zip e a assinatura .sig.json.");
        }

        if (package.Length > MaxPackageBytes)
        {
            return ApiResults.BadRequest("Pacote maior que 300 MB.");
        }

        ReleaseSignature? signature;
        await using (var stream = signatureFile.OpenReadStream())
        {
            signature = signatureFile.Length > MaxSignatureBytes ? null : await ReadSignatureAsync(stream, cancellationToken);
        }

        if (signature is null)
        {
            return ApiResults.BadRequest("Arquivo de assinatura inválido. Use o .sig.json gerado pelo sign-release junto com o pacote.");
        }

        if (await db.AgentReleases.AnyAsync(r => r.Version == signature.Version, cancellationToken))
        {
            return ApiResults.Conflict($"A versão {signature.Version} já foi enviada.");
        }

        await using var content = package.OpenReadStream();
        var (release, problem) = await ImportAsync(db, paths, content, signature, user, time, "releases.upload", progress: null, cancellationToken);
        if (release is null)
        {
            return ApiResults.BadRequest(problem!);
        }

        loggers.CreateLogger(typeof(ReleasesEndpoints).FullName!).LogInformation("Versão {Version} do agente publicada ({Size} bytes)", release.Version, release.SizeBytes);
        panel.Changed("releases");
        var info = (await ReadAsync(db, cancellationToken)).Single(r => r.Version == release.Version);
        return Results.Created($"/api/releases/{release.Version}", info);
    }

    /// <summary>Lê o .sig.json; nulo se não estiver no formato do sign-release.</summary>
    public static async Task<ReleaseSignature?> ReadSignatureAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var signature = await JsonSerializer.DeserializeAsync<ReleaseSignature>(stream, ContractJson.Options, cancellationToken);
            return signature is not null && ReleaseSigning.IsVersion(signature.Version) && signature.Sha256 is { Length: 64 } && !string.IsNullOrWhiteSpace(signature.Signature)
                ? signature
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Grava o pacote em <c>data\releases</c> conferindo SHA-256 e conteúdo e o marca como a versão de distribuição.
    /// Usado pelo envio no painel e pelas versões baixadas do GitHub (D29). Devolve o motivo da recusa se não gravar.
    /// </summary>
    public static async Task<(AgentRelease? Release, string? Problem)> ImportAsync(
        HubDb db, HubPaths paths, Stream package, ReleaseSignature signature, ClaimsPrincipal? user, TimeProvider time, string auditAction,
        Action<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(time);
        Directory.CreateDirectory(paths.Releases);
        var temp = Path.Combine(paths.Releases, $".envio-{Guid.NewGuid():N}.zip");
        try
        {
            string sha256;
            long size = 0;
            await using (var target = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await package.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > MaxPackageBytes)
                    {
                        return (null, "Pacote maior que 300 MB.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Invoke(size);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(sha256, signature.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return (null, "O pacote não confere com a assinatura (SHA-256 diferente). Envie o .zip e o .sig.json gerados juntos.");
            }

            if (CheckContents(temp) is { } problem)
            {
                return (null, problem);
            }

            var release = new AgentRelease
            {
                Version = signature.Version,
                Sha256 = sha256,
                SizeBytes = size,
                Signature = signature.Signature.Trim(),
                FileName = $"MarumbiSom.Agent-{signature.Version}.zip",
                CreatedAt = time.GetUtcNow(),
                IsCurrent = true,
            };
            File.Move(temp, PackagePath(paths, release), overwrite: true);
            await db.AgentReleases.Where(r => r.IsCurrent).ExecuteUpdateAsync(r => r.SetProperty(x => x.IsCurrent, false), cancellationToken);
            db.AgentReleases.Add(release);
            db.Audit(user, time, auditAction, release.Version, new { release.Sha256, release.SizeBytes });
            await db.SaveChangesAsync(cancellationToken);
            return (release, null);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>O .zip abre, tem os arquivos que o Updater precisa na raiz e nenhum caminho que saia da pasta.</summary>
    private static string? CheckContents(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.Entries.Any(e => e.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(e.FullName)))
            {
                return "O pacote tem caminhos inválidos.";
            }

            var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = RequiredFiles.Where(f => !names.Contains(f)).ToList();
            return missing.Count == 0 ? null : $"O pacote não tem {string.Join(", ", missing)} na raiz. Gere de novo pelo sign-release a partir da pasta do agente.";
        }
        catch (InvalidDataException)
        {
            return "O arquivo enviado não é um .zip válido.";
        }
    }

    private static async Task<IResult> SetCurrentAsync(
        CurrentReleaseInput input, HubDb db, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var release = await db.AgentReleases.SingleOrDefaultAsync(r => r.Version == input.Version, cancellationToken);
        if (release is null)
        {
            return Results.NotFound();
        }

        await db.AgentReleases.Where(r => r.IsCurrent && r.Version != release.Version).ExecuteUpdateAsync(r => r.SetProperty(x => x.IsCurrent, false), cancellationToken);
        release.IsCurrent = true;
        db.Audit(user, time, "releases.current", release.Version);
        await db.SaveChangesAsync(cancellationToken);
        panel.Changed("releases");
        return Results.Ok((await ReadAsync(db, cancellationToken)).Single(r => r.Version == release.Version));
    }

    private static async Task<IResult> DeleteAsync(
        string version, HubDb db, HubPaths paths, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var release = await db.AgentReleases.SingleOrDefaultAsync(r => r.Version == version, cancellationToken);
        if (release is null)
        {
            return Results.NotFound();
        }

        if (release.IsCurrent)
        {
            return ApiResults.BadRequest("Esta é a versão para distribuição. Escolha outra antes de remover.");
        }

        db.AgentReleases.Remove(release);
        db.Audit(user, time, "releases.delete", release.Version);
        await db.SaveChangesAsync(cancellationToken);
        File.Delete(PackagePath(paths, release));
        panel.Changed("releases");
        return Results.NoContent();
    }

    /// <param name="StoreCount">Lojas que informaram estar nesta versão.</param>
    public sealed record ReleaseInfo(string Version, string Sha256, long SizeBytes, DateTimeOffset CreatedAt, bool IsCurrent, int StoreCount);

    public sealed record CurrentReleaseInput(string Version);
}
