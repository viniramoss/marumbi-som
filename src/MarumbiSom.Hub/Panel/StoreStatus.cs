using System.Text.Json;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Resumo de cada loja para o dashboard: estado ao vivo, sincronização e os alertas da seção 6.6, que pela D21 aparecem
/// no painel em vez de irem para o Telegram.
/// </summary>
public sealed class StoreStatusReader(AgentConnections agents, TimeProvider time)
{
    public static readonly TimeSpan NoSignalAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BluetoothAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SyncStalledAfter = TimeSpan.FromMinutes(60);
    public const long LowDiskMb = 1024;
    public const int MaxRestartsPerHour = 3;

    public async Task<IReadOnlyList<StoreSummary>> ListAsync(HubDb db, CancellationToken cancellationToken) =>
        await LoadAsync(db, null, cancellationToken);

    public async Task<StoreSummary?> GetAsync(HubDb db, int storeId, CancellationToken cancellationToken) =>
        (await LoadAsync(db, storeId, cancellationToken)).SingleOrDefault();

    private async Task<IReadOnlyList<StoreSummary>> LoadAsync(HubDb db, int? storeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var now = time.GetUtcNow();
        var storesQuery = db.Stores.Include(s => s.Schedules).AsNoTracking();
        var statesQuery = db.StoreStates.AsNoTracking();
        var manifestsQuery = db.StoreManifests.AsNoTracking();
        var restartsQuery = db.Events.AsNoTracking().Where(e => e.Type == AgentConnections.AgentRestartedEvent && e.CreatedAt > now.AddHours(-1));
        var offerPlaysQuery = db.OfferPlays.AsNoTracking().Where(p => p.StartedAt > now.AddDays(-7) && (p.Result == null || p.Result != "skipped"));
        if (storeId is { } id)
        {
            storesQuery = storesQuery.Where(s => s.Id == id);
            statesQuery = statesQuery.Where(s => s.StoreId == id);
            manifestsQuery = manifestsQuery.Where(m => m.StoreId == id);
            restartsQuery = restartsQuery.Where(e => e.StoreId == id);
            offerPlaysQuery = offerPlaysQuery.Where(p => p.StoreId == id);
        }

        var stores = await storesQuery.ToListAsync(cancellationToken);
        var states = await statesQuery.ToDictionaryAsync(s => s.StoreId, cancellationToken);
        var latest = await manifestsQuery.GroupBy(m => m.StoreId).Select(g => new { StoreId = g.Key, Version = g.Max(m => m.Version) })
            .ToDictionaryAsync(x => x.StoreId, x => x.Version, cancellationToken);
        var restarts = await restartsQuery.GroupBy(e => e.StoreId).Select(g => new { StoreId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StoreId, x => x.Count, cancellationToken);
        var hashes = states.Values.Select(s => s.NowPlayingHash).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var tracks = await db.Tracks.AsNoTracking().Where(t => hashes.Contains(t.Hash))
            .ToDictionaryAsync(t => t.Hash, StringComparer.Ordinal, cancellationToken);
        var playlistIds = states.Values.Select(s => ManifestGenerator.ParseKey(s.PlaylistKey, "p")).OfType<int>().Distinct().ToList();
        var playlists = await db.Playlists.AsNoTracking().Where(p => playlistIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var lastOffers = (await offerPlaysQuery.ToListAsync(cancellationToken))
            .GroupBy(p => p.StoreId).ToDictionary(g => g.Key, g => g.MaxBy(p => p.StartedAt)!);
        var offerIds = lastOffers.Values.Select(p => p.OfferId)
            .Concat(states.Values.Select(s => ManifestGenerator.ParseKey(s.NextOfferKey, "of-"))).OfType<int>().Distinct().ToList();
        var offerNames = await db.Offers.AsNoTracking().Where(o => offerIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Name, cancellationToken);

        return stores.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).Select(store =>
        {
            var state = states.GetValueOrDefault(store.Id);
            var nowPlaying = state?.NowPlayingHash is { } hash
                ? new NowPlayingInfo(
                    hash, tracks.GetValueOrDefault(hash)?.Title, tracks.GetValueOrDefault(hash)?.Artist, tracks.GetValueOrDefault(hash)?.DurationMs,
                    state.PositionMs, state.PositionAt)
                : null;
            var playlistId = ManifestGenerator.ParseKey(state?.PlaylistKey, "p");
            var open = OpeningHours.IsOpen(store.Schedules, ManifestGenerator.ResolveZone(store.Timezone), now);
            var online = agents.IsOnline(store.Id);
            var nextOfferId = ManifestGenerator.ParseKey(state?.NextOfferKey, "of-");
            var nextOffer = online && state?.NextOfferKey is { } nextKey && state.NextOfferAt is { } nextAt
                ? new NextOfferInfo(nextKey, nextOfferId, OfferName(offerNames, nextOfferId), nextAt)
                : null;
            var lastOffer = lastOffers.GetValueOrDefault(store.Id) is { } last
                ? new LastOfferInfo(last.OfferId, OfferName(offerNames, last.OfferId), last.StartedAt, last.Result)
                : null;
            return new StoreSummary(
                store.Id, store.Slug, store.Name, online, store.TokenHash is not null, latest.TryGetValue(store.Id, out var v) ? v : null,
                state?.AppliedVersion, state?.PendingVersion, state?.SyncDone, state?.SyncTotal, state?.PlayerState, nowPlaying, state?.BtState, state?.Volume,
                state?.DiskFreeMb, state?.AgentVersion, state?.LastSeenAt, state?.PlaylistKey,
                playlistId is { } p && playlists.TryGetValue(p, out var name) ? name : null, open, store.Schedules.Count > 0, store.MinOfferGapS, nextOffer, lastOffer,
                Update(state, now), Alerts(store, state, online, open, restarts.GetValueOrDefault(store.Id), now));
        }).ToList();
    }

    public static readonly TimeSpan UpdateResultFor = TimeSpan.FromHours(24);

    public static UpdateInfo? Update(StoreState? state, DateTimeOffset now)
    {
        if (state is not { UpdateVersion: { } version, UpdateStage: { } stage, UpdateAt: { } at })
        {
            return null;
        }

        return UpdateStages.InProgress(stage) || now - at < UpdateResultFor
            ? new UpdateInfo(version, stage, state.UpdateDoneBytes, state.UpdateTotalBytes, state.UpdateError, at)
            : null;
    }

    private static string OfferName(Dictionary<int, string> names, int? offerId) =>
        offerId is { } id && names.TryGetValue(id, out var name) ? name : "Oferta removida";

    /// <summary>Condições da seção 6.6, só dentro do horário de funcionamento (alguma janela da agenda em vigor).</summary>
    public static IReadOnlyList<StoreAlert> Alerts(Store store, StoreState? state, bool online, bool open, int restartsLastHour, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        var alerts = new List<StoreAlert>();
        if (!open || store.TokenHash is null)
        {
            return alerts;
        }

        if (!online && (state?.LastSeenAt is null || now - state.LastSeenAt > NoSignalAfter))
        {
            alerts.Add(new StoreAlert(
                "noSignal", state?.LastSeenAt is null ? "A loja ainda não se conectou ao Hub." : "Sem sinal da loja.", state?.LastSeenAt));
            return alerts;
        }

        if (state is null)
        {
            return alerts;
        }

        if (state.BtState is { } bt && bt != "connected" && state.BtChangedAt is { } btSince && now - btSince > BluetoothAfter)
        {
            alerts.Add(new StoreAlert("bluetooth", bt == "radioOff" ? "Bluetooth desligado no PC da loja." : "Caixa de som desconectada.", btSince));
        }

        if (state.PendingVersion is not null && state.SyncChangedAt is { } syncSince && now - syncSince > SyncStalledAfter)
        {
            var progress = state.SyncTotal is { } total ? $" ({state.SyncDone ?? 0} de {total} arquivos)" : string.Empty;
            alerts.Add(new StoreAlert("syncStalled", $"Sincronização da versão {state.PendingVersion} parada{progress}.", syncSince));
        }

        if (state.DiskFreeMb is { } free && free < LowDiskMb)
        {
            alerts.Add(new StoreAlert("lowDisk", $"Pouco espaço no disco da loja: {free} MB livres.", null));
        }

        if (restartsLastHour > MaxRestartsPerHour)
        {
            alerts.Add(new StoreAlert("restarts", $"O agente reiniciou {restartsLastHour} vezes na última hora.", null));
        }

        return alerts;
    }

    public static IReadOnlyList<string> Queue(StoreState? state) =>
        state?.QueueJson is { } json ? JsonSerializer.Deserialize<List<string>>(json) ?? [] : [];
}

/// <summary>Horário de funcionamento a partir da agenda da loja (mesmas regras do agente, D19 b).</summary>
public static class OpeningHours
{
    private const int MinutesPerDay = 1440;
    private const int MinutesPerWeek = 7 * MinutesPerDay;

