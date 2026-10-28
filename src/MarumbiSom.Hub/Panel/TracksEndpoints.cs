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
        api.MapGet("/tracks", ListAsync).Returns<IReadOnlyList<TrackDto>>();
        api.MapPost("/tracks", UploadAsync)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxRequestBytes })
            .Accepts<IFormFileCollection>("multipart/form-data")
            .Returns<IReadOnlyList<IngestionJob>>(StatusCodes.Status202Accepted, StatusCodes.Status400BadRequest);
        api.MapGet("/uploads", (IngestionWorker queue) => Results.Ok(queue.Jobs)).Returns<IReadOnlyList<IngestionJob>>();
        api.MapPatch("/tracks/{hash}", UpdateAsync).Returns<TrackDto>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapDelete("/tracks/{hash}", DeleteAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapGet("/tracks/{hash}/audio", AudioAsync).Produces(StatusCodes.Status200OK, contentType: "audio/mp4").Produces(StatusCodes.Status404NotFound);
    }

    public static TrackDto ToDto(Data.Track track, IReadOnlyList<PlaylistRef> playlists) =>
        new(track.Hash, track.Kind, track.Title, track.Artist, track.DurationMs, track.SizeBytes, track.LoudnessLufs, track.OriginalName, track.CreatedAt, playlists);

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

        var usage = await UsageAsync(db, cancellationToken);
        return Results.Ok(tracks.OrderBy(t => t.Artist ?? t.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => ToDto(t, usage[t.Hash].ToList())));
    }

    /// <summary>Em quais playlists cada música está (seção 7.2, Biblioteca).</summary>
    private static async Task<ILookup<string, PlaylistRef>> UsageAsync(HubDb db, CancellationToken cancellationToken)
    {
        var rows = await db.PlaylistItems.AsNoTracking()
            .Join(db.Playlists, i => i.PlaylistId, p => p.Id, (i, p) => new { i.TrackHash, p.Id, p.Name })
            .Distinct().ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToLookup(r => r.TrackHash, r => new PlaylistRef(r.Id, r.Name), StringComparer.Ordinal);
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, IngestionWorker queue, HubDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return ApiResults.BadRequest("Envie os arquivos como multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.Count == 0)
        {
            return ApiResults.BadRequest("Nenhum arquivo enviado.");
        }

        var kind = form.TryGetValue("kind", out var kindValue) && kindValue == "offer" ? TrackKind.Offer : TrackKind.Music;
        var jobs = new List<IngestionJob>();
        foreach (var file in form.Files)
        {
            if (!AudioProcessor.AllowedExtensions.Contains(Path.GetExtension(file.FileName)))
            {
                jobs.Add(IngestionJob.Rejected(file.FileName, kind, time.GetUtcNow(), "Formato não aceito."));
                continue;
            }

            if (file.Length > AudioProcessor.MaxUploadBytes)
            {
                jobs.Add(IngestionJob.Rejected(file.FileName, kind, time.GetUtcNow(), "Arquivo maior que 100 MB."));
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
        string hash, TrackUpdate update, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
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
                return ApiResults.BadRequest("Título obrigatório, até 200 caracteres.");
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
        panel.Changed("tracks", "playlists");
        var usage = await UsageAsync(db, cancellationToken);
        return Results.Ok(ToDto(track, usage[hash].ToList()));
    }

    private static async Task<IResult> DeleteAsync(string hash, HubDb db, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
        panel.Changed("tracks");
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
        string Hash,
        TrackKind Kind,
        string Title,
        string? Artist,
        long DurationMs,
        long SizeBytes,
        double? LoudnessLufs,
        string OriginalName,
        DateTimeOffset CreatedAt,
        IReadOnlyList<PlaylistRef> Playlists);

    public sealed record PlaylistRef(int Id, string Name);

    public sealed record TrackUpdate(string? Title, string? Artist);
}
