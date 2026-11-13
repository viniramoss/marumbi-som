using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>Lojas, agenda, vinculação e comandos ao vivo (seção 11.2).</summary>
public static class StoresEndpoints
{
    /// <summary>Quanto o painel espera pelo resultado de um comando (meta de 2 s na Etapa 4).</summary>
    public static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(5);

    public static void MapStores(this RouteGroupBuilder api)
    {
        api.MapGet("/stores", ListAsync).Returns<IReadOnlyList<StoreSummary>>();
        api.MapPost("/stores", CreateAsync).Returns<StoreDetail>(StatusCodes.Status201Created, StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict);
        api.MapGet("/stores/{id:int}", GetAsync).Returns<StoreDetail>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapPatch("/stores/{id:int}", UpdateAsync).Returns<StoreDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapDelete("/stores/{id:int}", DeleteAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status404NotFound);
        api.MapPut("/stores/{id:int}/schedules", ReplaceSchedulesAsync)
            .Returns<StoreDetail>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapPost("/stores/{id:int}/enroll-code", EnrollCodeAsync).Returns<EnrollCodeResult>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapDelete("/stores/{id:int}/token", RevokeTokenAsync).ReturnsNothing(StatusCodes.Status204NoContent, StatusCodes.Status404NotFound);
        api.MapPost("/stores/{id:int}/commands", CommandAsync)
            .Returns<CommandOutcome>(StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
        api.MapGet("/stores/{id:int}/tracks", TracksAsync).Returns<IReadOnlyList<StoreTrack>>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapGet("/stores/{id:int}/offers", OffersAsync).Returns<IReadOnlyList<StoreOffer>>(StatusCodes.Status200OK, StatusCodes.Status404NotFound);
        api.MapGet("/stores/{id:int}/history", HistoryAsync).Returns<IReadOnlyList<HistoryEntry>>();
        api.MapGet("/stores/{id:int}/events", EventsAsync).Returns<IReadOnlyList<EventEntry>>();
    }

    private static async Task<IResult> ListAsync(HubDb db, StoreStatusReader status, CancellationToken cancellationToken) =>
        Results.Ok(await status.ListAsync(db, cancellationToken));

    private static async Task<IResult> CreateAsync(
        StoreInput input, HubDb db, ManifestPublisher publisher, StoreStatusReader status, PanelHub panel, ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80)
        {
            return ApiResults.BadRequest("Nome obrigatório, até 80 caracteres.");
        }

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slugify(input.Name) : input.Slug.Trim();
        if (slug.Length is < 2 or > 40 || !slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return ApiResults.BadRequest("Identificador inválido: use letras minúsculas sem acento, números e hífen (2 a 40).");
        }

        if (await db.Stores.AnyAsync(s => s.Slug == slug, cancellationToken))
        {
            return ApiResults.Conflict($"Já existe uma loja com o identificador {slug}.");
        }

        var store = new Store { Slug = slug, Name = input.Name.Trim(), CreatedAt = time.GetUtcNow() };
        if (Apply(store, input) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        db.Stores.Add(store);
        await db.SaveChangesAsync(cancellationToken);
        db.StoreStates.Add(new StoreState { StoreId = store.Id });
        db.Audit(user, time, "stores.create", slug, input);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("stores");
        return Results.Created($"/api/stores/{store.Id}", await DetailAsync(db, status, store.Id, cancellationToken));
    }

    private static async Task<IResult> GetAsync(int id, HubDb db, StoreStatusReader status, CancellationToken cancellationToken) =>
        await DetailAsync(db, status, id, cancellationToken) is { } detail ? Results.Ok(detail) : Results.NotFound();

    private static async Task<IResult> UpdateAsync(
        int id, StoreInput input, HubDb db, ManifestPublisher publisher, StoreStatusReader status, PanelHub panel, ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var store = await db.Stores.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        if (input.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80)
            {
                return ApiResults.BadRequest("Nome obrigatório, até 80 caracteres.");
            }

            store.Name = input.Name.Trim();
        }

        if (Apply(store, input) is { } error)
        {
            return ApiResults.BadRequest(error);
        }

        db.Audit(user, time, "stores.update", store.Slug, input);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("stores", $"store:{id}");
        panel.StoreChanged(id);
        return Results.Ok(await DetailAsync(db, status, id, cancellationToken));
    }

