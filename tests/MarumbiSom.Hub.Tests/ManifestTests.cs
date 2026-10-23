using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Reordenar_mantem_os_ids_dos_itens_e_sobe_a_versao()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var a = await hub.SeedTrackAsync("A");
        var b = await hub.SeedTrackAsync("B");
        var c = await hub.SeedTrackAsync("C");
        var (storeId, playlistId) = await StoreWithPlaylistAsync(admin, [a, b]);
        var before = await LatestAsync(hub, storeId);
        var items = before.Playlists[ManifestGenerator.PlaylistKey(playlistId)].Items;

        var response = await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/items", new object[]
        {
            new { hash = c },
            new { id = items[1].Id, hash = b },
            new { id = items[0].Id, hash = a },
        });

        await HubHarness.JsonAsync(response);
        var after = await LatestAsync(hub, storeId);
        Assert.Equal(before.Version + 1, after.Version);
        var reordered = after.Playlists[ManifestGenerator.PlaylistKey(playlistId)].Items;
        Assert.Equal([c, b, a], reordered.Select(i => i.Hash));
        Assert.Equal(items[1].Id, reordered[1].Id);
        Assert.Equal(items[0].Id, reordered[2].Id);
        Assert.Equal(3, after.Tracks.Count);
        Assert.Equal("Playlist", after.Playlists[ManifestGenerator.PlaylistKey(playlistId)].Name);
    }

    [Fact]
    public async Task Versao_so_sobe_quando_o_conteudo_muda()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var a = await hub.SeedTrackAsync("A");
        var (storeId, _) = await StoreWithPlaylistAsync(admin, [a]);
        var version = (await LatestAsync(hub, storeId)).Version;

        await HubHarness.JsonAsync(await admin.PatchAsJsonAsync($"/api/stores/{storeId}", new { volume = 70 }));
        hub.Time.Advance(TimeSpan.FromHours(1));
        await hub.App.Services.GetRequiredService<ManifestPublisher>().RegenerateAllAsync(CancellationToken.None);

        Assert.Equal(version, (await LatestAsync(hub, storeId)).Version);
        await HubHarness.JsonAsync(await admin.PatchAsJsonAsync($"/api/stores/{storeId}", new { volume = 55, crossfadeS = 0 }));
        var changed = await LatestAsync(hub, storeId);
        Assert.Equal(version + 1, changed.Version);
        Assert.Equal(55, changed.Volume);
        Assert.Equal(0, changed.CrossfadeS);
        Assert.Equal(ManifestHasher.ComputeContentHash(changed), changed.ContentHash);
    }

    [Fact]
    public async Task Agenda_sobreposta_e_playlist_inexistente_sao_recusadas()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (storeId, playlistId) = await StoreWithPlaylistAsync(admin, [await hub.SeedTrackAsync("A")]);

        var overlap = await admin.PutAsJsonAsync($"/api/stores/{storeId}/schedules", new object[]
        {
            new { days = new[] { 1, 2 }, start = "08:00", end = "12:30", playlistId },
            new { days = new[] { 2 }, start = "12:00", end = "20:00", playlistId },
        });
        var missing = await admin.PutAsJsonAsync($"/api/stores/{storeId}/schedules", new object[] { new { days = new[] { 1 }, start = "08:00", end = "12:00", playlistId = 999 } });
        var adjacent = await admin.PutAsJsonAsync($"/api/stores/{storeId}/schedules", new object[]
        {
            new { days = new[] { 1, 2 }, start = "08:00", end = "12:00", playlistId },
            new { days = new[] { 1, 2 }, start = "12:00", end = "24:00", playlistId },
            new { days = new[] { 6 }, start = "22:00", end = "02:00", playlistId },
        });

        Assert.Equal(HttpStatusCode.BadRequest, overlap.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await HubHarness.JsonAsync(adjacent);
        var manifest = await LatestAsync(hub, storeId);
        Assert.Equal(3, manifest.Schedules.Count);
        Assert.Contains(manifest.Schedules, s => s.End == TimeOnly.MaxValue);
    }

    [Fact]
    public async Task Musica_em_uso_nao_pode_ser_removida()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var a = await hub.SeedTrackAsync("A");
        var free = await hub.SeedTrackAsync("Livre");
        await StoreWithPlaylistAsync(admin, [a]);

        var inUse = await admin.DeleteAsync($"/api/tracks/{a}");
        var removed = await admin.DeleteAsync($"/api/tracks/{free}");
        var list = await HubHarness.JsonAsync(await admin.GetAsync("/api/tracks"));

        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains("Playlist", await inUse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal([a], list.EnumerateArray().Select(t => t.GetProperty("hash").GetString()));
    }

    [Fact]
    public async Task Busca_ignora_acentos_e_maiusculas()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        await hub.SeedTrackAsync("Açaí Gelado", artist: "Banda do Pará");
        await hub.SeedTrackAsync("Outra");

        var found = await HubHarness.JsonAsync(await admin.GetAsync("/api/tracks?q=acai"));
        var byArtist = await HubHarness.JsonAsync(await admin.GetAsync("/api/tracks?q=PARA"));

        Assert.Single(found.EnumerateArray());
        Assert.Single(byArtist.EnumerateArray());
    }

    [Fact]
    public async Task Ofertas_entram_no_manifesto_conforme_validade_loja_e_tipo_de_playlist()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var music = await hub.SeedTrackAsync("Música", durationMs: 60_000);
        var voice = await hub.SeedTrackAsync("Picanha", durationMs: 10_000, kind: TrackKind.Offer);
        var (storeId, playlistId) = await StoreWithPlaylistAsync(admin, [music]);
        var other = (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/stores", new { name = "Loja 3" }))).GetProperty("status").GetProperty("id").GetInt32();
        var itemId = (await LatestAsync(hub, storeId)).Playlists[ManifestGenerator.PlaylistKey(playlistId)].Items[0].Id;
        var now = hub.Time.GetUtcNow();
        await hub.WithDbAsync(async db =>
        {
            Offer Add(string name, DateTimeOffset from, DateTimeOffset until, bool enabled = true)
            {
                var offer = new Offer { Name = name, AudioHash = voice, ValidFrom = from, ValidUntil = until, Enabled = enabled, CreatedAt = now, UpdatedAt = now };
                db.Offers.Add(offer);
                return offer;
            }

            var current = Add("Vigente", now.AddDays(-1), now.AddDays(2));
            var soon = Add("Em 5 dias", now.AddDays(5), now.AddDays(6));
            Add("Em 10 dias", now.AddDays(10), now.AddDays(11));
            Add("Vencida", now.AddDays(-3), now.AddHours(-1));
            Add("Desligada", now.AddDays(-1), now.AddDays(2), enabled: false);
            var restricted = Add("Só na outra loja", now.AddDays(-1), now.AddDays(2));
            await db.SaveChangesAsync();
            db.OfferStores.Add(new OfferStore { OfferId = restricted.Id, StoreId = other });
            db.OfferPlacements.Add(new OfferPlacement { OfferId = current.Id, Kind = PlacementKinds.AnchorDuring, PlaylistId = playlistId, PlaylistItemId = itemId, OffsetMs = 90_000 });
            db.OfferPlacements.Add(new OfferPlacement { OfferId = current.Id, Kind = PlacementKinds.EveryMinutes, EveryMinutes = 15, Mode = SpotMode.Between });
            db.OfferPlacements.Add(new OfferPlacement { OfferId = soon.Id, Kind = PlacementKinds.Clock, ClockTime = new TimeOnly(12, 0), DaysMask = 0b0111110 });
            await db.SaveChangesAsync();
        });

        await hub.App.Services.GetRequiredService<ManifestPublisher>().RegenerateAllAsync(CancellationToken.None);
        var manifest = await LatestAsync(hub, storeId);

        Assert.Equal(["Em 5 dias", "Vigente"], manifest.Spots.Offers.Values.Select(o => o.Name).Order());
        Assert.Equal(TrackKind.Offer, manifest.Tracks[voice].Kind);
        var during = Assert.Single(manifest.Spots.Placements.OfType<AnchorDuringPlacement>());
        Assert.Equal(55_000, during.OffsetMs);
        Assert.Equal(SpotMode.Between, Assert.Single(manifest.Spots.Placements.OfType<EveryMinutesPlacement>()).Mode);
        Assert.Equal([1, 2, 3, 4, 5], Assert.Single(manifest.Spots.Placements.OfType<ClockPlacement>()).Days);
        Assert.Equal(TimeSpan.FromHours(-3), manifest.Spots.Offers.Values.First().ValidFrom.Offset);

        // Ativar o aleatório remove as âncoras da playlist (seção 10.3.4).
        await HubHarness.JsonAsync(await admin.PatchAsJsonAsync($"/api/playlists/{playlistId}", new { shuffle = true }));
        var shuffled = await LatestAsync(hub, storeId);
        Assert.Empty(shuffled.Spots.Placements.OfType<AnchorDuringPlacement>());
        Assert.Equal(0, await hub.WithDbAsync(db => db.OfferPlacements.CountAsync(p => p.Kind == PlacementKinds.AnchorDuring)));
    }

    internal static async Task<(int StoreId, int PlaylistId)> StoreWithPlaylistAsync(HttpClient admin, IReadOnlyList<string> hashes)
    {
        var playlist = await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/playlists", new { name = "Playlist" }));
        var playlistId = playlist.GetProperty("id").GetInt32();
        await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/items", hashes.Select(h => new { hash = h })));
        var store = await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/stores", new { name = "Loja 2 – Centro" }));
        var storeId = store.GetProperty("status").GetProperty("id").GetInt32();
        Assert.Equal("loja-2-centro", store.GetProperty("status").GetProperty("slug").GetString());
        await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/stores/{storeId}/schedules", new object[]
        {
            new { days = new[] { 0, 1, 2, 3, 4, 5, 6 }, start = "00:00", end = "24:00", playlistId },
        }));
        return (storeId, playlistId);
    }

    internal static Task<Manifest> LatestAsync(HubHarness hub, int storeId) => hub.WithDbAsync(async db =>
    {
        var stored = await ManifestPublisher.LatestAsync(db, storeId, CancellationToken.None);
        Assert.NotNull(stored);
        return JsonSerializer.Deserialize<Manifest>(stored.Json, ContractJson.Options)!;
    });
}
