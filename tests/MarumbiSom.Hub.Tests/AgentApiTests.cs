using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Agents;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Tests;

public sealed class AgentApiTests
{
    [Fact]
    public async Task Vinculacao_troca_o_codigo_por_um_token_uma_unica_vez()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (storeId, _) = await ManifestTests.StoreWithPlaylistAsync(admin, [await hub.SeedTrackAsync("A")]);
        var code = (await HubHarness.JsonAsync(await admin.PostAsync($"/api/stores/{storeId}/enroll-code", null))).GetProperty("code").GetString()!;
        Assert.Matches("^[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$", code);

        var agent = hub.AnonymousClient(csrf: false);
        var enrolled = await HubHarness.JsonAsync(await agent.PostAsJsonAsync("/agent/enroll", new { code = code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal) }));
        var again = await agent.PostAsJsonAsync("/agent/enroll", new { code });

        Assert.Equal("loja-2-centro", enrolled.GetProperty("storeId").GetString());
        var token = enrolled.GetProperty("token").GetString()!;
        Assert.True(token.Length >= 43);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        var stored = await hub.WithDbAsync(db => db.Stores.SingleAsync(s => s.Id == storeId));
        Assert.Equal(AgentEndpoints.HashSecret(token), stored.TokenHash);
    }

    [Fact]
    public async Task Codigo_vence_em_30_minutos()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (storeId, _) = await ManifestTests.StoreWithPlaylistAsync(admin, [await hub.SeedTrackAsync("A")]);
        var code = (await HubHarness.JsonAsync(await admin.PostAsync($"/api/stores/{storeId}/enroll-code", null))).GetProperty("code").GetString()!;

        hub.Time.Advance(TimeSpan.FromMinutes(31));
        var response = await hub.AnonymousClient(csrf: false).PostAsJsonAsync("/agent/enroll", new { code });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Manifesto_usa_etag_da_versao_e_exige_token()
    {
        await using var hub = await HubHarness.StartAsync();
        var (_, token) = await EnrolledStoreAsync(hub);
        var agent = hub.AgentClient(token);

        var first = await agent.GetAsync("/agent/manifest");
        var body = await HubHarness.JsonAsync(first);
        var etag = first.Headers.ETag!;
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/agent/manifest");
        conditional.Headers.IfNoneMatch.Add(etag);
        var notModified = await agent.SendAsync(conditional);
        var anonymous = await hub.AgentClient("token-que-nao-existe-de-jeito-nenhum").GetAsync("/agent/manifest");

        Assert.Equal($"\"{body.GetProperty("version").GetInt64()}\"", etag.Tag);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Download_aceita_range_e_so_entrega_arquivos_do_manifesto_da_loja()
    {
        await using var hub = await HubHarness.StartAsync();
        var (hash, token) = await EnrolledStoreAsync(hub);
        var outside = await hub.SeedTrackAsync("Fora do manifesto");
        var agent = hub.AgentClient(token);
        var full = await File.ReadAllBytesAsync(hub.Paths.LibraryFile(hash));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/agent/files/{hash}");
        request.Headers.Range = new RangeHeaderValue(10, null);
        var partial = await agent.SendAsync(request);
        var denied = await agent.GetAsync($"/agent/files/{outside}");

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(full[10..], await partial.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task WebSocket_avisa_manifesto_novo_grava_status_e_entrega_comandos()
    {
        await using var hub = await HubHarness.StartAsync();
        var (hash, token) = await EnrolledStoreAsync(hub);
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        var client = hub.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {token}";
        using var socket = await client.ConnectAsync(new Uri("wss://localhost/agent/ws"), CancellationToken.None);

        await SendAsync(socket, new HelloMessage("loja-2-centro", "0.3.0", 0));
        var changed = Assert.IsType<ManifestChangedMessage>(await ReceiveAsync(socket));
        await SendAsync(socket, new StatusMessage
        {
            Player = PlayerStatus.Playing,
            NowPlaying = hash,
            PositionMs = 12_000,
            Volume = 70,
            Bluetooth = BluetoothStatus.Connected,
            PendingVersion = changed.Version,
            SyncProgress = new SyncProgress(1, 3),
            DiskFreeMb = 50_000,
        });
        await SendAsync(socket, new TrackStartedMessage(hash, hub.Time.GetUtcNow()));
        await SendAsync(socket, new ManifestAppliedMessage(changed.Version));

        var admin = await hub.AdminClientAsync();
        var commandTask = admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "setVolume", args = new { volume = 40 } });
        var command = Assert.IsType<CommandMessage>(await ReceiveAsync(socket));
        await SendAsync(socket, new CommandResultMessage(command.Id, true));
        var outcome = await HubHarness.JsonAsync(await commandTask);

        Assert.Equal(40, command.Args!["volume"]!.GetValue<int>());
        Assert.Equal("ok", outcome.GetProperty("result").GetString());
        var detail = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}"));
        var status = detail.GetProperty("status");
        Assert.True(status.GetProperty("online").GetBoolean());
        Assert.Equal("playing", status.GetProperty("player").GetString());
        Assert.Equal("connected", status.GetProperty("bluetooth").GetString());
        Assert.Equal(changed.Version, status.GetProperty("appliedVersion").GetInt64());
        var history = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}/history"));
        Assert.Equal("A", Assert.Single(history.EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("ok", await hub.WithDbAsync(db => db.Commands.Select(c => c.Result).SingleAsync()));
    }

    [Fact]
    public async Task Comando_para_loja_desconectada_responde_offline()
    {
        await using var hub = await HubHarness.StartAsync();
        await EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());

        var outcome = await HubHarness.JsonAsync(await admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "next" }));
        var unknown = await admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "formatarDisco" });

        Assert.Equal("offline", outcome.GetProperty("result").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    internal static async Task<(string Hash, string Token)> EnrolledStoreAsync(HubHarness hub)
    {
        var admin = await hub.AdminClientAsync();
        var hash = await hub.SeedTrackAsync("A");
        var (storeId, _) = await ManifestTests.StoreWithPlaylistAsync(admin, [hash]);
        var code = (await HubHarness.JsonAsync(await admin.PostAsync($"/api/stores/{storeId}/enroll-code", null))).GetProperty("code").GetString()!;
        var enrolled = await HubHarness.JsonAsync(await hub.AnonymousClient(csrf: false).PostAsJsonAsync("/agent/enroll", new { code }));
        return (hash, enrolled.GetProperty("token").GetString()!);
    }

    private static Task SendAsync(WebSocket socket, AgentMessage message) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<HubMessage> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonSerializer.Deserialize<HubMessage>(Encoding.UTF8.GetString(buffer, 0, result.Count), ContractJson.Options)!;
    }
}