    /// <summary>Remove a loja e tudo dela (agenda, manifestos, histórico); o PC da loja segue tocando o que tem.</summary>
    private static async Task<IResult> DeleteAsync(
        int id, HubDb db, AgentConnections agents, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var store = await db.Stores.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        await db.OfferPlays.Where(p => p.StoreId == id).ExecuteDeleteAsync(cancellationToken);
        db.Stores.Remove(store);
        db.Audit(user, time, "stores.delete", store.Slug, new { store.Name });
        await db.SaveChangesAsync(cancellationToken);
        await agents.DisconnectAsync(id, "loja removida");
        panel.Changed("stores");
        return Results.NoContent();
    }

    private static async Task<IResult> ReplaceSchedulesAsync(
        int id, List<ScheduleInput> schedules, HubDb db, ManifestPublisher publisher, StoreStatusReader status, PanelHub panel, ClaimsPrincipal user,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var store = await db.Stores.Include(s => s.Schedules).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        var playlistIds = await db.Playlists.Select(p => p.Id).ToListAsync(cancellationToken);
        foreach (var schedule in schedules)
        {
            if (!playlistIds.Contains(schedule.PlaylistId))
            {
                return ApiResults.BadRequest($"Playlist {schedule.PlaylistId} não existe.");
            }

            if (schedule.Days is not { Count: > 0 } || schedule.Days.Any(d => d is < 0 or > 6))
            {
                return ApiResults.BadRequest("Informe os dias da semana (0 = domingo … 6 = sábado).");
            }
        }

        if (Overlap(schedules) is { } overlap)
        {
            return ApiResults.BadRequest(overlap);
        }

        db.Schedules.RemoveRange(store.Schedules);
        foreach (var schedule in schedules)
        {
            db.Schedules.Add(new Schedule
            {
                StoreId = id,
                PlaylistId = schedule.PlaylistId,
                DaysMask = ManifestGenerator.MaskFromDays(schedule.Days),
                StartTime = schedule.Start,
                EndTime = schedule.End,
            });
        }

        db.Audit(user, time, "stores.schedules", store.Slug, schedules);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        panel.Changed("playlists", $"store:{id}");
        panel.StoreChanged(id);
        return Results.Ok(await DetailAsync(db, status, id, cancellationToken));
    }

