using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Ofertas (seções 7.2, 10.3 e 11.2, D24 e D26): cadastro com o áudio já processado, regras de recorrência, lojas,
/// âncoras (somente leitura aqui; editadas na playlist) e execuções por loja.
/// </summary>
public static class OffersEndpoints
{
    public const int MaxNameLength = 80;
    public const int MaxMinutes = 24 * 60;
    public const int MaxEveryTracks = 100;
    public static readonly TimeSpan ExpiringWithin = TimeSpan.FromHours(24);
    public static readonly TimeSpan PlaysWindow = TimeSpan.FromDays(7);

    public static void MapOffers(this RouteGroupBuilder api)
    {
        api.MapGet("/offers", ListAsync).Returns<IReadOnlyList<OfferSummary>>();
        api.MapPost("/offers", CreateAsync).Returns<OfferDetail>(StatusCodes.Status201Created, StatusCodes.Status400BadRequest);
        api.MapGet("/offers/{id:int}", GetAsync).Returns<OfferDetail>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapPatch("/offers/{id:int}", UpdateAsync).Returns<OfferDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapDelete("/offers/{id:int}", DeleteAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status404NotFound);
        api.MapPut("/offers/{id:int}/rules", ReplaceRulesAsync)
            .Returns<OfferDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapGet("/offers/{id:int}/audio", AudioAsync).Produces(StatusCodes.Status200OK, contentType: "audio/mp4").Produces(StatusCodes.Status404NotFound);
        api.MapGet("/offers/{id:int}/plays", PlaysAsync).Returns<IReadOnlyList<OfferStorePlays>>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
    }

    /// <summary>Situação calculada (seção 7.2): desativada, vencida, agendada ou no ar.</summary>
    public static OfferState StateOf(Offer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return !offer.Enabled ? OfferState.Disabled
            : offer.ValidUntil <= now ? OfferState.Expired
            : offer.ValidFrom > now ? OfferState.Scheduled
            : OfferState.Live;
    }

    private static async Task<IResult> ListAsync(HubDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var offers = await db.Offers.AsNoTracking().ToListAsync(cancellationToken);
        var context = await SummaryContextAsync(db, null, now, cancellationToken);
        return Results.Ok(offers.OrderBy(o => StateOf(o, now)).ThenBy(o => o.ValidUntil).Select(o => Summary(o, context, now)));
    }

    private static async Task<IResult> GetAsync(int id, HubDb db, TimeProvider time, CancellationToken cancellationToken) =>
        await DetailAsync(db, id, time.GetUtcNow(), cancellationToken) is { } detail ? Results.Ok(detail) : Results.NotFound();

