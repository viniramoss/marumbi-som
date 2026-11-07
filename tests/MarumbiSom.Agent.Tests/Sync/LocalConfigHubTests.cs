using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests.Sync;

/// <summary>Seção "Hub" da página Configurar Som: vincular, ver o andamento e desvincular.</summary>
public sealed class LocalConfigHubTests
{
    private const string Password = "senha-forte-123";

    [Fact]
    public async Task Pagina_local_vincula_mostra_o_andamento_e_desvincula()
    {
        await using var h = await SyncHarness.StartAsync();
        var auth = new LocalAuth(h.Config, h.Player.Time, NullLogger<LocalAuth>.Instance);
        auth.SetPassword(Password);
        using var live = new HubClient(
            h.Link, h.Transport, h.Player.Manifests, h.Engine, h.Player.Engine, h.Player.Bluetooth, h.Player.Events, h.Player.Paths,
            new AgentRestart(new NoLifetime()), h.NewUpdater(), h.AgentTime, NullLogger<HubClient>.Instance);
        var windows = new SimulatedWindowsEnvironment(BluetoothAddress.Parse("AA:BB:CC:DD:EE:FF"));
        var context = new LocalConfigContext(
            auth, h.Config, h.Player.Engine, h.Player.Bluetooth, new NoSpeaker(), windows, h.Player.Manifests, h.Player.Paths.LogsDirectory,
            h.Player.Time, new HubContext(h.Link, h.Engine, live));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapLocalConfig(context);
        await app.StartAsync();
        var client = app.GetTestClient();
        var login = await PostAsync(client, "/api/login", new { password = Password });
        client.DefaultRequestHeaders.Add("Cookie", login.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);

        var before = (await StatusAsync(client)).GetProperty("hub");
        Assert.False(before.GetProperty("linked").GetBoolean());

        var wrong = await PostAsync(client, "/api/hub", new { url = SyncHarness.HubUrl, code = "AAAA-BBBB" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("inválido ou vencido", await wrong.Content.ReadAsStringAsync());
        var insecure = await PostAsync(client, "/api/hub", new { url = "http://hub.exemplo.ts.net", code = "AAAA-BBBB" });
        Assert.Contains("https", await insecure.Content.ReadAsStringAsync());

        var linked = await PostAsync(client, "/api/hub", new { url = SyncHarness.HubUrl, code = await h.EnrollCodeAsync() });
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        await h.Engine.SyncOnceAsync(h.Link.Current!, CancellationToken.None);
        var hub = await StatusAsync(client);
        var section = hub.GetProperty("hub");
        Assert.True(section.GetProperty("linked").GetBoolean());
        Assert.Equal("Loja 2 – Centro", section.GetProperty("storeName").GetString());
        Assert.Equal(SyncHarness.StoreSlug, hub.GetProperty("storeId").GetString());
        Assert.Equal(await h.HubVersionAsync(), hub.GetProperty("manifestVersion").GetInt64());
        Assert.Equal(JsonValueKind.String, section.GetProperty("lastCheckAt").ValueKind);
        Assert.DoesNotContain(h.Link.Current!.Token, hub.GetRawText());

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/hub/unlink", new { })).StatusCode);
        Assert.False((await StatusAsync(client)).GetProperty("hub").GetProperty("linked").GetBoolean());
        Assert.Equal(SyncHarness.StoreSlug, h.Player.Manifests.Active!.StoreId); // segue tocando o que tinha
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(LocalConfigEndpoints.CsrfHeader, "1");
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/status")).RootElement.Clone();

    private sealed class NoSpeaker : ISpeakerSelection
    {
        public BluetoothAddress? SpeakerAddress => null;

        public void SetSpeaker(BluetoothAddress? address)
        {
        }
    }

    private sealed class NoLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
