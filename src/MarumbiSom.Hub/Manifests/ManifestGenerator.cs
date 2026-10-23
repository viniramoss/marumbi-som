using System.Globalization;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;

namespace MarumbiSom.Hub.Manifests;

/// <summary>Dados do banco necessários para montar os manifestos de todas as lojas.</summary>
public sealed record HubSnapshot(
    IReadOnlyDictionary<int, Playlist> Playlists,
    IReadOnlyDictionary<string, Data.Track> Tracks,
    IReadOnlyList<Offer> Offers,
    ILookup<int, int> OfferStores,
    ILookup<int, OfferPlacement> Placements);

/// <summary>
/// Monta o manifesto de uma loja (seção 6.4): configurações + agenda + playlists da agenda + faixas dessas playlists +
/// ofertas da loja vigentes ou que entram em vigor nos próximos 7 dias, com seus posicionamentos.
/// </summary>
public static class ManifestGenerator
{
    public static readonly TimeSpan OfferLookahead = TimeSpan.FromDays(7);

    /// <summary>Âncora durante a música começa no máximo 5 s antes do fim (seção 10.3.4).</summary>
    public static readonly TimeSpan AnchorEndMargin = TimeSpan.FromSeconds(5);

    public static string PlaylistKey(int id) => "p" + id.ToString(CultureInfo.InvariantCulture);

    public static string OfferKey(int id) => "of-" + id.ToString(CultureInfo.InvariantCulture);

    public static string PlacementKey(int id) => "pl-" + id.ToString(CultureInfo.InvariantCulture);