    private static async Task<IResult> CreateAsync(
        OfferInput input, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (input.Name is null || input.AudioHash is null || input.ValidFrom is null || input.ValidUntil is null)
        {
            return ApiResults.BadRequest("Nome, áudio e validade (início e fim) são obrigatórios.");
        }

        var now = time.GetUtcNow();
        var offer = new Offer
        {
            Name = input.Name,
            AudioHash = input.AudioHash,
            ValidFrom = input.ValidFrom.Value,
            ValidUntil = input.ValidUntil.Value,
            DuckLevel = input.DuckLevel ?? 30,
            Enabled = input.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (await ValidateAsync(db, offer, input, cancellationToken) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        offer.Name = offer.Name.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Offers.Add(offer);
        await db.SaveChangesAsync(cancellationToken);
        ReplaceStores(db, offer.Id, input.StoreIds ?? []);
        await WriteRulesAsync(db, offer.Id, input.Rules ?? [], cancellationToken);
        db.Audit(user, time, "offers.create", Key(offer.Id), input);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("offers");
        return Results.Created($"/api/offers/{offer.Id}", await DetailAsync(db, offer.Id, now, cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(
        int id, OfferInput input, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var offer = await db.Offers.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (offer is null)
        {
            return Results.NotFound();
        }

        offer.Name = input.Name ?? offer.Name;
        offer.AudioHash = input.AudioHash ?? offer.AudioHash;
        offer.ValidFrom = input.ValidFrom ?? offer.ValidFrom;
        offer.ValidUntil = input.ValidUntil ?? offer.ValidUntil;
        offer.DuckLevel = input.DuckLevel ?? offer.DuckLevel;
        offer.Enabled = input.Enabled ?? offer.Enabled;
        if (await ValidateAsync(db, offer, input, cancellationToken) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        offer.Name = offer.Name.Trim();
        offer.UpdatedAt = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (input.StoreIds is not null)
        {
            ReplaceStores(db, id, input.StoreIds);
        }

        if (input.Rules is not null)
        {
            await WriteRulesAsync(db, id, input.Rules, cancellationToken);
        }

        db.Audit(user, time, "offers.update", Key(id), input);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("offers");
        return Results.Ok(await DetailAsync(db, id, time.GetUtcNow(), cancellationToken));
    }

    private static async Task<IResult> ReplaceRulesAsync(
        int id, List<OfferRule> rules, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!await db.Offers.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        if (await ValidateRulesAsync(db, rules, cancellationToken) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await WriteRulesAsync(db, id, rules, cancellationToken);
        db.Audit(user, time, "offers.rules", Key(id), rules);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("offers");
        return Results.Ok(await DetailAsync(db, id, time.GetUtcNow(), cancellationToken));
    }

    /// <summary>Apaga a oferta, as regras, as âncoras e as lojas dela; as execuções ficam no histórico (D26 h).</summary>
    private static async Task<IResult> DeleteAsync(
        int id, HubDb db, ManifestPublisher publisher, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var offer = await db.Offers.SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (offer is null)
        {
            return Results.NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.OfferPlacements.Where(p => p.OfferId == id).ExecuteDeleteAsync(cancellationToken);
        await db.OfferStores.Where(s => s.OfferId == id).ExecuteDeleteAsync(cancellationToken);
        db.Offers.Remove(offer);
        await db.SaveChangesAsync(cancellationToken);

        // O áudio de oferta não aparece na Biblioteca: sem outra oferta usando, sai junto (exclusão lógica).
        if (!await db.Offers.AnyAsync(o => o.AudioHash == offer.AudioHash, cancellationToken)
            && await db.Tracks.SingleOrDefaultAsync(t => t.Hash == offer.AudioHash && t.Kind == TrackKind.Offer, cancellationToken) is { } audio)
        {
            audio.DeletedAt = time.GetUtcNow();
        }

        db.Audit(user, time, "offers.delete", Key(id), new { offer.Name });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("offers", "playlists");
        return Results.NoContent();
    }

    private static async Task<IResult> AudioAsync(int id, HubDb db, HubPaths paths, CancellationToken cancellationToken)
    {
        var hash = await db.Offers.Where(o => o.Id == id).Select(o => o.AudioHash).SingleOrDefaultAsync(cancellationToken);
        return hash is not null && File.Exists(paths.LibraryFile(hash))
            ? Results.File(paths.LibraryFile(hash), "audio/mp4", enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static async Task<IResult> PlaysAsync(int id, HubDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!await db.Offers.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok(await PlaysByStoreAsync(db, id, time.GetUtcNow(), cancellationToken));
    }

    /// <summary>Âncoras de uma playlist ou de uma oferta, com os nomes para mostrar (seção 7.2).</summary>
    public static async Task<List<OfferAnchor>> AnchorsAsync(HubDb db, int? playlistId, int? offerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.OfferPlacements.AsNoTracking().Where(p => p.Kind == PlacementKinds.AnchorDuring || p.Kind == PlacementKinds.AnchorBetween);
        if (playlistId is { } playlist)
        {
            query = query.Where(p => p.PlaylistId == playlist);
        }

        if (offerId is { } offer)
        {
            query = query.Where(p => p.OfferId == offer);
        }

        var rows = await query
            .Join(db.Offers, p => p.OfferId, o => o.Id, (p, o) => new { Placement = p, OfferName = o.Name })
            .Join(db.Playlists, x => x.Placement.PlaylistId, pl => pl.Id, (x, pl) => new { x.Placement, x.OfferName, PlaylistName = pl.Name })
            .Join(db.PlaylistItems, x => x.Placement.PlaylistItemId, i => i.Id, (x, i) => new { x.Placement, x.OfferName, x.PlaylistName, i.Position, i.TrackHash })
            .Join(db.Tracks, x => x.TrackHash, t => t.Hash, (x, t) => new { x.Placement, x.OfferName, x.PlaylistName, x.Position, t.Title })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.PlaylistName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Position)
            .Select(r => new OfferAnchor(
                r.Placement.Id, r.Placement.OfferId, r.OfferName, r.Placement.PlaylistId!.Value, r.PlaylistName, r.Placement.PlaylistItemId!, r.Position,
                r.Title, r.Placement.Kind, r.Placement.OffsetMs))
            .ToList();
    }

    private static async Task<OfferDetail?> DetailAsync(HubDb db, int id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var offer = await db.Offers.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (offer is null)
        {
            return null;
        }

        var context = await SummaryContextAsync(db, id, now, cancellationToken);
        var rules = await db.OfferPlacements.AsNoTracking()
            .Where(p => p.OfferId == id && p.Kind != PlacementKinds.AnchorDuring && p.Kind != PlacementKinds.AnchorBetween)
            .OrderBy(p => p.Id).ToListAsync(cancellationToken);
        return new OfferDetail(
            Summary(offer, context, now),
            rules.Select(ToRule).ToList(),
            await AnchorsAsync(db, null, id, cancellationToken),
            await PlaysByStoreAsync(db, id, now, cancellationToken));
    }

    private static async Task<List<OfferStorePlays>> PlaysByStoreAsync(HubDb db, int offerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - PlaysWindow;
        var rows = await db.OfferPlays.AsNoTracking().Where(p => p.OfferId == offerId && p.StartedAt > since)
            .GroupBy(p => new { p.StoreId, p.Result }).Select(g => new { g.Key.StoreId, g.Key.Result, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var stores = await db.Stores.AsNoTracking().Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);
        return rows.GroupBy(r => r.StoreId)
            .Select(g => new OfferStorePlays(
                g.Key,
                stores.GetValueOrDefault(g.Key) ?? "Loja removida",
                // Sem resultado ainda = tocando agora; conta como tocada.
                g.Where(r => r.Result is null or "played").Sum(r => r.Count),
                g.Where(r => r.Result == "interrupted").Sum(r => r.Count),
                g.Where(r => r.Result == "skipped").Sum(r => r.Count)))
            .OrderBy(p => p.StoreName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<SummaryContext> SummaryContextAsync(HubDb db, int? offerId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stores = db.OfferStores.AsNoTracking();
        var placements = db.OfferPlacements.AsNoTracking();
        var plays = db.OfferPlays.AsNoTracking().Where(p => p.StartedAt > now - PlaysWindow && (p.Result == null || p.Result == "played"));
        if (offerId is { } id)
        {
            stores = stores.Where(s => s.OfferId == id);
            placements = placements.Where(p => p.OfferId == id);
            plays = plays.Where(p => p.OfferId == id);
        }

        var durations = await db.Tracks.AsNoTracking().Where(t => t.Kind == TrackKind.Offer)
            .ToDictionaryAsync(t => t.Hash, t => t.DurationMs, StringComparer.Ordinal, cancellationToken);
        var playCounts = await plays.Where(p => p.OfferId != null).GroupBy(p => p.OfferId!.Value)
            .Select(g => new { OfferId = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.OfferId, x => x.Count, cancellationToken);
        return new SummaryContext(
            (await stores.ToListAsync(cancellationToken)).ToLookup(s => s.OfferId, s => s.StoreId),
            (await placements.Select(p => new { p.OfferId, p.Kind }).ToListAsync(cancellationToken)).ToLookup(p => p.OfferId, p => p.Kind),
            durations,
            playCounts);
    }

    private static OfferSummary Summary(Offer offer, SummaryContext context, DateTimeOffset now)
    {
        var kinds = context.Placements[offer.Id].ToList();
        var state = StateOf(offer, now);
        return new OfferSummary(
            offer.Id,
            ManifestGenerator.OfferKey(offer.Id),
            offer.Name,
            offer.AudioHash,
            context.Durations.GetValueOrDefault(offer.AudioHash),
            offer.ValidFrom,
            offer.ValidUntil,
            offer.DuckLevel,
            offer.Enabled,
            context.Stores[offer.Id].Order().ToList(),
            state,
            state is OfferState.Live or OfferState.Scheduled && offer.ValidUntil - now <= ExpiringWithin,
            kinds.Count(k => k is not (PlacementKinds.AnchorDuring or PlacementKinds.AnchorBetween)),
            kinds.Count(k => k is PlacementKinds.AnchorDuring or PlacementKinds.AnchorBetween),
            context.PlayCounts.GetValueOrDefault(offer.Id),
            offer.UpdatedAt);
    }

    private static async Task<string?> ValidateAsync(HubDb db, Offer offer, OfferInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(offer.Name) || offer.Name.Trim().Length > MaxNameLength)
        {
            return $"Nome obrigatório, até {MaxNameLength} caracteres.";
        }

        if (offer.ValidUntil <= offer.ValidFrom)
        {
            return "O fim da validade precisa ser depois do início.";
        }

        if (offer.DuckLevel is < 0 or > 100)
        {
            return "O nível da música durante a oferta vai de 0% a 100%.";
        }

        if (!await db.Tracks.AnyAsync(t => t.Hash == offer.AudioHash && t.Kind == TrackKind.Offer && t.DeletedAt == null, cancellationToken))
        {
            return "Áudio da oferta não encontrado. Envie o áudio pela tela de Ofertas e espere o processamento terminar.";
        }

        if (input.StoreIds is { Count: > 0 } storeIds)
        {
            var known = await db.Stores.Where(s => storeIds.Contains(s.Id)).CountAsync(cancellationToken);
            if (known != storeIds.Distinct().Count())
            {
                return "Uma das lojas escolhidas não existe.";
            }
        }

        return input.Rules is null ? null : await ValidateRulesAsync(db, input.Rules, cancellationToken);
    }

    private static async Task<string?> ValidateRulesAsync(HubDb db, IReadOnlyList<OfferRule> rules, CancellationToken cancellationToken)
    {
        foreach (var rule in rules)
        {
            var error = rule.Kind switch
            {
                PlacementKinds.EveryMinutes when rule.Minutes is not (>= 1 and <= MaxMinutes) => $"\"A cada X minutos\" precisa de 1 a {MaxMinutes} minutos.",
                PlacementKinds.Clock when rule.Time is null => "\"Horário fixo\" precisa da hora.",
                PlacementKinds.Clock when rule.Days is not { Count: > 0 } || rule.Days.Any(d => d is < 0 or > 6) => "\"Horário fixo\" precisa de pelo menos um dia da semana.",
                PlacementKinds.EveryTracks when rule.N is not (>= 1 and <= MaxEveryTracks) => $"\"A cada N músicas\" precisa de 1 a {MaxEveryTracks} músicas.",
                PlacementKinds.EveryMinutes or PlacementKinds.Clock or PlacementKinds.EveryTracks => null,
                _ => $"Regra desconhecida: {rule.Kind}.",
            };
            if (error is not null)
            {
                return error;
            }

            if (rule.PlaylistIds is { Count: > 0 } ids
                && await db.Playlists.Where(p => ids.Contains(p.Id)).CountAsync(cancellationToken) != ids.Distinct().Count())
            {
                return "Uma das playlists da regra não existe.";
            }
        }

        return null;
    }

    private static void ReplaceStores(HubDb db, int offerId, IReadOnlyList<int> storeIds)
    {
        db.OfferStores.RemoveRange(db.OfferStores.Where(s => s.OfferId == offerId));
        db.OfferStores.AddRange(storeIds.Distinct().Select(storeId => new OfferStore { OfferId = offerId, StoreId = storeId }));
    }

    /// <summary>Substitui as regras (não mexe nas âncoras, que são da playlist).</summary>
    private static async Task WriteRulesAsync(HubDb db, int offerId, IReadOnlyList<OfferRule> rules, CancellationToken cancellationToken)
    {
        await db.OfferPlacements
            .Where(p => p.OfferId == offerId && p.Kind != PlacementKinds.AnchorDuring && p.Kind != PlacementKinds.AnchorBetween)
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var rule in rules)
        {
            db.OfferPlacements.Add(new OfferPlacement
            {
                OfferId = offerId,
                Kind = rule.Kind,
                // Pela D24 o painel só cria "durante"; "a cada N músicas" é sempre entre músicas (seção 10.3.4).
                Mode = rule.Kind == PlacementKinds.EveryTracks ? SpotMode.Between : rule.Mode ?? SpotMode.During,
                EveryMinutes = rule.Kind == PlacementKinds.EveryMinutes ? rule.Minutes : null,
                EveryN = rule.Kind == PlacementKinds.EveryTracks ? rule.N : null,
                ClockTime = rule.Kind == PlacementKinds.Clock ? rule.Time : null,
                DaysMask = rule.Kind == PlacementKinds.Clock ? ManifestGenerator.MaskFromDays(rule.Days ?? []) : null,
                PlaylistIdsJson = rule.PlaylistIds is { Count: > 0 } ids ? JsonSerializer.Serialize(ids.Distinct().Order()) : null,
            });
        }
    }

    private static OfferRule ToRule(OfferPlacement placement) => new(
        placement.Kind,
        placement.Id,
        placement.Mode,
        placement.EveryMinutes,
        placement.ClockTime,
        placement.DaysMask is { } mask ? ManifestGenerator.DaysFromMask(mask) : null,
        placement.EveryN,
        string.IsNullOrEmpty(placement.PlaylistIdsJson) ? null : JsonSerializer.Deserialize<List<int>>(placement.PlaylistIdsJson));

    private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

    private sealed record SummaryContext(
        ILookup<int, int> Stores, ILookup<int, string> Placements, Dictionary<string, long> Durations, Dictionary<int, int> PlayCounts);

    public enum OfferState
    {
        Live,
        Scheduled,
        Disabled,
        Expired,
    }

    /// <summary>Corpo de criação e edição; em PATCH os campos nulos ficam como estão.</summary>
    public sealed record OfferInput(
        string? Name,
        string? AudioHash,
        DateTimeOffset? ValidFrom,
        DateTimeOffset? ValidUntil,
        int? DuckLevel,
        bool? Enabled,
        IReadOnlyList<int>? StoreIds,
        IReadOnlyList<OfferRule>? Rules);

    /// <summary>Regra de recorrência (seção 10.3.4): <c>everyMinutes</c>, <c>clock</c> ou <c>everyTracks</c>.</summary>
    public sealed record OfferRule(
        string Kind,
        int? Id,
        SpotMode? Mode,
        int? Minutes,
        TimeOnly? Time,
        IReadOnlyList<int>? Days,
        int? N,
        IReadOnlyList<int>? PlaylistIds);

    /// <param name="Key">Identificador da oferta no manifesto e nos comandos (<c>of-12</c>).</param>
    /// <param name="StoreIds">Vazio = todas as lojas.</param>
    /// <param name="Plays7d">Execuções tocadas nos últimos 7 dias, somando as lojas.</param>
    public sealed record OfferSummary(
        int Id,
        string Key,
        string Name,
        string AudioHash,
        long DurationMs,
        DateTimeOffset ValidFrom,
        DateTimeOffset ValidUntil,
        int DuckLevel,
        bool Enabled,
        IReadOnlyList<int> StoreIds,
        OfferState Status,
        bool ExpiresSoon,
        int RuleCount,
        int AnchorCount,
        int Plays7d,
        DateTimeOffset UpdatedAt);

    public sealed record OfferDetail(OfferSummary Offer, IReadOnlyList<OfferRule> Rules, IReadOnlyList<OfferAnchor> Anchors, IReadOnlyList<OfferStorePlays> Plays);

    public sealed record OfferAnchor(
        int Id,
        int OfferId,
        string OfferName,
        int PlaylistId,
        string PlaylistName,
        string ItemId,
        int Position,
        string TrackTitle,
        string Kind,
        long? OffsetMs);

    /// <summary>Execuções de uma oferta numa loja nos últimos 7 dias.</summary>
    public sealed record OfferStorePlays(int StoreId, string StoreName, int Played, int Interrupted, int Skipped);
}
