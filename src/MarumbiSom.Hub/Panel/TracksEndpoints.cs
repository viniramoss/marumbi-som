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
        api.MapPost("/tracks/{hash}/edit", EditAsync)
            .Returns<IngestionJob>(StatusCodes.Status202Accepted, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapGet("/tracks/{hash}/audio", AudioAsync).Produces(StatusCodes.Status200OK, contentType: "audio/mp4").Produces(StatusCodes.Status404NotFound);
    }

    public static TrackDto ToDto(Data.Track track, IReadOnlyList<PlaylistRef> playlists) =>
        new(track.Hash, track.Kind, track.Number, track.Title, track.Artist, track.DurationMs, track.SizeBytes, track.LoudnessLufs, track.OriginalName, track.CreatedAt, playlists,
            track.SourceHash is not null, TrackCuts.RemovedMs(TrackCuts.Parse(track.CutsJson)));

    /// <param name="kind"><c>music</c> (padrão) ou <c>offer</c>, como no JSON (o enum na query só aceitaria "Offer").</param>
    private static async Task<IResult> ListAsync(HubDb db, string? q, string? kind, CancellationToken cancellationToken)
    {
        var wanted = string.Equals(kind, "offer", StringComparison.OrdinalIgnoreCase) ? TrackKind.Offer : TrackKind.Music;
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
        // D31: pelo número de importação; áudios de oferta (sem número) pelo nome.
        return Results.Ok(tracks.OrderBy(t => t.Number ?? int.MaxValue).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
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

    /// <summary>
    /// Edição de música (D34): tira os trechos marcados (no tempo da música como está) ou, com <c>original</c>, volta para
    /// a música enviada. O Hub refaz a música a partir do arquivo enviado, na fila da ingestão; o andamento vem em
    /// <c>/api/uploads</c>.
    /// </summary>
    private static async Task<IResult> EditAsync(
        string hash, TrackEditInput input, HubDb db, HubPaths paths, IngestionWorker queue, ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var track = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == hash && t.DeletedAt == null, cancellationToken);
        if (track is null)
        {
            return Results.NotFound();
        }

        if (track.Kind != TrackKind.Music)
        {
            return ApiResults.BadRequest("Só músicas podem ser editadas aqui; o áudio da oferta se troca na própria oferta.");
        }

        var cuts = input.Cuts ?? [];
        if (cuts.Count > 50 || cuts.Any(c => c.StartMs < 0 || c.EndMs < 0))
        {
            return ApiResults.BadRequest("Trechos inválidos: até 50, com início e fim a partir de 0:00.");
        }

        var sourceHash = track.SourceHash ?? track.Hash;
        var source = await db.Tracks.AsNoTracking().SingleOrDefaultAsync(t => t.Hash == sourceHash, cancellationToken) ?? track;
        IReadOnlyList<TrackCut> newCuts;
        if (input.Original)
        {
            if (track.SourceHash is null)
            {
                return ApiResults.BadRequest("Esta música não foi editada.");
            }

            newCuts = [];
        }
        else
        {
            var marked = TrackCuts.Normalize(cuts.Select(c => new TrackCut(c.StartMs, c.EndMs)), track.DurationMs);
            if (marked.Count == 0)
            {
                return ApiResults.BadRequest("Marque pelo menos um trecho para tirar.");
            }

            newCuts = TrackCuts.Compose(TrackCuts.Parse(track.CutsJson), marked, source.DurationMs);
        }

        var remaining = TimeSpan.FromMilliseconds(source.DurationMs - TrackCuts.RemovedMs(newCuts));
        if (remaining < IngestionProfile.Music().MinDuration)
        {
            return ApiResults.BadRequest(string.Create(
                CultureInfo.InvariantCulture, $"Sem os trechos a música ficaria com {remaining.TotalSeconds:0} s; o mínimo é 30 s."));
        }

        if (queue.IsEditing(track.Hash))
        {
            return ApiResults.Conflict("Esta música já está sendo editada; espere terminar.");
        }

        // O arquivo enviado guardado em originals\ (sem perda); sem ele, o da biblioteca.
        var sourcePath = Directory.Exists(paths.Originals) ? Directory.EnumerateFiles(paths.Originals, sourceHash + ".*").FirstOrDefault() : null;
        sourcePath ??= File.Exists(paths.LibraryFile(sourceHash)) ? paths.LibraryFile(sourceHash) : null;
        if (sourcePath is null)
        {
            return ApiResults.Conflict("O arquivo enviado desta música não está mais no Hub; envie a música de novo para editar.");
        }

        var job = await queue.EnqueueEditAsync(track, sourceHash, sourcePath, newCuts, cancellationToken);
        db.Audit(user, time, "tracks.edit", track.Hash, new { input.Original, source = sourceHash, cuts = newCuts.Select(c => new[] { c.StartMs, c.EndMs }) });
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted("/api/uploads", job);
    }

    private static async Task<IResult> AudioAsync(string hash, HubDb db, HubPaths paths, CancellationToken cancellationToken)
    {
        if (!await db.Tracks.AnyAsync(t => t.Hash == hash, cancellationToken) || !File.Exists(paths.LibraryFile(hash)))
        {
            return Results.NotFound();
        }

        return Results.File(paths.LibraryFile(hash), "audio/mp4", enableRangeProcessing: true);
    }

    /// <param name="Number">Número da música em ordem de importação (D31); nulo nos áudios de oferta.</param>
    /// <param name="Edited">Música editada no painel (D34).</param>
    /// <param name="RemovedMs">Quanto foi tirado da música enviada na edição.</param>
    public sealed record TrackDto(
        string Hash,
        TrackKind Kind,
        int? Number,
        string Title,
        string? Artist,
        long DurationMs,
        long SizeBytes,
        double? LoudnessLufs,
        string OriginalName,
        DateTimeOffset CreatedAt,
        IReadOnlyList<PlaylistRef> Playlists,
        bool Edited,
        long RemovedMs);

    public sealed record PlaylistRef(int Id, string Name);

    public sealed record TrackUpdate(string? Title, string? Artist);

    /// <param name="Cuts">Trechos a tirar, em ms no tempo da música como ela está agora.</param>
    /// <param name="Original">Volta para a música enviada, sem nenhum corte (os trechos são ignorados).</param>
    public sealed record TrackEditInput(IReadOnlyList<CutInput>? Cuts, bool Original);

    public sealed record CutInput(long StartMs, long EndMs);
}
