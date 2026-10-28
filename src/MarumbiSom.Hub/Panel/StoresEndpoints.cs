using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        api.MapGet("/stores", ListAsync);
        api.MapPost("/stores", CreateAsync);
        api.MapGet("/stores/{id:int}", GetAsync);
        api.MapPatch("/stores/{id:int}", UpdateAsync);
        api.MapPut("/stores/{id:int}/schedules", ReplaceSchedulesAsync);
        api.MapPost("/stores/{id:int}/enroll-code", EnrollCodeAsync);
        api.MapDelete("/stores/{id:int}/token", RevokeTokenAsync);
        api.MapPost("/stores/{id:int}/commands", CommandAsync);
        api.MapGet("/stores/{id:int}/history", HistoryAsync);
        api.MapGet("/stores/{id:int}/events", EventsAsync);
    }

    private static async Task<IResult> ListAsync(HubDb db, AgentConnections agents, CancellationToken cancellationToken)
    {
        var stores = await db.Stores.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken);
        var result = new List<StoreSummary>();
        foreach (var store in stores)
        {
            result.Add(await SummaryAsync(db, agents, store, cancellationToken));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        StoreInput input, HubDb db, ManifestPublisher publisher, AgentConnections agents, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80)
        {
            return Results.BadRequest(new { error = "Nome obrigatório, até 80 caracteres." });
        }

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slugify(input.Name) : input.Slug.Trim();
        if (slug.Length is < 2 or > 40 || !slug.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return Results.BadRequest(new { error = "Identificador inválido: use letras minúsculas sem acento, números e hífen (2 a 40)." });
        }

        if (await db.Stores.AnyAsync(s => s.Slug == slug, cancellationToken))
        {
            return Results.Conflict(new { error = $"Já existe uma loja com o identificador {slug}." });
        }

        var store = new Store { Slug = slug, Name = input.Name.Trim(), CreatedAt = time.GetUtcNow() };
        if (Apply(store, input) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        db.Stores.Add(store);
        await db.SaveChangesAsync(cancellationToken);
        db.StoreStates.Add(new StoreState { StoreId = store.Id });
        db.Audit(user, time, "stores.create", slug, input);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        return Results.Created($"/api/stores/{store.Id}", await DetailAsync(db, agents, store.Id, cancellationToken));
    }

    private static async Task<IResult> GetAsync(int id, HubDb db, AgentConnections agents, CancellationToken cancellationToken) =>
        await DetailAsync(db, agents, id, cancellationToken) is { } detail ? Results.Ok(detail) : Results.NotFound();

    private static async Task<IResult> UpdateAsync(
        int id, StoreInput input, HubDb db, ManifestPublisher publisher, AgentConnections agents, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
                return Results.BadRequest(new { error = "Nome obrigatório, até 80 caracteres." });
            }

            store.Name = input.Name.Trim();
        }

        if (Apply(store, input) is { } error)
        {
            return Results.BadRequest(new { error });
        }

        db.Audit(user, time, "stores.update", store.Slug, input);
        await db.SaveChangesAsync(cancellationToken);
        await publisher.RegenerateAllAsync(cancellationToken);
        return Results.Ok(await DetailAsync(db, agents, id, cancellationToken));
    }

    private static async Task<IResult> ReplaceSchedulesAsync(
        int id, List<ScheduleInput> schedules, HubDb db, ManifestPublisher publisher, AgentConnections agents, ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
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
                return Results.BadRequest(new { error = $"Playlist {schedule.PlaylistId} não existe." });
            }

            if (schedule.Days is not { Count: > 0 } || schedule.Days.Any(d => d is < 0 or > 6))
            {
                return Results.BadRequest(new { error = "Informe os dias da semana (0 = domingo … 6 = sábado)." });
            }
        }

        if (Overlap(schedules) is { } overlap)
        {
            return Results.BadRequest(new { error = overlap });
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
        return Results.Ok(await DetailAsync(db, agents, id, cancellationToken));
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
        return Results.Ok(new { code, expiresAt = now + AgentEndpoints.EnrollCodeLifetime });
    }

    private static async Task<IResult> RevokeTokenAsync(int id, HubDb db, AgentConnections agents, ClaimsPrincipal user, TimeProvider time, CancellationToken cancellationToken)
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
            return Results.BadRequest(new { error = $"Comando desconhecido: {input.Command}." });
        }

        var now = time.GetUtcNow();
        var command = new StoreCommand { StoreId = id, Type = input.Command, ArgsJson = input.Args?.ToJsonString(), CreatedAt = now };
        db.Commands.Add(command);
        db.Audit(user, time, "stores.command", store.Slug, new { input.Command, args = input.Args?.ToJsonString() });
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
        var result = await agents.SendCommandAsync(id, new CommandMessage(commandId, input.Command, input.Args), CommandWait, cancellationToken);
        return result is null
            ? Results.Ok(new CommandOutcome(command.Id, "pending", "A loja não respondeu a tempo; o resultado aparece no histórico."))
            : Results.Ok(new CommandOutcome(command.Id, result.Ok ? "ok" : "error", result.Error));
    }

    private static async Task<IResult> HistoryAsync(int id, HubDb db, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 100, 1, 1000);
        var history = await db.PlayHistory.AsNoTracking().Where(h => h.StoreId == id).OrderByDescending(h => h.StartedAt).Take(take)
            .GroupJoin(db.Tracks, h => h.TrackHash, t => t.Hash, (h, tracks) => new { h, tracks })
            .SelectMany(x => x.tracks.DefaultIfEmpty(), (x, t) => new { x.h.TrackHash, x.h.StartedAt, Title = t != null ? t.Title : null, Artist = t != null ? t.Artist : null })
            .ToListAsync(cancellationToken);
        return Results.Ok(history.OrderByDescending(h => h.StartedAt));
    }

    private static async Task<IResult> EventsAsync(int id, HubDb db, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 100, 1, 1000);
        var events = await db.Events.AsNoTracking().Where(e => e.StoreId == id).OrderByDescending(e => e.CreatedAt).Take(take)
            .Select(e => new { e.Type, e.DataJson, e.CreatedAt }).ToListAsync(cancellationToken);
        return Results.Ok(events.Select(e => new { e.Type, data = e.DataJson is null ? null : JsonNode.Parse(e.DataJson), e.CreatedAt }));
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

    private static async Task<StoreSummary> SummaryAsync(HubDb db, AgentConnections agents, Store store, CancellationToken cancellationToken)
    {
        var state = await db.StoreStates.AsNoTracking().SingleOrDefaultAsync(s => s.StoreId == store.Id, cancellationToken);
        var latest = await db.StoreManifests.Where(m => m.StoreId == store.Id).MaxAsync(m => (long?)m.Version, cancellationToken);
        return new StoreSummary(
            store.Id, store.Slug, store.Name, agents.IsOnline(store.Id), store.TokenHash is not null, latest, state?.AppliedVersion, state?.PendingVersion,
            state?.SyncDone, state?.SyncTotal, state?.PlayerState, state?.NowPlayingHash, state?.BtState, state?.Volume, state?.DiskFreeMb, state?.AgentVersion,
            state?.LastSeenAt);
    }

    private static async Task<StoreDetail?> DetailAsync(HubDb db, AgentConnections agents, int id, CancellationToken cancellationToken)
    {
        var store = await db.Stores.Include(s => s.Schedules).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (store is null)
        {
            return null;
        }

        var schedules = store.Schedules.OrderBy(s => s.StartTime)
            .Select(s => new ScheduleInput(ManifestGenerator.DaysFromMask(s.DaysMask), s.StartTime, s.EndTime, s.PlaylistId)).ToList();
        return new StoreDetail(
            await SummaryAsync(db, agents, store, cancellationToken), store.Volume, store.CrossfadeS, store.MinOfferGapS, store.Timezone, store.BtDeviceName, schedules);
    }

    public sealed record StoreInput(string? Name, string? Slug, int? Volume, int? CrossfadeS, int? MinOfferGapS, string? Timezone, string? BtDeviceName);

    public sealed record ScheduleInput(IReadOnlyList<int> Days, TimeOnly Start, TimeOnly End, int PlaylistId);

    public sealed record CommandInput(string Command, JsonObject? Args);

    public sealed record CommandOutcome(int Id, string Result, string? Error);

    public sealed record StoreSummary(
        int Id, string Slug, string Name, bool Online, bool Enrolled, long? LatestVersion, long? AppliedVersion, long? PendingVersion, int? SyncDone, int? SyncTotal,
        string? Player, string? NowPlaying, string? Bluetooth, int? Volume, long? DiskFreeMb, string? AgentVersion, DateTimeOffset? LastSeenAt);

    public sealed record StoreDetail(
        StoreSummary Status, int Volume, int CrossfadeS, int MinOfferGapS, string Timezone, string? BtDeviceName, IReadOnlyList<ScheduleInput> Schedules);
}