    private static async Task<IResult> EnrollCodeAsync(int id, HubDb db, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var store = await db.Stores.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        var now = time.GetUtcNow();
        var code = AgentEndpoints.NewEnrollCode();
        await db.EnrollCodes.Where(c => c.StoreId == id && c.UsedAt == null).ExecuteDeleteAsync(cancellationToken);
        db.EnrollCodes.Add(new EnrollCode
        {
            CodeHash = AgentEndpoints.HashSecret(AgentEndpoints.NormalizeCode(code)),
            StoreId = id,
            ExpiresAt = now + AgentEndpoints.EnrollCodeLifetime,
        });
        db.Audit(user, time, "stores.enroll-code", store.Slug);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new EnrollCodeResult(code, now + AgentEndpoints.EnrollCodeLifetime));
    }

    private static async Task<IResult> RevokeTokenAsync(
        int id, HubDb db, AgentConnections agents, PanelHub panel, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var store = await db.Stores.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        store.TokenHash = null;
        db.Audit(user, time, "stores.revoke-token", store.Slug);
        await db.SaveChangesAsync(cancellationToken);
        await agents.DisconnectAsync(id, "token revogado");
        panel.StoreChanged(id);
        return Results.NoContent();
    }

    private static async Task<IResult> CommandAsync(
        int id, CommandInput input, HubDb db, AgentConnections agents, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        var store = await db.Stores.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return Results.NotFound();
        }

        if (!CommandTypes.All.Contains(input.Command))
        {
            return ApiResults.BadRequest($"Comando desconhecido: {input.Command}.");
        }

        var args = input.Args;
        if (input.Command == CommandTypes.UpdateAgent)
        {
            // O Hub completa o comando com a versão, o SHA-256 e a assinatura do pacote (D27 d).
            var wanted = (string?)args?["version"];
            var release = await db.AgentReleases.AsNoTracking()
                .SingleOrDefaultAsync(r => wanted == null ? r.IsCurrent : r.Version == wanted, cancellationToken);
            if (release is null)
            {
                return ApiResults.BadRequest(wanted is null
                    ? "Nenhuma versão do agente para distribuição ainda. Ela chega do GitHub (veja em Configurações) ou pode ser enviada lá."
                    : $"A versão {wanted} do agente não foi enviada ao Hub.");
            }

            args = JsonSerializer.SerializeToNode(ReleasesEndpoints.ToAgentInfo(release), ContractJson.Options)!.AsObject();
        }

        var now = time.GetUtcNow();
        var command = new StoreCommand { StoreId = id, Type = input.Command, ArgsJson = args?.ToJsonString(), CreatedAt = now };
        db.Commands.Add(command);
        db.Audit(user, time, "stores.command", store.Slug, new { input.Command, args = args?.ToJsonString() });
        await db.SaveChangesAsync(cancellationToken);

        var commandId = command.Id.ToString(CultureInfo.InvariantCulture);
        if (!agents.IsOnline(id))
        {
            command.Result = "offline";
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(new CommandOutcome(command.Id, "offline", "A loja está desconectada do Hub."));
        }

        command.SentAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var result = await agents.SendCommandAsync(id, new CommandMessage(commandId, input.Command, args), CommandWait, cancellationToken);
        return result is null
            ? Results.Ok(new CommandOutcome(command.Id, "pending", "A loja não respondeu a tempo; o resultado aparece no histórico."))
            : Results.Ok(new CommandOutcome(command.Id, result.Ok ? "ok" : "error", result.Ok ? null : AgentError(result.Error)));
    }

    /// <summary>Agente de versão anterior a um comando novo responde "Comando desconhecido" (D37 f).</summary>
    private static string? AgentError(string? error) =>
        error is not null && error.StartsWith("Comando desconhecido", StringComparison.Ordinal)
            ? "O agente desta loja é de uma versão antiga e não conhece este comando. Atualize o agente da loja e tente de novo."
            : error;

    /// <summary>Músicas do manifesto que a loja aplicou (para "tocar agora"); sem manifesto aplicado, as do mais recente.</summary>
    private static async Task<IResult> TracksAsync(int id, HubDb db, CancellationToken cancellationToken)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        var applied = await db.StoreStates.Where(s => s.StoreId == id).Select(s => s.AppliedVersion).SingleOrDefaultAsync(cancellationToken);
        var stored = await db.StoreManifests.AsNoTracking().Where(m => m.StoreId == id && m.Version == applied).SingleOrDefaultAsync(cancellationToken)
            ?? await ManifestPublisher.LatestAsync(db, id, cancellationToken);
        if (stored is null)
        {
            return Results.Ok(Array.Empty<StoreTrack>());
        }

        var manifest = ManifestPublisher.Deserialize(stored);
        var tracks = manifest.Tracks.Where(t => t.Value.Kind == Contracts.Manifests.TrackKind.Music)
            .Select(t => new StoreTrack(t.Key, t.Value.Title, t.Value.Artist, t.Value.DurationMs))
            .OrderBy(t => t.Artist ?? t.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Results.Ok(tracks);
    }

    /// <summary>Ofertas do manifesto que a loja aplicou e que valem agora (para "tocar oferta agora", D26 g).</summary>
    private static async Task<IResult> OffersAsync(int id, HubDb db, TimeProvider time, CancellationToken cancellationToken)
    {
        if (!await db.Stores.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return Results.NotFound();
        }

        var applied = await db.StoreStates.Where(s => s.StoreId == id).Select(s => s.AppliedVersion).SingleOrDefaultAsync(cancellationToken);
        var stored = await db.StoreManifests.AsNoTracking().Where(m => m.StoreId == id && m.Version == applied).SingleOrDefaultAsync(cancellationToken)
            ?? await ManifestPublisher.LatestAsync(db, id, cancellationToken);
        if (stored is null)
        {
            return Results.Ok(Array.Empty<StoreOffer>());
        }

        var now = time.GetUtcNow();
        var manifest = ManifestPublisher.Deserialize(stored);
        var offers = manifest.Spots.Offers.Where(o => o.Value.IsValidAt(now))
            .Select(o => new StoreOffer(
                o.Key, ManifestGenerator.ParseKey(o.Key, "of-"), o.Value.Name, manifest.Tracks.GetValueOrDefault(o.Value.Hash)?.DurationMs ?? 0, o.Value.ValidUntil))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return Results.Ok(offers);
    }

    /// <summary>O que tocou na loja: músicas e ofertas (D26 g), da mais recente para a mais antiga.</summary>
    private static async Task<IResult> HistoryAsync(int id, HubDb db, int? limit, DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 100, 1, 2000);
        var query = db.PlayHistory.AsNoTracking().Where(h => h.StoreId == id);
        var plays = db.OfferPlays.AsNoTracking().Where(p => p.StoreId == id && (p.Result == null || p.Result != "skipped"));
        if (since is { } from)
        {
            query = query.Where(h => h.StartedAt >= from);
            plays = plays.Where(p => p.StartedAt >= from);
        }

        var tracks = await query.OrderByDescending(h => h.StartedAt).Take(take)
            .GroupJoin(db.Tracks, h => h.TrackHash, t => t.Hash, (h, tracks) => new { h, tracks })
            .SelectMany(
                x => x.tracks.DefaultIfEmpty(),
                (x, t) => new HistoryEntry(x.h.TrackHash, x.h.StartedAt, t != null ? t.Title : null, t != null ? t.Artist : null, HistoryKind.Music, null))
            .ToListAsync(cancellationToken);
        var offers = await plays.OrderByDescending(p => p.StartedAt).Take(take)
            .GroupJoin(db.Offers, p => p.OfferId, o => o.Id, (p, offers) => new { p, offers })
            .SelectMany(
                x => x.offers.DefaultIfEmpty(),
                (x, o) => new HistoryEntry(o != null ? o.AudioHash : null, x.p.StartedAt, o != null ? o.Name : null, null, HistoryKind.Offer, x.p.Result))
            .ToListAsync(cancellationToken);
        return Results.Ok(tracks.Concat(offers).OrderByDescending(h => h.StartedAt).Take(take));
    }

    private static async Task<IResult> EventsAsync(int id, HubDb db, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 100, 1, 1000);
        var events = await db.Events.AsNoTracking().Where(e => e.StoreId == id).OrderByDescending(e => e.CreatedAt).Take(take)
            .Select(e => new { e.Type, e.DataJson, e.CreatedAt }).ToListAsync(cancellationToken);
        return Results.Ok(events.Select(e => new EventEntry(e.Type, e.DataJson is null ? null : JsonNode.Parse(e.DataJson) as JsonObject, e.CreatedAt)));
    }

    private static string? Apply(Store store, StoreInput input)
    {
        if (input.Volume is { } volume)
        {
            if (volume is < 0 or > 100)
            {
                return "Volume deve ficar entre 0 e 100.";
            }

            store.Volume = volume;
        }

        if (input.CrossfadeS is { } crossfade)
        {
            if (crossfade is < 0 or > 30)
            {
                return "Mixagem deve ficar entre 0 e 30 segundos.";
            }

            store.CrossfadeS = crossfade;
        }

        if (input.MinOfferGapS is { } gap)
        {
            if (gap is < 0 or > 86400)
            {
                return "Intervalo mínimo entre ofertas inválido.";
            }

            store.MinOfferGapS = gap;
        }

        if (input.Timezone is { } timezone)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"Fuso horário desconhecido: {timezone}.";
            }

            store.Timezone = timezone;
        }

        if (input.BtDeviceName is not null)
        {
            store.BtDeviceName = string.IsNullOrWhiteSpace(input.BtDeviceName) ? null : input.BtDeviceName.Trim();
        }

        return null;
    }

    /// <summary>Duas janelas no mesmo dia não podem se sobrepor: a playlist ativa precisa ser única (seção 10.2).</summary>
    private static string? Overlap(IReadOnlyList<ScheduleInput> schedules)
    {
        static IEnumerable<(int Start, int End)> Minutes(ScheduleInput s, int day)
        {
            var start = (int)s.Start.ToTimeSpan().TotalMinutes;
            var end = s.End == TimeOnly.MaxValue ? 1440 : (int)s.End.ToTimeSpan().TotalMinutes;
            if (end > start)
            {
                yield return (day * 1440 + start, day * 1440 + end);
            }
            else
            {
                // Atravessa a meia-noite (ou dura 24 h quando início = fim).
                yield return (day * 1440 + start, (day * 1440) + 1440 + end);
            }
        }

        var ranges = schedules.SelectMany(s => s.Days.SelectMany(d => Minutes(s, d)).Select(r => (r.Start, r.End, s))).ToList();
        // Uma janela de sábado que passa da meia-noite encosta no domingo seguinte.
        ranges.AddRange(ranges.Where(r => r.End > 7 * 1440).Select(r => (r.Start - (7 * 1440), r.End - (7 * 1440), r.s)).ToList());
        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                if (!ReferenceEquals(ranges[i].s, ranges[j].s) && ranges[i].Start < ranges[j].End && ranges[j].Start < ranges[i].End)
                {
                    return "Há horários da agenda sobrepostos.";
                }
            }
        }

        return null;
    }

    private static string Slugify(string name)
    {
        var normalized = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') ? c : '-');
        }

        return string.Join('-', builder.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<StoreDetail?> DetailAsync(HubDb db, StoreStatusReader status, int id, CancellationToken cancellationToken)
    {
        var store = await db.Stores.Include(s => s.Schedules).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        var summary = await status.GetAsync(db, id, cancellationToken);
        if (store is null || summary is null)
        {
            return null;
        }

        var schedules = store.Schedules.OrderBy(s => s.StartTime)
            .Select(s => new ScheduleInput(ManifestGenerator.DaysFromMask(s.DaysMask), s.StartTime, s.EndTime, s.PlaylistId)).ToList();
        var state = await db.StoreStates.AsNoTracking().SingleOrDefaultAsync(s => s.StoreId == id, cancellationToken);
        var queue = StoreStatusReader.Queue(state);
        var tracks = await db.Tracks.AsNoTracking().Where(t => queue.Contains(t.Hash)).ToDictionaryAsync(t => t.Hash, StringComparer.Ordinal, cancellationToken);
        var upcoming = queue.Select(h => new StoreTrack(h, tracks.GetValueOrDefault(h)?.Title ?? "(música removida)", tracks.GetValueOrDefault(h)?.Artist,
            tracks.GetValueOrDefault(h)?.DurationMs ?? 0)).ToList();
        return new StoreDetail(summary, store.Volume, store.CrossfadeS, store.MinOfferGapS, store.Timezone, store.BtDeviceName, schedules, upcoming);
    }

    public sealed record StoreInput(string? Name, string? Slug, int? Volume, int? CrossfadeS, int? MinOfferGapS, string? Timezone, string? BtDeviceName);

    public sealed record ScheduleInput(IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End, int PlaylistId);

    public sealed record CommandInput(string Command, JsonObject? Args);

    /// <summary><see cref="Result"/>: <c>ok</c>, <c>error</c>, <c>offline</c> ou <c>pending</c> (sem resposta em 5 s).</summary>
    public sealed record CommandOutcome(int Id, string Result, string? Error);

    public sealed record EnrollCodeResult(string Code, DateTimeOffset ExpiresAt);

    public sealed record StoreTrack(string Hash, string Title, string? Artist, long DurationMs);

    /// <param name="Key">Identificador no manifesto, usado no comando <c>playOffer</c> (<c>of-12</c>).</param>
    public sealed record StoreOffer(string Key, int? OfferId, string Name, long DurationMs, DateTimeOffset ValidUntil);

    /// <param name="TrackHash">Hash do áudio; nulo numa oferta que já foi removida.</param>
    /// <param name="Result">Só nas ofertas: <c>played</c>, <c>interrupted</c> ou nulo enquanto toca.</param>
    public sealed record HistoryEntry(string? TrackHash, DateTimeOffset StartedAt, string? Title, string? Artist, HistoryKind Kind, string? Result);

    public enum HistoryKind
    {
        Music,
        Offer,
    }

    public sealed record EventEntry(string Type, JsonObject? Data, DateTimeOffset CreatedAt);

    public sealed record StoreDetail(
        StoreSummary Status,
        int Volume,
        int CrossfadeS,
        int MinOfferGapS,
        string Timezone,
        string? BtDeviceName,
        IReadOnlyList<ScheduleInput> Schedules,
        IReadOnlyList<StoreTrack> Queue);
}