    public static int? ParseKey(string? key, string prefix) =>
        key is not null && key.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    public static IReadOnlyList<int> DaysFromMask(int mask) => Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0).ToList();

    public static int MaskFromDays(IEnumerable<int> days) => days.Aggregate(0, (mask, day) => mask | (1 << day));

    public static TimeZoneInfo ResolveZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>Manifesto com <c>version</c> 0 e <c>contentHash</c> calculado; o <see cref="ManifestPublisher"/> numera.</summary>
    public static Manifest Build(Store store, HubSnapshot data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);
        var zone = ResolveZone(store.Timezone);
        var schedules = store.Schedules
            .Where(s => data.Playlists.ContainsKey(s.PlaylistId))
            .OrderBy(s => s.StartTime).ThenBy(s => s.Id)
            .Select(s => new ScheduleEntry(DaysFromMask(s.DaysMask), s.StartTime, s.EndTime, PlaylistKey(s.PlaylistId)))
            .ToList();

        var playlists = new Dictionary<string, PlaylistDefinition>(StringComparer.Ordinal);
        var tracks = new Dictionary<string, TrackInfo>(StringComparer.Ordinal);
        foreach (var playlistId in store.Schedules.Select(s => s.PlaylistId).Distinct())
        {
            if (!data.Playlists.TryGetValue(playlistId, out var playlist))
            {
                continue;
            }

            var items = playlist.Items.OrderBy(i => i.Position)
                .Where(i => data.Tracks.ContainsKey(i.TrackHash))
                .Select(i => new PlaylistItemRef(i.Id, i.TrackHash))
                .ToList();
            playlists[PlaylistKey(playlistId)] = new PlaylistDefinition(playlist.Shuffle, items, playlist.Name);
            foreach (var item in items)
            {
                tracks[item.Hash] = Info(data.Tracks[item.Hash]);
            }
        }

        var offers = new Dictionary<string, OfferDefinition>(StringComparer.Ordinal);
        var placements = new List<Placement>();
        foreach (var offer in data.Offers.OrderBy(o => o.Id))
        {
            var stores = data.OfferStores[offer.Id].ToList();
            if (!offer.Enabled || offer.ValidUntil <= now || offer.ValidFrom > now + OfferLookahead
                || (stores.Count > 0 && !stores.Contains(store.Id))
                || !data.Tracks.TryGetValue(offer.AudioHash, out var audio))
            {
                continue;
            }

            var offerKey = OfferKey(offer.Id);
            offers[offerKey] = new OfferDefinition
            {
                Hash = offer.AudioHash,
                Name = offer.Name,
                DuckLevel = offer.DuckLevel,
                ValidFrom = TimeZoneInfo.ConvertTime(offer.ValidFrom, zone),
                ValidUntil = TimeZoneInfo.ConvertTime(offer.ValidUntil, zone),
            };
            tracks[offer.AudioHash] = Info(audio);
            foreach (var placement in data.Placements[offer.Id].OrderBy(p => p.Id))
            {
                if (ToPlacement(placement, offerKey, playlists, tracks) is { } mapped)
                {
                    placements.Add(mapped);
                }
            }
        }

        var manifest = new Manifest
        {
            StoreId = store.Slug,
            Version = 0,
            GeneratedAt = TimeZoneInfo.ConvertTime(now, zone),
            Timezone = store.Timezone,
            Volume = store.Volume,
            CrossfadeS = store.CrossfadeS,
            Schedules = schedules,
            Playlists = playlists,
            Tracks = tracks,
            Spots = new SpotsSection { MinOfferGapS = store.MinOfferGapS, Offers = offers, Placements = placements },
        };
        return ManifestHasher.WithContentHash(manifest);
    }

    private static Placement? ToPlacement(
        OfferPlacement placement, string offerKey, Dictionary<string, PlaylistDefinition> playlists, Dictionary<string, TrackInfo> tracks)
    {
        var id = PlacementKey(placement.Id);
        switch (placement.Kind)
        {
            case PlacementKinds.AnchorBetween or PlacementKinds.AnchorDuring:
                // Âncoras só em playlist sequencial que está no manifesto e com o item ainda presente (seção 10.3.4).
                if (placement.PlaylistId is not { } playlistId || placement.PlaylistItemId is not { } itemId
                    || !playlists.TryGetValue(PlaylistKey(playlistId), out var playlist) || playlist.Shuffle
                    || playlist.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
                {
                    return null;
                }

                if (placement.Kind == PlacementKinds.AnchorBetween)
                {
                    return new AnchorBetweenPlacement { Id = id, OfferId = offerKey, PlaylistId = PlaylistKey(playlistId), AfterItemId = itemId };
                }

                var maxOffset = Math.Max(0, tracks[item.Hash].DurationMs - (long)AnchorEndMargin.TotalMilliseconds);
                return new AnchorDuringPlacement
                {
                    Id = id,
                    OfferId = offerKey,
                    PlaylistId = PlaylistKey(playlistId),
                    ItemId = itemId,
                    OffsetMs = Math.Clamp(placement.OffsetMs ?? 0, 0, maxOffset),
                };
            case PlacementKinds.EveryTracks when placement.EveryN is > 0:
                return new EveryTracksPlacement { Id = id, OfferId = offerKey, N = placement.EveryN.Value, PlaylistIds = PlaylistIds(placement) };
            case PlacementKinds.EveryMinutes when placement.EveryMinutes is > 0:
                return new EveryMinutesPlacement
                {
                    Id = id,
                    OfferId = offerKey,
                    Minutes = placement.EveryMinutes.Value,
                    Mode = placement.Mode ?? SpotMode.During,
                    PlaylistIds = PlaylistIds(placement),
                };
            case PlacementKinds.Clock when placement.ClockTime is { } clock:
                return new ClockPlacement
                {
                    Id = id,
                    OfferId = offerKey,
                    Time = clock,
                    Days = DaysFromMask(placement.DaysMask ?? 0b111_1111),
                    Mode = placement.Mode ?? SpotMode.During,
                    PlaylistIds = PlaylistIds(placement),
                };
            default:
                return null;
        }
    }

    private static List<string>? PlaylistIds(OfferPlacement placement)
    {
        if (string.IsNullOrEmpty(placement.PlaylistIdsJson))
        {
            return null;
        }

        var ids = JsonSerializer.Deserialize<int[]>(placement.PlaylistIdsJson);
        return ids is { Length: > 0 } ? ids.Order().Select(PlaylistKey).ToList() : null;
    }

    private static TrackInfo Info(Data.Track track) => new()
    {
        Kind = track.Kind,
        Size = track.SizeBytes,
        DurationMs = track.DurationMs,
        Title = track.Title,
        Artist = track.Artist,
    };
}
