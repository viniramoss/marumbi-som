using System.Security.Claims;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Panel;
using Microsoft.AspNetCore.Mvc;

namespace MarumbiSom.Hub.Updates;

/// <summary>Configurações → Atualização do Hub (D32 f, D35): versão instalada e disponível, buscar e atualizar agora, a madrugada e o envio à mão.</summary>
public static class HubUpdateEndpoints
{
    public static void MapHubUpdate(this RouteGroupBuilder api)
    {
        api.MapGet("/hub/update", (HubUpdates updates, CancellationToken cancellationToken) => updates.StatusAsync(cancellationToken))
            .Returns<HubUpdateStatus>();
        api.MapPost("/hub/update", RequestAsync).Returns<HubUpdateStatus>(StatusCodes.Status202Accepted, StatusCodes.Status400BadRequest);
        api.MapDelete("/hub/update", CancelAsync).Returns<HubUpdateStatus>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest);
        api.MapPut("/hub/update/auto", SetAutoAsync).Returns<HubUpdateStatus>();
        api.MapPost("/hub/update/check", SearchAsync).Returns<HubUpdateStatus>(StatusCodes.Status202Accepted, StatusCodes.Status400BadRequest);
        api.MapPost("/hub/update/package", UploadAsync)
            .DisableAntiforgery()
            .WithMetadata(
                new RequestSizeLimitAttribute(ReleasesEndpoints.MaxPackageBytes + (1024 * 1024)),
                new RequestFormLimitsAttribute { MultipartBodyLengthLimit = ReleasesEndpoints.MaxPackageBytes + (1024 * 1024) })
            .Accepts<IFormFileCollection>("multipart/form-data")
            .Returns<HubUpdateStatus>(StatusCodes.Status201Created, StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> RequestAsync(HubUpdates updates, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await updates.RequestAsync(automatic: false, user, cancellationToken) is { } problem
            ? ApiResults.BadRequest(problem)
            : Results.Json(await updates.StatusAsync(cancellationToken), statusCode: StatusCodes.Status202Accepted);

    /// <summary>"Buscar atualizações" (D35): procura no GitHub agora e já pede a troca se vier versão nova.</summary>
    private static async Task<IResult> SearchAsync(
        GitHubReleaseWatcher github, HubUpdates updates, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await github.SearchAndInstallAsync(user, cancellationToken) is { } problem
            ? ApiResults.BadRequest(problem)
            : Results.Json(await updates.StatusAsync(cancellationToken), statusCode: StatusCodes.Status202Accepted);

    private static async Task<IResult> CancelAsync(HubUpdates updates, ClaimsPrincipal user, CancellationToken cancellationToken) =>
        await updates.CancelAsync(user, cancellationToken) is { } problem
            ? ApiResults.BadRequest(problem)
            : Results.Ok(await updates.StatusAsync(cancellationToken));

    private static async Task<IResult> SetAutoAsync(HubAutoUpdateInput input, HubUpdates updates, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await updates.SetAutoAsync(input.Enabled, user, cancellationToken);
        return Results.Ok(await updates.StatusAsync(cancellationToken));
    }

    /// <summary>O .zip e o .sig.json do sign-release -HubDir (pacotes de teste, ou o GitHub fora do ar).</summary>
    private static async Task<IResult> UploadAsync(HttpRequest request, HubUpdates updates, ClaimsPrincipal user, CancellationToken cancellationToken)
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
            return ApiResults.BadRequest("Envie os dois arquivos do pacote do Hub: o .zip e o .sig.json gerados juntos.");
        }

        ReleaseSignature? signature;
        await using (var stream = signatureFile.OpenReadStream())
        {
            signature = signatureFile.Length > ReleasesEndpoints.MaxSignatureBytes ? null : await ReleasesEndpoints.ReadSignatureAsync(stream, cancellationToken);
        }

        if (signature is null)
        {
            return ApiResults.BadRequest("Arquivo de assinatura inválido. Use o .sig.json gerado junto com o pacote do Hub.");
        }

        await using var content = package.OpenReadStream();
        var (_, problem) = await updates.ImportAsync(content, signature, progress: null, user, "hub.update.upload", cancellationToken);
        return problem is not null
            ? ApiResults.BadRequest(problem)
            : Results.Json(await updates.StatusAsync(cancellationToken), statusCode: StatusCodes.Status201Created);
    }
}
