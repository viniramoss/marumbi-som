using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using MarumbiSom.Hub.Panel;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarumbiSom.Hub.Tests;

/// <summary>Dados e tempo real do painel (Etapa 4, D23).</summary>
public sealed class PanelTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Painel_ao_vivo_recebe_o_resumo_da_loja_e_os_topicos_alterados()
    {
        await using var hub = await HubHarness.StartAsync();
        var (hash, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        using var panel = await ConnectPanelAsync(hub, await hub.AdminCookieAsync());
        using var agent = await ConnectAgentAsync(hub, token);

        await SendAsync(agent, new HelloMessage("loja-2-centro", "0.4.0", 0));
        var online = await ReceiveStoreAsync(panel, s => s.Online);
        Assert.Equal(storeId, online.Id);

        await SendAsync(agent, Status(hash, BluetoothStatus.Connected) with { PlaylistId = "p1", Queue = [hash] });
        await SendAsync(agent, new TrackStartedMessage(hash, hub.Time.GetUtcNow()));
        var playing = await ReceiveStoreAsync(panel, s => s.NowPlaying?.Title == "A" && s.NowPlaying.PositionMs == 0);
        Assert.Equal("playing", playing.Player);
        Assert.Equal("Playlist", playing.PlaylistName);
        Assert.Equal(200_000, playing.NowPlaying!.DurationMs);
        Assert.Equal(hub.Time.GetUtcNow(), playing.NowPlaying.PositionAt);
        Assert.Contains($"history:{storeId}", (await ReceiveChangedAsync(panel, t => t.Contains($"history:{storeId}"))).Topics);

        await SendAsync(agent, new AgentEventMessage(AgentEvents.BluetoothLost));
        await ReceiveChangedAsync(panel, t => t.Contains($"events:{storeId}"));

        await HubHarness.JsonAsync(await admin.PatchAsJsonAsync($"/api/tracks/{hash}", new { title = "A (ao vivo)" }));
        await ReceiveChangedAsync(panel, t => t.Contains("tracks") && t.Contains("playlists"));

        await agent.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", None);
        await ReceiveStoreAsync(panel, s => !s.Online);
    }

    [Fact]
    public async Task WebSocket_do_painel_exige_login_e_a_mesma_origem()
    {
        await using var hub = await HubHarness.StartAsync();
        var cookie = await hub.AdminCookieAsync();

        var anonymous = await Assert.ThrowsAnyAsync<Exception>(() => ConnectPanelAsync(hub, cookie: null));
        var foreign = await Assert.ThrowsAnyAsync<Exception>(() => ConnectPanelAsync(hub, cookie, origin: "https://outro-site.example"));
        using var proxied = await ConnectPanelAsync(hub, cookie, origin: "https://hub.exemplo.ts.net", forwardedHost: "hub.exemplo.ts.net");

        Assert.Contains("401", anonymous.Message, StringComparison.Ordinal);
        Assert.Contains("403", foreign.Message, StringComparison.Ordinal);
        Assert.Equal(WebSocketState.Open, proxied.State);
    }

    [Fact]
    public async Task Detalhe_da_loja_mostra_fila_e_alerta_de_reinicios_do_agente()
    {
        await using var hub = await HubHarness.StartAsync();
        var (hash, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        using var agent = await ConnectAgentAsync(hub, token);
        await SendAsync(agent, new HelloMessage("loja-2-centro", "0.4.0", 0));
        await ReceiveAgentAsync(agent);

        for (var restart = 0; restart <= StoreStatusReader.MaxRestartsPerHour; restart++)
        {
            await SendAsync(agent, Status(hash, BluetoothStatus.Connected) with { UptimeS = 600, Queue = [hash, hash] });
            await SendAsync(agent, Status(hash, BluetoothStatus.Connected) with { UptimeS = 5, Queue = [hash, hash] });
        }

        var detail = await WaitForAsync(admin, $"/api/stores/{storeId}", d => d.GetProperty("status").GetProperty("alerts").GetArrayLength() > 0);
        Assert.Equal(["A", "A"], detail.GetProperty("queue").EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        Assert.True(detail.GetProperty("status").GetProperty("hasSchedule").GetBoolean());
        var alert = Assert.Single(detail.GetProperty("status").GetProperty("alerts").EnumerateArray());
        Assert.Equal("restarts", alert.GetProperty("code").GetString());
        var restarts = await hub.WithDbAsync(db => db.Events.CountAsync(e => e.Type == AgentConnections.AgentRestartedEvent));
        Assert.Equal(StoreStatusReader.MaxRestartsPerHour + 1, restarts);
    }

    [Fact]
    public void Alertas_da_secao_6_6_respeitam_os_tempos_e_o_horario_de_funcionamento()
    {
        var now = new DateTimeOffset(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);
        var store = new Store { Slug = "loja", Name = "Loja", TokenHash = "x" };
        StoreState State(Action<StoreState> change)
        {
            var state = new StoreState
            {
                LastSeenAt = now.AddSeconds(-10),
                BtState = "connected",
                BtChangedAt = now.AddHours(-2),
                DiskFreeMb = 50_000,
            };
            change(state);
            return state;
        }

        static string[] Codes(IReadOnlyList<StoreAlert> alerts) => alerts.Select(a => a.Code).ToArray();

        Assert.Empty(StoreStatusReader.Alerts(store, State(_ => { }), online: true, open: true, 0, now));
        Assert.Equal(["noSignal"], Codes(StoreStatusReader.Alerts(store, State(s => s.LastSeenAt = now.AddMinutes(-11)), online: false, open: true, 0, now)));
        Assert.Empty(StoreStatusReader.Alerts(store, State(s => s.LastSeenAt = now.AddMinutes(-9)), online: false, open: true, 0, now));
        Assert.Empty(StoreStatusReader.Alerts(store, State(s => s.LastSeenAt = now.AddMinutes(-11)), online: false, open: false, 0, now));
        Assert.Empty(StoreStatusReader.Alerts(store, State(s => (s.BtState, s.BtChangedAt) = ("disconnected", now.AddMinutes(-4))), true, true, 0, now));
        Assert.Equal(
            ["bluetooth"],
            Codes(StoreStatusReader.Alerts(store, State(s => (s.BtState, s.BtChangedAt) = ("radioOff", now.AddMinutes(-6))), true, true, 0, now)));
        Assert.Equal(
            ["syncStalled"],
            Codes(StoreStatusReader.Alerts(store, State(s => (s.PendingVersion, s.SyncChangedAt) = (9, now.AddMinutes(-61))), true, true, 0, now)));
        Assert.Equal(["lowDisk"], Codes(StoreStatusReader.Alerts(store, State(s => s.DiskFreeMb = 900), true, true, 0, now)));
        Assert.Equal(["restarts"], Codes(StoreStatusReader.Alerts(store, State(_ => { }), true, true, 4, now)));
        Assert.Empty(StoreStatusReader.Alerts(store, State(_ => { }), true, true, 3, now));
        // Loja ainda não vinculada: sem alertas.
        Assert.Empty(StoreStatusReader.Alerts(new Store { Slug = "nova", Name = "Nova" }, null, false, true, 0, now));
    }

    [Theory]
    [InlineData("2026-10-24T23:30:00-03:00", true)] // sábado 23:30, janela 22:00–02:00 de sábado
    [InlineData("2026-10-25T01:59:00-03:00", true)] // domingo 01:59, ainda na janela de sábado
    [InlineData("2026-10-25T02:00:00-03:00", false)]
    [InlineData("2026-10-23T23:30:00-03:00", false)] // sexta: sem janela noturna
    [InlineData("2026-10-23T08:00:00-03:00", true)] // sexta 08:00, janela 08:00–18:00
    [InlineData("2026-10-23T18:00:00-03:00", false)]
    public void Horario_de_funcionamento_segue_a_agenda_inclusive_depois_da_meia_noite(string at, bool open)
    {
        var schedules = new[]
        {
            new Schedule { DaysMask = ManifestGenerator.MaskFromDays([1, 2, 3, 4, 5]), StartTime = new TimeOnly(8, 0), EndTime = new TimeOnly(18, 0) },
            new Schedule { DaysMask = ManifestGenerator.MaskFromDays([6]), StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(2, 0) },
        };

        Assert.Equal(open, OpeningHours.IsOpen(schedules, ManifestGenerator.ResolveZone("America/Sao_Paulo"), DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task Trocar_senha_exige_a_senha_atual_e_o_tamanho_minimo()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();

        var wrong = await admin.PostAsJsonAsync("/api/auth/password", new { currentPassword = "errada-errada", newPassword = "nova-senha-forte" });
        var shortPassword = await admin.PostAsJsonAsync("/api/auth/password", new { currentPassword = HubHarness.AdminPassword, newPassword = "curta" });
        var ok = await admin.PostAsJsonAsync("/api/auth/password", new { currentPassword = HubHarness.AdminPassword, newPassword = "nova-senha-forte" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("senha atual", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.False(string.IsNullOrEmpty(await hub.AdminCookieAsync(password: "nova-senha-forte")));
    }

    [Fact]
    public async Task Configuracoes_mostram_o_backup_mais_recente()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var before = await HubHarness.JsonAsync(await admin.GetAsync("/api/settings"));
        await hub.App.Services.GetRequiredService<DailyMaintenance>().BackupAsync(None);

        var after = await HubHarness.JsonAsync(await admin.GetAsync("/api/settings"));

        Assert.Equal(0, before.GetProperty("backup").GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("backup").GetProperty("lastAt").ValueKind);
        Assert.Equal(1, after.GetProperty("backup").GetProperty("count").GetInt32());
        Assert.StartsWith("marumbi-", after.GetProperty("backup").GetProperty("lastFile").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(after.GetProperty("hubVersion").GetString()));
    }

    [Fact]
    public async Task Biblioteca_e_playlists_mostram_onde_cada_coisa_e_usada()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var used = await hub.SeedTrackAsync("Usada");
        var loose = await hub.SeedTrackAsync("Solta");
        var (storeId, playlistId) = await ManifestTests.StoreWithPlaylistAsync(admin, [used]);

        var tracks = await HubHarness.JsonAsync(await admin.GetAsync("/api/tracks"));
        var playlists = await HubHarness.JsonAsync(await admin.GetAsync("/api/playlists"));
        var detail = await HubHarness.JsonAsync(await admin.GetAsync($"/api/playlists/{playlistId}"));

        var usedTrack = tracks.EnumerateArray().Single(t => t.GetProperty("hash").GetString() == used);
        Assert.Equal("Playlist", Assert.Single(usedTrack.GetProperty("playlists").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(tracks.EnumerateArray().Single(t => t.GetProperty("hash").GetString() == loose).GetProperty("playlists").EnumerateArray());
        var usage = Assert.Single(Assert.Single(playlists.EnumerateArray()).GetProperty("usage").EnumerateArray());
        Assert.Equal(storeId, usage.GetProperty("storeId").GetInt32());
        Assert.Equal("00:00", usage.GetProperty("start").GetString());
        Assert.Equal("24:00", usage.GetProperty("end").GetString());
        Assert.Equal("Loja 2 – Centro", Assert.Single(detail.GetProperty("usage").EnumerateArray()).GetProperty("storeName").GetString());
    }

    [Fact]
    public async Task Tocar_agora_lista_as_musicas_da_loja_e_o_historico_filtra_por_data()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var a = await hub.SeedTrackAsync("A", artist: "Zeca");
        var b = await hub.SeedTrackAsync("B", artist: "Ana");
        await hub.SeedTrackAsync("Fora");
        var (storeId, _) = await ManifestTests.StoreWithPlaylistAsync(admin, [a, b]);
        var now = hub.Time.GetUtcNow();
        await hub.WithDbAsync(db =>
        {
            db.PlayHistory.Add(new PlayHistory { StoreId = storeId, TrackHash = a, StartedAt = now.AddDays(-8) });
            db.PlayHistory.Add(new PlayHistory { StoreId = storeId, TrackHash = b, StartedAt = now.AddHours(-1) });
            return db.SaveChangesAsync();
        });

        var tracks = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/tracks"));
        var week = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/history?since={Uri.EscapeDataString(now.AddDays(-7).ToString("O"))}"));
        var all = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/history"));

        Assert.Equal(["B", "A"], tracks.EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        Assert.Equal("B", Assert.Single(week.EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(2, all.GetArrayLength());
    }

    [Fact]
    public async Task Remover_loja_apaga_os_dados_dela_e_derruba_a_conexao()
    {
        await using var hub = await HubHarness.StartAsync();
        var (_, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        using var agent = await ConnectAgentAsync(hub, token);
        await SendAsync(agent, new HelloMessage("loja-2-centro", "0.4.0", 0));
        await ReceiveAgentAsync(agent);

        var deleted = await admin.DeleteAsync($"/api/stores/{storeId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/stores/{storeId}")).StatusCode);
        Assert.Equal(0, await hub.WithDbAsync(db => db.StoreManifests.CountAsync()));
        Assert.False(hub.App.Services.GetRequiredService<AgentConnections>().IsOnline(storeId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await hub.AgentClient(token).GetAsync("/agent/manifest")).StatusCode);
    }

    [Fact]
    public async Task Hub_serve_o_painel_com_csp_e_volta_para_o_index_nas_rotas_do_painel()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "marumbi-hub-tests", "www-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Marumbi Som</title>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "assets", "app-1234.js"), "console.log(1)");
        try
        {
            await using var hub = await HubHarness.StartAsync(webRoot: webRoot);
            var client = hub.AnonymousClient();

            var home = await client.GetAsync("/");
            var deep = await client.GetAsync("/lojas/3");
            var asset = await client.GetAsync("/assets/app-1234.js");
            var missingAsset = await client.GetAsync("/assets/nao-existe.js");
            var api = await client.GetAsync("/api/nao-existe");

            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            Assert.Contains("Marumbi Som", await deep.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal("no-cache", deep.Headers.CacheControl!.ToString());
            Assert.Contains("default-src 'self'", home.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Contains("immutable", asset.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);
            Assert.True(api.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, api.StatusCode.ToString());
            Assert.DoesNotContain("Marumbi Som", await api.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(webRoot, recursive: true);
        }
    }

    private static StatusMessage Status(string hash, BluetoothStatus bluetooth) => new()
    {
        Player = PlayerStatus.Playing,
        NowPlaying = hash,
        PositionMs = 12_000,
        Volume = 70,
        Bluetooth = bluetooth,
        DiskFreeMb = 50_000,
        UptimeS = 100,
    };

    private static async Task<WebSocket> ConnectPanelAsync(HubHarness hub, string? cookie, string? origin = "https://localhost", string? forwardedHost = null)
    {
        var client = hub.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (cookie is not null)
            {
                request.Headers.Cookie = cookie;
            }

            if (origin is not null)
            {
                request.Headers.Origin = origin;
            }

            if (forwardedHost is not null)
            {
                request.Headers["X-Forwarded-Host"] = forwardedHost;
            }
        };
        return await client.ConnectAsync(new Uri("wss://localhost/ws/panel"), None);
    }

    private static async Task<WebSocket> ConnectAgentAsync(HubHarness hub, string token)
    {
        var client = hub.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {token}";
        return await client.ConnectAsync(new Uri("wss://localhost/agent/ws"), None);
    }

    private static Task SendAsync(WebSocket socket, AgentMessage message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options), WebSocketMessageType.Text, true, None);

    private static async Task<HubMessage> ReceiveAgentAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonSerializer.Deserialize<HubMessage>(Encoding.UTF8.GetString(buffer, 0, result.Count), ContractJson.Options)!;
    }

    private static async Task<T> ReceivePanelAsync<T>(WebSocket socket, Func<T, bool> match)
        where T : PanelMessage
    {
        var buffer = new byte[64 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var length = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
                length += result.Count;
            }
            while (!result.EndOfMessage);

            if (JsonSerializer.Deserialize<PanelMessage>(buffer.AsSpan(0, length), HubJson.Options) is T message && match(message))
            {
                return message;
            }
        }
    }

    private static async Task<StoreSummary> ReceiveStoreAsync(WebSocket socket, Func<StoreSummary, bool> match) =>
        (await ReceivePanelAsync<PanelStoreMessage>(socket, m => match(m.Store))).Store;

    private static Task<PanelChangedMessage> ReceiveChangedAsync(WebSocket socket, Func<IReadOnlyList<string>, bool> match) =>
        ReceivePanelAsync<PanelChangedMessage>(socket, m => match(m.Topics));

    private static async Task<JsonElement> WaitForAsync(HttpClient client, string path, Func<JsonElement, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var value = await HubHarness.JsonAsync(await client.GetAsync(path));
            if (match(value) || DateTime.UtcNow > deadline)
            {
                return value;
            }

            await Task.Delay(50);
        }
    }
}
