using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Tests;

/// <summary>Ofertas pelo painel (Etapa 5, D24 e D26): cadastro, regras, âncoras, execuções e o que chega à loja.</summary>
public sealed class OffersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 13, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public async Task Oferta_com_regras_e_loja_vai_para_o_manifesto_so_dessa_loja()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var music = await hub.SeedTrackAsync("Música");
        var (storeId, playlistId) = await ManifestTests.StoreWithPlaylistAsync(admin, [music]);
        var other = (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/stores", new { name = "Loja 3" }))).GetProperty("status").GetProperty("id").GetInt32();
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);

        var created = await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new
        {
            name = "  Picanha R$ 69,90 ",
            audioHash = audio,
            validFrom = Now.AddHours(-1),
            validUntil = Now.AddDays(3),
            storeIds = new[] { storeId },
            rules = new object[]
            {
                new { kind = "everyMinutes", minutes = 15 },
                new { kind = "clock", time = "12:30", days = new[] { 1, 2, 3, 4, 5 }, playlistIds = new[] { playlistId } },
            },
        }));

        var offer = created.GetProperty("offer");
        Assert.Equal("Picanha R$ 69,90", offer.GetProperty("name").GetString());
        Assert.Equal("live", offer.GetProperty("status").GetString());
        Assert.Equal(12_000, offer.GetProperty("durationMs").GetInt64());
        Assert.Equal(30, offer.GetProperty("duckLevel").GetInt32());
        Assert.Equal(2, offer.GetProperty("ruleCount").GetInt32());
        var rules = created.GetProperty("rules").EnumerateArray().ToList();
        Assert.All(rules, r => Assert.Equal("during", r.GetProperty("mode").GetString()));
        Assert.Equal("12:30", rules[1].GetProperty("time").GetString());

        var manifest = await ManifestTests.LatestAsync(hub, storeId);
        var key = offer.GetProperty("key").GetString()!;
        Assert.Equal("Picanha R$ 69,90", manifest.Spots.Offers[key].Name);
        Assert.Contains(manifest.Spots.Placements, p => p is EveryMinutesPlacement { Minutes: 15, Mode: SpotMode.During });
        Assert.Contains(manifest.Spots.Placements, p => p is ClockPlacement { Mode: SpotMode.During } c && c.PlaylistIds!.Single() == ManifestGenerator.PlaylistKey(playlistId));
        Assert.True(manifest.Tracks.ContainsKey(audio));
        Assert.Empty((await ManifestTests.LatestAsync(hub, other)).Spots.Offers);

        var listed = Assert.Single((await HubHarness.JsonAsync(await admin.GetAsync("/api/offers"))).EnumerateArray());
        Assert.Equal(new[] { storeId }, listed.GetProperty("storeIds").EnumerateArray().Select(s => s.GetInt32()));
    }

    [Fact]
    public async Task Biblioteca_lista_musicas_e_audios_de_oferta_separados()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var music = await hub.SeedTrackAsync("Música");
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);

        async Task<List<string?>> HashesAsync(string url) =>
            (await HubHarness.JsonAsync(await admin.GetAsync(url))).EnumerateArray().Select(t => t.GetProperty("hash").GetString()).ToList();

        // O painel manda o valor como no JSON ("offer"), que o enum na query recusava com 400.
        Assert.Equal([audio], await HashesAsync("/api/tracks?kind=offer"));
        Assert.Equal([music], await HashesAsync("/api/tracks"));
        Assert.Equal([music], await HashesAsync("/api/tracks?kind=music"));
    }

    [Fact]
    public async Task Oferta_invalida_e_recusada_com_motivo()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var music = await hub.SeedTrackAsync("Música");
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);

        async Task<string> RejectAsync(object body)
        {
            var response = await admin.PostAsJsonAsync("/api/offers", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
        }

        Assert.Contains("obrigatórios", await RejectAsync(new { name = "X", validFrom = Now, validUntil = Now.AddDays(1) }), StringComparison.Ordinal);
        Assert.Contains("depois do início", await RejectAsync(new { name = "X", audioHash = audio, validFrom = Now, validUntil = Now }), StringComparison.Ordinal);
        Assert.Contains("Áudio da oferta", await RejectAsync(new { name = "X", audioHash = music, validFrom = Now, validUntil = Now.AddDays(1) }), StringComparison.Ordinal);
        Assert.Contains("dia da semana", await RejectAsync(new
        {
            name = "X",
            audioHash = audio,
            validFrom = Now,
            validUntil = Now.AddDays(1),
            rules = new[] { new { kind = "clock", time = "10:00", days = Array.Empty<int>() } },
        }), StringComparison.Ordinal);
        Assert.Contains("1 a 1440", await RejectAsync(new
        {
            name = "X",
            audioHash = audio,
            validFrom = Now,
            validUntil = Now.AddDays(1),
            rules = new[] { new { kind = "everyMinutes", minutes = 0 } },
        }), StringComparison.Ordinal);
        Assert.Empty(await hub.WithDbAsync(db => db.Offers.ToListAsync()));
    }

    [Fact]
    public async Task Situacao_e_aviso_de_vencimento_sao_calculados_pelo_hub()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);

        async Task<int> CreateAsync(string name, DateTimeOffset from, DateTimeOffset until, bool enabled = true) =>
            (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new { name, audioHash = audio, validFrom = from, validUntil = until, enabled })))
                .GetProperty("offer").GetProperty("id").GetInt32();

        await CreateAsync("No ar", Now.AddDays(-1), Now.AddDays(5));
        await CreateAsync("Vence hoje", Now.AddDays(-1), Now.AddHours(6));
        await CreateAsync("Agendada", Now.AddDays(2), Now.AddDays(5));
        await CreateAsync("Vencida", Now.AddDays(-5), Now.AddDays(-1));
        await CreateAsync("Desligada", Now.AddDays(-1), Now.AddDays(5), enabled: false);

        var offers = (await HubHarness.JsonAsync(await admin.GetAsync("/api/offers"))).EnumerateArray()
            .ToDictionary(o => o.GetProperty("name").GetString()!, o => (Status: o.GetProperty("status").GetString(), Soon: o.GetProperty("expiresSoon").GetBoolean()));

        Assert.Equal(("live", false), offers["No ar"]);
        Assert.Equal(("live", true), offers["Vence hoje"]);
        Assert.Equal(("scheduled", false), offers["Agendada"]);
        Assert.Equal(("expired", false), offers["Vencida"]);
        Assert.Equal(("disabled", false), offers["Desligada"]);
    }

    [Fact]
    public async Task Ancora_durante_presa_ao_item_valida_o_tempo_e_aparece_na_playlist_e_na_oferta()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var first = await hub.SeedTrackAsync("Primeira", 100_000);
        var second = await hub.SeedTrackAsync("Segunda", 60_000);
        var (storeId, playlistId) = await ManifestTests.StoreWithPlaylistAsync(admin, [first, second]);
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);
        var offerId = (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new { name = "Picanha", audioHash = audio, validFrom = Now, validUntil = Now.AddDays(3) })))
            .GetProperty("offer").GetProperty("id").GetInt32();
        var items = (await HubHarness.JsonAsync(await admin.GetAsync($"/api/playlists/{playlistId}"))).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!).ToList();

        var tooLate = await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/anchors", new[] { new { offerId, itemId = items[1], offsetMs = 56_000 } });
        Assert.Equal(HttpStatusCode.BadRequest, tooLate.StatusCode);
        Assert.Contains("0:55", await tooLate.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var saved = await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/anchors", new[] { new { offerId, itemId = items[1], offsetMs = 20_000 } }));
        var anchor = Assert.Single(saved.GetProperty("anchors").EnumerateArray());
        Assert.Equal(items[1], anchor.GetProperty("itemId").GetString());
        Assert.Equal("Segunda", anchor.GetProperty("trackTitle").GetString());
        Assert.Equal("anchorDuring", anchor.GetProperty("kind").GetString());

        var detail = await HubHarness.JsonAsync(await admin.GetAsync($"/api/offers/{offerId}"));
        Assert.Equal(1, detail.GetProperty("offer").GetProperty("anchorCount").GetInt32());
        Assert.Equal("Playlist", Assert.Single(detail.GetProperty("anchors").EnumerateArray()).GetProperty("playlistName").GetString());

        // Reordenar mantém a âncora na mesma música (seção 10.3.4).
        await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/items", new[] { new { id = items[1], hash = second }, new { id = items[0], hash = first } }));
        var placement = Assert.IsType<AnchorDuringPlacement>(Assert.Single((await ManifestTests.LatestAsync(hub, storeId)).Spots.Placements));
        Assert.Equal((items[1], 20_000L), (placement.ItemId, placement.OffsetMs));

        // Aleatório não aceita âncora.
        await HubHarness.JsonAsync(await admin.PatchAsJsonAsync($"/api/playlists/{playlistId}", new { shuffle = true }));
        Assert.Empty(await hub.WithDbAsync(db => db.OfferPlacements.ToListAsync()));
        var shuffled = await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/anchors", new[] { new { offerId, itemId = items[1], offsetMs = 1_000 } });
        Assert.Equal(HttpStatusCode.BadRequest, shuffled.StatusCode);
    }

    [Fact]
    public async Task Execucoes_por_loja_historico_e_remocao_da_oferta()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var admin = await hub.AdminClientAsync();
        var music = await hub.SeedTrackAsync("Música");
        var (storeId, _) = await ManifestTests.StoreWithPlaylistAsync(admin, [music]);
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);
        var offerId = (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new
        {
            name = "Picanha",
            audioHash = audio,
            validFrom = Now,
            validUntil = Now.AddDays(3),
            rules = new[] { new { kind = "everyMinutes", minutes = 15 } },
        }))).GetProperty("offer").GetProperty("id").GetInt32();
        await hub.WithDbAsync(db =>
        {
            db.OfferPlays.AddRange(
                new OfferPlay { StoreId = storeId, OfferId = offerId, StartedAt = Now.AddHours(-1), Result = "played" },
                new OfferPlay { StoreId = storeId, OfferId = offerId, StartedAt = Now.AddMinutes(-40), Result = "played" },
                new OfferPlay { StoreId = storeId, OfferId = offerId, StartedAt = Now.AddMinutes(-20), Result = "interrupted" },
                new OfferPlay { StoreId = storeId, OfferId = offerId, StartedAt = Now.AddMinutes(-10), Result = "skipped", Reason = "stale" },
                new OfferPlay { StoreId = storeId, OfferId = offerId, StartedAt = Now.AddDays(-8), Result = "played" });
            return db.SaveChangesAsync();
        });

        var plays = Assert.Single((await HubHarness.JsonAsync(await admin.GetAsync($"/api/offers/{offerId}/plays"))).EnumerateArray());
        Assert.Equal((2, 1, 1), (plays.GetProperty("played").GetInt32(), plays.GetProperty("interrupted").GetInt32(), plays.GetProperty("skipped").GetInt32()));
        Assert.Equal(2, Assert.Single((await HubHarness.JsonAsync(await admin.GetAsync("/api/offers"))).EnumerateArray()).GetProperty("plays7d").GetInt32());

        var store = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}"));
        var last = store.GetProperty("status").GetProperty("lastOffer");
        Assert.Equal(("Picanha", "interrupted"), (last.GetProperty("name").GetString(), last.GetProperty("result").GetString()));

        var history = (await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/history?since={Uri.EscapeDataString(Now.AddDays(-1).ToString("O"))}")))
            .EnumerateArray().Where(h => h.GetProperty("kind").GetString() == "offer").ToList();
        Assert.Equal(3, history.Count);
        Assert.All(history, h => Assert.Equal("Picanha", h.GetProperty("title").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/offers/{offerId}")).StatusCode);
        Assert.Empty(await hub.WithDbAsync(db => db.OfferPlacements.ToListAsync()));
        Assert.NotNull((await hub.WithDbAsync(db => db.Tracks.SingleAsync(t => t.Hash == audio))).DeletedAt);
        Assert.Equal(5, await hub.WithDbAsync(db => db.OfferPlays.CountAsync()));
        Assert.Empty((await ManifestTests.LatestAsync(hub, storeId)).Spots.Offers);
    }

    [Fact]
    public async Task Loja_mostra_a_proxima_oferta_prevista_pelo_agente_e_lista_as_ofertas_para_tocar_agora()
    {
        await using var hub = await HubHarness.StartAsync(Now);
        var (hash, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        var audio = await hub.SeedTrackAsync("Picanha", 12_000, TrackKind.Offer);
        var offer = (await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new { name = "Picanha", audioHash = audio, validFrom = Now.AddHours(-1), validUntil = Now.AddDays(3) })))
            .GetProperty("offer");
        await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/offers", new { name = "Futura", audioHash = audio, validFrom = Now.AddDays(1), validUntil = Now.AddDays(3) }));
        var key = offer.GetProperty("key").GetString()!;

        var offers = (await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/offers"))).EnumerateArray().ToList();
        Assert.Equal((key, "Picanha", 12_000L), (Assert.Single(offers).GetProperty("key").GetString(), offers[0].GetProperty("name").GetString(), offers[0].GetProperty("durationMs").GetInt64()));

        using var agent = await PanelTests.ConnectAgentAsync(hub, token);
        await PanelTests.SendAsync(agent, new HelloMessage("loja-2-centro", "0.5.0", 0));
        await PanelTests.ReceiveAgentAsync(agent);
        await PanelTests.SendAsync(agent, PanelTests.Status(hash, BluetoothStatus.Connected) with { NextSpot = new NextSpot(key, Now.AddMinutes(7)) });

        var store = await PanelTests.WaitForAsync(admin, $"/api/stores/{storeId}", s => s.GetProperty("status").GetProperty("nextOffer").ValueKind == JsonValueKind.Object);
        var next = store.GetProperty("status").GetProperty("nextOffer");
        Assert.Equal(("Picanha", key), (next.GetProperty("name").GetString(), next.GetProperty("key").GetString()));
        Assert.Equal(Now.AddMinutes(7), next.GetProperty("at").GetDateTimeOffset());
    }
}
