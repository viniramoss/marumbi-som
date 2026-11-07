using System.Security.Claims;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>Playlists (seção 11.2). Itens têm id estável (UUID) preservado ao reordenar.</summary>
public static class PlaylistsEndpoints
{
    public const int MaxNameLength = 80;

    public static void MapPlaylists(this RouteGroupBuilder api)
    {
        api.MapGet("/playlists", ListAsync).Returns<IReadOnlyList<PlaylistSummary>>();
        api.MapPost("/playlists", CreateAsync).Returns<PlaylistDetail>(StatusCodes.Status201Created, StatusCodes.Status400BadRequest);
        api.MapGet("/playlists/{id:int}", GetAsync).Returns<PlaylistDetail>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapPatch("/playlists/{id:int}", UpdateAsync).Returns<PlaylistDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapDelete("/playlists/{id:int}", DeleteAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict);
        api.MapPut("/playlists/{id:int}/items", ReplaceItemsAsync)
            .Returns<PlaylistDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapPut("/playlists/{id:int}/anchors", ReplaceAnchorsAsync)
            .Returns<PlaylistDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(HubDb db, CancellationToken cancellationToken)
    {
        var playlists = await db.Playlists.AsNoTracking()
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Shuffle,
                ItemCount = p.Items.Count,
                DurationMs = p.Items.Join(db.Tracks, i => i.TrackHash, t => t.Hash, (_, t) => t.DurationMs).Sum(),
                p.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, null, cancellationToken);
        return Results.Ok(playlists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PlaylistSummary(p.Id, p.Name, p.Shuffle, p.ItemCount, p.DurationMs, p.UpdatedAt, usage[p.Id].ToList())));
    }

    private static async Task<IResult> CreateAsync(PlaylistInput input, HubDb db, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (ValidateName(input.Name) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        var now = time.GetUtcNow();
        var playlist = new Playlist { Name = input.Name!.Trim(), Shuffle = input.Shuffle ?? false, CreatedAt = now, UpdatedAt = now };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken);
        db.Audit(user, time, "playlists.create", playlist.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), input);
        await db.SaveChangesAsync(cancellationToken);
        panel.Changed("playlists");
        return Results.Created($"/api/playlists/{playlist.Id}", await DetailAsync(db, playlist.Id, cancellationToken));
    }

    private static async Task<IResult> GetAsync(int id, HubDb db, CancellationToken cancellationToken) =>
        await DetailAsync(db, id, cancellationToken) is { } detail ? Results.Ok(detail) : Results.NotFound();

    private static async Task<IResult> UpdateAsync(
        int id, PlaylistInput input, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var playlist = await db.Playlists.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (playlist is null)
        {
            return Results.NotFound();
        }

        if (input.Name is not null)
        {
            if (ValidateName(input.Name) is { } error)
            {
                return ApiResults.BadRequest(error);
            }

            playlist.Name = input.Name.Trim();
        }

        var removedAnchors = 0;
        if (input.Shuffle is { } shuffle)
        {
            // Âncoras só existem em playlist sequencial: ativar o aleatório remove as âncoras (seção 10.3.4).
            if (shuffle && !playlist.Shuffle)
            {
                removedAnchors = await RemoveAnchorsAsync(db, id, null, cancellationToken);
            }

            playlist.Shuffle = shuffle;
        }

        playlist.UpdatedAt = time.GetUtcNow();
        db.Audit(user, time, "playlists.update", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new { input, removedAnchors });
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("playlists", "tracks", $"playlist:{id}");
        return Results.Ok(await DetailAsync(db, id, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(
        int id, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var playlist = await db.Playlists.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (playlist is null)
        {
            return Results.NotFound();
        }

        var stores = await db.Schedules.Where(s => s.PlaylistId == id)
            .Join(db.Stores, s => s.StoreId, s => s.Id, (_, store) => store.Name).Distinct().ToListAsync(cancellationToken);
        if (stores.Count > 0)
        {
            return Results.Conflict(new { error = "A playlist está na agenda das lojas: " + string.Join(", ", stores) + ".", stores });
        }

        await RemoveAnchorsAsync(db, id, null, cancellationToken);
        db.Playlists.Remove(playlist);
        db.Audit(user, time, "playlists.delete", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new { playlist.Name });
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("playlists", "tracks");
        return Results.NoContent();
    }

    /// <summary>
    /// Substitui a lista ordenada inteira (idempotente). Itens existentes mantêm o <c>id</c>; itens novos vêm sem <c>id</c>.
    /// Itens removidos levam junto as âncoras de oferta presas a eles.
    /// </summary>
    private static async Task<IResult> ReplaceItemsAsync(
        int id, List<ItemInput> items, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var playlist = await db.Playlists.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (playlist is null)
        {
            return Results.NotFound();
        }

        var existingIds = playlist.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var givenIds = items.Where(i => i.Id is not null).Select(i => i.Id!).ToList();
        if (givenIds.Except(existingIds).FirstOrDefault() is { } unknown)
        {
            return ApiResults.BadRequest($"Item {unknown} não pertence a esta playlist.");
        }

        if (givenIds.Count != givenIds.Distinct(StringComparer.Ordinal).Count())
        {
            return ApiResults.BadRequest("O mesmo item aparece duas vezes.");
        }

        var hashes = items.Select(i => i.Hash).Distinct(StringComparer.Ordinal).ToList();
        var valid = await db.Tracks.Where(t => hashes.Contains(t.Hash) && t.DeletedAt == null && t.Kind == TrackKind.Music)
            .Select(t => t.Hash).ToListAsync(cancellationToken);
        if (hashes.Except(valid, StringComparer.Ordinal).FirstOrDefault() is { } missing)
        {
            return ApiResults.BadRequest($"Música {missing} não existe na biblioteca.");
        }

        var removed = existingIds.Except(givenIds, StringComparer.Ordinal).ToList();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var removedAnchors = removed.Count > 0 ? await RemoveAnchorsAsync(db, id, removed, cancellationToken) : 0;

        // Apaga e regrava para não violar a posição única no meio da troca de ordem.
        db.PlaylistItems.RemoveRange(playlist.Items);
        await db.SaveChangesAsync(cancellationToken);
        var position = 0;
        foreach (var item in items)
        {
            db.PlaylistItems.Add(new PlaylistItem { Id = item.Id ?? Guid.NewGuid().ToString(), PlaylistId = id, Position = position++, TrackHash = item.Hash });
        }

        playlist.UpdatedAt = time.GetUtcNow();
        db.Audit(user, time, "playlists.items", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new { count = items.Count, removed = removed.Count, removedAnchors });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("playlists", "tracks", $"playlist:{id}");
        return Results.Ok(await DetailAsync(db, id, cancellationToken));
    }

    /// <summary>
    /// Substitui todas as âncoras de oferta da playlist (seção 11.2, D26 c). Só em playlist sequencial; o tempo da âncora
    /// "durante" vai de 0 até a duração da música − 5 s (seção 10.3.4).
    /// </summary>
    private static async Task<IResult> ReplaceAnchorsAsync(
        int id, List<AnchorInput> anchors, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var playlist = await db.Playlists.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (playlist is null)
        {
            return Results.NotFound();
        }

        if (playlist.Shuffle && anchors.Count > 0)
        {
            return ApiResults.BadRequest("Âncoras de oferta só existem em playlist sequencial. Desative o aleatório primeiro.");
        }

        var items = await db.PlaylistItems.AsNoTracking().Where(i => i.PlaylistId == id)
            .Join(db.Tracks, i => i.TrackHash, t => t.Hash, (i, t) => new { i.Id, t.Title, t.DurationMs })
            .ToDictionaryAsync(i => i.Id, StringComparer.Ordinal, cancellationToken);
        var offerIds = anchors.Select(a => a.OfferId).Distinct().ToList();
        var offers = await db.Offers.Where(o => offerIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(cancellationToken);
        foreach (var anchor in anchors)
        {
            var kind = anchor.Kind ?? PlacementKinds.AnchorDuring;
            if (kind is not (PlacementKinds.AnchorDuring or PlacementKinds.AnchorBetween))
            {
                return ApiResults.BadRequest($"Tipo de âncora desconhecido: {kind}.");
            }

            if (!offers.Contains(anchor.OfferId))
            {
                return ApiResults.BadRequest($"Oferta {anchor.OfferId} não existe.");
            }

            if (!items.TryGetValue(anchor.ItemId, out var item))
            {
                return ApiResults.BadRequest($"Item {anchor.ItemId} não pertence a esta playlist.");
            }

            var maxOffset = Math.Max(0, item.DurationMs - (long)ManifestGenerator.AnchorEndMargin.TotalMilliseconds);
            if (kind == PlacementKinds.AnchorDuring && (anchor.OffsetMs is null || anchor.OffsetMs < 0 || anchor.OffsetMs > maxOffset))
            {
                return ApiResults.BadRequest(
                    $"Em \"{item.Title}\" a oferta pode entrar de 0:00 até {TimeSpan.FromMilliseconds(maxOffset):m\\:ss} (5 s antes do fim).");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var removed = await RemoveAnchorsAsync(db, id, null, cancellationToken);
        foreach (var anchor in anchors)
        {
            var kind = anchor.Kind ?? PlacementKinds.AnchorDuring;
            db.OfferPlacements.Add(new OfferPlacement
            {
                OfferId = anchor.OfferId,
                Kind = kind,
                Mode = kind == PlacementKinds.AnchorDuring ? SpotMode.During : SpotMode.Between,
                PlaylistId = id,
                PlaylistItemId = anchor.ItemId,
                OffsetMs = kind == PlacementKinds.AnchorDuring ? anchor.OffsetMs : null,
            });
        }

        db.Audit(user, time, "playlists.anchors", id.ToString(System.Globalization.CultureInfo.InvariantCulture), new { count = anchors.Count, removed });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("playlists", "offers", $"playlist:{id}");
        return Results.Ok(await DetailAsync(db, id, cancellationToken));
    }

    private static async Task<int> RemoveAnchorsAsync(HubDb db, int playlistId, IReadOnlyList<string>? itemIds, CancellationToken cancellationToken)
    {
        var anchors = db.OfferPlacements.Where(p => p.PlaylistId == playlistId
            && (p.Kind == PlacementKinds.AnchorBetween || p.Kind == PlacementKinds.AnchorDuring));
        if (itemIds is not null)
        {
            anchors = anchors.Where(p => itemIds.Contains(p.PlaylistItemId!));
        }

        return await anchors.ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<PlaylistDetail?> DetailAsync(HubDb db, int id, CancellationToken cancellationToken)
    {
        var playlist = await db.Playlists.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (playlist is null)
        {
            return null;
        }

        var items = await db.PlaylistItems.AsNoTracking().Where(i => i.PlaylistId == id)
            .Join(db.Tracks, i => i.TrackHash, t => t.Hash, (i, t) => new { i.Position, Item = new PlaylistItemDetail(i.Id, t.Hash, t.Title, t.Artist, t.DurationMs) })
            .OrderBy(x => x.Position).Select(x => x.Item)
            .ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, id, cancellationToken);
        var anchors = await OffersEndpoints.AnchorsAsync(db, id, null, cancellationToken);
        return new PlaylistDetail(playlist.Id, playlist.Name, playlist.Shuffle, items, usage[id].ToList(), anchors);
    }

    /// <summary>Lojas e horários que usam cada playlist (seção 7.2, Playlists).</summary>
    private static async Task<ILookup<int, PlaylistUsage>> UsageAsync(HubDb db, int? playlistId, CancellationToken cancellationToken)
    {
        var query = db.Schedules.AsNoTracking();
        if (playlistId is { } only)
        {
            query = query.Where(s => s.PlaylistId == only);
        }

        var rows = await query.Join(db.Stores, s => s.StoreId, s => s.Id, (s, store) => new { s.PlaylistId, s.StoreId, store.Name, s.DaysMask, s.StartTime, s.EndTime })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.StartTime)
            .ToLookup(r => r.PlaylistId, r => new PlaylistUsage(r.StoreId, r.Name, ManifestGenerator.DaysFromMask(r.DaysMask), r.StartTime, r.EndTime));
    }

    private static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength ? $"Nome obrigatório, até {MaxNameLength} caracteres." : null;

    public sealed record PlaylistInput(string? Name, bool? Shuffle);

    public sealed record ItemInput(string? Id, string Hash);

    /// <summary>Âncora de oferta num item; <c>kind</c> ausente = <c>anchorDuring</c> (o painel só cria essa, D24).</summary>
    public sealed record AnchorInput(int OfferId, string ItemId, long? OffsetMs, string? Kind);

    public sealed record PlaylistItemDetail(string Id, string Hash, string Title, string? Artist, long DurationMs);

    /// <summary>Uma janela da agenda de uma loja que toca esta playlist.</summary>
    public sealed record PlaylistUsage(int StoreId, string StoreName, IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End);

    public sealed record PlaylistSummary(int Id, string Name, bool Shuffle, int ItemCount, long DurationMs, DateTimeOffset UpdatedAt, IReadOnlyList<PlaylistUsage> Usage);

    public sealed record PlaylistDetail(
        int Id,
        string Name,
        bool Shuffle,
        IReadOnlyList<PlaylistItemDetail> Items,
        IReadOnlyList<PlaylistUsage> Usage,
        IReadOnlyList<OffersEndpoints.OfferAnchor> Anchors);
}
