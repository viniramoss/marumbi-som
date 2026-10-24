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

    private static async Task<IResult> ListAsync(HubDb db, CancellationToken cancellationToken) =>
        Results.Ok(await ReadAsync(db, cancellationToken));

    private static async Task<List<ReleaseInfo>> ReadAsync(HubDb db, CancellationToken cancellationToken)
    {
        var releases = await db.AgentReleases.AsNoTracking().ToListAsync(cancellationToken);
        var versions = await db.StoreStates.AsNoTracking().Where(s => s.AgentVersion != null).Select(s => s.AgentVersion!).ToListAsync(cancellationToken);
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
        try
        {
            await using var stream = signatureFile.OpenReadStream();
            signature = signatureFile.Length > 16 * 1024 ? null : await JsonSerializer.DeserializeAsync<ReleaseSignature>(stream, ContractJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            signature = null;
        }

        if (signature is null || !ReleaseSigning.IsVersion(signature.Version) || signature.Sha256 is not { Length: 64 } || string.IsNullOrWhiteSpace(signature.Signature))
        {
            return ApiResults.BadRequest("Arquivo de assinatura inválido. Use o .sig.json gerado pelo sign-release junto com o pacote.");
        }

        if (await db.AgentReleases.AnyAsync(r => r.Version == signature.Version, cancellationToken))
        {
            return ApiResults.Conflict($"A versão {signature.Version} já foi enviada.");
        }

        Directory.CreateDirectory(paths.Releases);
        var temp = Path.Combine(paths.Releases, $".envio-{Guid.NewGuid():N}.zip");
        try
        {
            string sha256;
            await using (var source = package.OpenReadStream())
            await using (var target = File.Create(temp))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(sha256, signature.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ApiResults.BadRequest("O pacote não confere com a assinatura (SHA-256 diferente). Envie o .zip e o .sig.json gerados juntos.");
            }

            if (CheckContents(temp) is { } problem)
            {
                return ApiResults.BadRequest(problem);
            }

            var release = new AgentRelease
            {
                Version = signature.Version,
                Sha256 = sha256,
                SizeBytes = package.Length,
                Signature = signature.Signature.Trim(),
                FileName = $"MarumbiSom.Agent-{signature.Version}.zip",
                CreatedAt = time.GetUtcNow(),
                IsCurrent = true,
            };
            File.Move(temp, PackagePath(paths, release), overwrite: true);
            await db.AgentReleases.Where(r => r.IsCurrent).ExecuteUpdateAsync(r => r.SetProperty(x => x.IsCurrent, false), cancellationToken);
            db.AgentReleases.Add(release);
            db.Audit(user, time, "releases.upload", release.Version, new { release.Sha256, release.SizeBytes });
            await db.SaveChangesAsync(cancellationToken);
            loggers.CreateLogger(typeof(ReleasesEndpoints).FullName!).LogInformation("Versão {Version} do agente publicada ({Size} bytes)", release.Version, release.SizeBytes);
            panel.Changed("releases");
            var info = (await ReadAsync(db, cancellationToken)).Single(r => r.Version == release.Version);
            return Results.Created($"/api/releases/{release.Version}", info);
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
