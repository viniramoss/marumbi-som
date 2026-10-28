using System.Globalization;
using System.Security.Claims;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Library;
using MarumbiSom.Hub.Manifests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>Biblioteca de músicas (seção 11.2): upload, busca, edição, remoção e pré-escuta.</summary>
public static class TracksEndpoints
{
    /// <summary>Até 20 arquivos de 100 MB por envio.</summary>
    public const long MaxRequestBytes = 20 * AudioProcessor.MaxUploadBytes;

    public static void MapTracks(this RouteGroupBuilder api)
    {
        api.MapGet("/tracks", ListAsync);
        api.MapPost("/tracks", UploadAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxRequestBytes });
        api.MapGet("/uploads", (IngestionWorker queue) => Results.Ok(queue.Jobs));
        api.MapPatch("/tracks/{hash}", UpdateAsync);
        api.MapDelete("/tracks/{hash}", DeleteAsync);
        api.MapGet("/tracks/{hash}/audio", AudioAsync);
    }

    public static TrackDto ToDto(Data.Track track) =>
        new(track.Hash, track.Kind, track.Title, track.Artist, track.DurationMs, track.SizeBytes, track.LoudnessLufs, track.OriginalName, track.CreatedAt);

    private static async Task<IResult> ListAsync(HubDb db, string? q, TrackKind? kind, CancellationToken cancellationToken)
    {
        var wanted = kind ?? TrackKind.Music;
        var tracks = await db.Tracks.AsNoTracking().Where(t => t.DeletedAt == null && t.Kind == wanted).ToListAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Busca sem diferenciar maiúsculas nem acentos ("acai" acha "Açaí").
            var compare = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            tracks = tracks.Where(t => compare.IndexOf(t.Title, q, options) >= 0 || (t.Artist is not null && compare.IndexOf(t.Artist, q, options) >= 0)
                || compare.IndexOf(t.OriginalName, q, options) >= 0).ToList();
        }

        return Results.Ok(tracks.OrderBy(t => t.Artist ?? t.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).Select(ToDto));
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, IngestionWorker queue, HubDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Envie os arquivos como multipart/form-data." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new { error = "Nenhum arquivo enviado." });
        }

        var kind = form.TryGetValue("kind", out var kindValue) && kindValue == "offer" ? TrackKind.Offer : TrackKind.Music;
        var jobs = new List<object>();
        foreach (var file in form.Files)
        {
            if (!AudioProcessor.AllowedExtensions.Contains(Path.GetExtension(file.FileName)))
            {
                jobs.Add(new { originalName = file.FileName, status = "rejected", error = "Formato não aceito." });
                continue;
            }

            if (file.Length > AudioProcessor.MaxUploadBytes)
            {
                jobs.Add(new { originalName = file.FileName, status = "rejected", error = "Arquivo maior que 100 MB." });
                continue;
            }

            await using var stream = file.OpenReadStream();
            jobs.Add(await queue.EnqueueAsync(stream, file.FileName, kind, cancellationToken));
        }

        db.Audit(user, time, "tracks.upload", null, new { files = form.Files.Select(f => f.FileName) });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted("/api/uploads", jobs);
    }

    private static async Task<IResult> UpdateAsync(
        string hash, TrackUpdate update, HubDb db, ManifestPublisher publisher, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var track = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == hash && t.DeletedAt == null, cancellationToken);
        if (track is null)
        {
            return Results.NotFound();
        }

        if (update.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(update.Title) || update.Title.Length > 200)
            {
                return Results.BadRequest(new { error = "Título obrigatório, até 200 caracteres." });
            }

            track.Title = update.Title.Trim();
        }

        if (update.Artist is not null)
        {
            track.Artist = string.IsNullOrWhiteSpace(update.Artist) ? null : update.Artist.Trim()[..Math.Min(200, update.Artist.Trim().Length)];
        }

        db.Audit(user, time, "tracks.update", hash, update);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        return Results.Ok(ToDto(track));
    }

    private static async Task<IResult> DeleteAsync(string hash, HubDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var track = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == hash && t.DeletedAt == null, cancellationToken);
        if (track is null)
        {
            return Results.NotFound();
        }

        var playlists = await db.PlaylistItems.Where(i => i.TrackHash == hash)
            .Join(db.Playlists, i => i.PlaylistId, p => p.Id, (_, p) => p.Name).Distinct().ToListAsync(cancellationToken);
        if (playlists.Count > 0 || await db.Offers.AnyAsync(o => o.AudioHash == hash, cancellationToken))
        {
            return Results.Conflict(new
            {
                error = playlists.Count > 0 ? "A música está em uso nas playlists: " + string.Join(", ", playlists) + "." : "O áudio está em uso por uma oferta.",
                playlists,
            });
        }

        // Exclusão lógica: o arquivo fica na biblioteca e volta se for enviado de novo.
        track.DeletedAt = time.GetUtcNow();
        db.Audit(user, time, "tracks.delete", hash, new { track.Title });
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> AudioAsync(string hash, HubDb db, HubPaths paths, CancellationToken cancellationToken)
    {
        if (!await db.Tracks.AnyAsync(t => t.Hash == hash, cancellationToken) || !File.Exists(paths.LibraryFile(hash)))
        {
            return Results.NotFound();
        }

        return Results.File(paths.LibraryFile(hash), "audio/mp4", enableRangeProcessing: true);
    }

    public sealed record TrackDto(
        string Hash, TrackKind Kind, string Title, string? Artist, long DurationMs, long SizeBytes, double? LoudnessLufs, string OriginalName, DateTimeOffset CreatedAt);

    public sealed record TrackUpdate(string? Title, string? Artist);
}