    public static bool IsOpen(IEnumerable<Schedule> schedules, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var minute = ((int)local.DayOfWeek * MinutesPerDay) + (local.Hour * 60) + local.Minute;
        foreach (var schedule in schedules)
        {
            var start = (int)schedule.StartTime.ToTimeSpan().TotalMinutes;
            var end = schedule.EndTime == TimeOnly.MaxValue ? MinutesPerDay : (int)schedule.EndTime.ToTimeSpan().TotalMinutes;
            for (var day = 0; day < 7; day++)
            {
                if ((schedule.DaysMask & (1 << day)) == 0)
                {
                    continue;
                }

                var from = (day * MinutesPerDay) + start;
                var to = (day * MinutesPerDay) + (end > start ? end : MinutesPerDay + end);
                // A janela de sábado que passa da meia-noite continua no domingo (início da semana).
                if ((minute >= from && minute < to) || (minute + MinutesPerWeek >= from && minute + MinutesPerWeek < to))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

public sealed record NowPlayingInfo(string Hash, string? Title, string? Artist, long? DurationMs, long? PositionMs, DateTimeOffset? PositionAt);

public sealed record StoreAlert(string Code, string Message, DateTimeOffset? Since);

public sealed record StoreSummary(
    int Id,
    string Slug,
    string Name,
    bool Online,
    bool Enrolled,
    long? LatestVersion,
    long? AppliedVersion,
    long? PendingVersion,
    int? SyncDone,
    int? SyncTotal,
    string? Player,
    NowPlayingInfo? NowPlaying,
    string? Bluetooth,
    int? Volume,
    long? DiskFreeMb,
    string? AgentVersion,
    DateTimeOffset? LastSeenAt,
    string? PlaylistId,
    string? PlaylistName,
    bool Open,
    bool HasSchedule,
    int MinOfferGapS,
    NextOfferInfo? NextOffer,
    LastOfferInfo? LastOffer,
    UpdateInfo? Update,
    IReadOnlyList<StoreAlert> Alerts);

/// <summary>Próxima oferta prevista pelo agente (D26 d); só com a loja conectada.</summary>
public sealed record NextOfferInfo(string Key, int? OfferId, string Name, DateTimeOffset At);

/// <summary>Última oferta que começou a tocar na loja nos últimos 7 dias; <c>Result</c> nulo = tocando agora.</summary>
public sealed record LastOfferInfo(int? OfferId, string Name, DateTimeOffset At, string? Result);

/// <summary>
/// Atualização do agente (D27 h): em andamento sempre; o resultado (<c>done</c>, <c>failed</c>, <c>rolledBack</c>) por 24 horas.
/// </summary>
public sealed record UpdateInfo(string Version, string Stage, long? DoneBytes, long? TotalBytes, string? Error, DateTimeOffset At);
