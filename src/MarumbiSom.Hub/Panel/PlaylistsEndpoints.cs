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
        api.MapGet("/playlists", ListAsync);
        api.MapPost("/playlists", CreateAsync);
        api.MapGet("/playlists/{id:int}", GetAsync);
        api.MapPatch("/playlists/{id:int}", UpdateAsync);
        api.MapDelete("/playlists/{id:int}", DeleteAsync);
        api.MapPut("/playlists/{id:int}/items", ReplaceItemsAsync);
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
        return Results.Ok(playlists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    private static async Task<IResult> CreateAsync(PlaylistInput input, HubDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (ValidateName(input.Name) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        var now = time.GetUtcNow();
        var playlist = new Playlist { Name = input.Name!.Trim(), Shuffle = input.Shuffle ?? false, CreatedAt = now, UpdatedAt = now };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync(cancellationToken);
        db.Audit(user, time, "playlists.create", playlist.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), input);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/playlists/{playlist.Id}", await DetailAsync(db, playlist.Id, cancellationToken));
    }

    private static async Task<IResult> GetAsync(int id, HubDb db, CancellationToken cancellationToken) =>
        await DetailAsync(db, id, cancellationToken) is { } detail ? Results.Ok(detail) : Results.NotFound();

    private static async Task<IResult> UpdateAsync(
        int id, PlaylistInput input, HubDb db, ManifestPublisher publisher, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
                return Results.BadRequest(new { error });
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
        return Results.Ok(await DetailAsync(db, id, cancellationToken));
    }

    private static async Task<IResult> DeleteAsync(int id, HubDb db, ManifestPublisher publisher, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
        return Results.NoContent();
    }

    /// <summary>
    /// Substitui a lista ordenada inteira (idempotente). Itens existentes mantêm o <c>id</c>; itens novos vêm sem <c>id</c>.
    /// Itens removidos levam junto as âncoras de oferta presas a eles.
    /// </summary>
    private static async Task<IResult> ReplaceItemsAsync(
        int id, List<ItemInput> items, HubDb db, ManifestPublisher publisher, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
            return Results.BadRequest(new { error = $"Item {unknown} não pertence a esta playlist." });
        }

        if (givenIds.Count != givenIds.Distinct(StringComparer.Ordinal).Count())
        {
            return Results.BadRequest(new { error = "O mesmo item aparece duas vezes." });
        }

        var hashes = items.Select(i => i.Hash).Distinct(StringComparer.Ordinal).ToList();
        var valid = await db.Tracks.Where(t => hashes.Contains(t.Hash) && t.DeletedAt == null && t.Kind == TrackKind.Music)
            .Select(t => t.Hash).ToListAsync(cancellationToken);
        if (hashes.Except(valid, StringComparer.Ordinal).FirstOrDefault() is { } missing)
        {
            return Results.BadRequest(new { error = $"Música {missing} não existe na biblioteca." });
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
        return new PlaylistDetail(playlist.Id, playlist.Name, playlist.Shuffle, items);
    }

    private static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength ? $"Nome obrigatório, até {MaxNameLength} caracteres." : null;

    public sealed record PlaylistInput(string? Name, bool? Shuffle);

    public sealed record ItemInput(string? Id, string Hash);

    public sealed record PlaylistItemDetail(string Id, string Hash, string Title, string? Artist, long DurationMs);

    public sealed record PlaylistDetail(int Id, string Name, bool Shuffle, IReadOnlyList<PlaylistItemDetail> Items);
}
