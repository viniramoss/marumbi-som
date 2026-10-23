using System.Net.Http.Json;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Agent.Tests.Playback;
using MarumbiSom.Hub.Manifests;
using MarumbiSom.Hub.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent.Tests.Sync;

/// <summary>Hub de verdade em memória com uma loja e uma playlist, e o lado do agente (vinculação e sincronização).</summary>
internal sealed class SyncHarness : IAsyncDisposable
{
    public const string HubUrl = "https://localhost/";
    public const string StoreSlug = "loja-2-centro";

    private SyncHarness(HubHarness hub, HttpClient admin, int storeId, int playlistId, List<string> hashes, TimeProvider? agentTime, int bandwidthKbps)
    {
        Hub = hub;
        Admin = admin;
        StoreId = storeId;
        PlaylistId = playlistId;
        Hashes = hashes;
        Player = new PlayerHarness();
        AgentTime = agentTime ?? Player.Time;
        Transport = new TestHubTransport(hub.Server);
        Config = new LocalConfigFile(Player.Paths);
        Link = NewLink();
        Engine = new SyncEngine(
            Link, Transport, Player.Manifests, Player.Cache, Player.Paths, Options.Create(new SyncOptions { BandwidthKbps = bandwidthKbps }),
            AgentTime, NullLogger<SyncEngine>.Instance);
        Engine.Applied += (_, version) => Applied.Add(version);
    }

    public HubHarness Hub { get; }

    public HttpClient Admin { get; }

    public int StoreId { get; }

    public int PlaylistId { get; }

    /// <summary>Faixas da playlist da loja, na ordem.</summary>
    public List<string> Hashes { get; }

    public PlayerHarness Player { get; }

    public TimeProvider AgentTime { get; }

    public TestHubTransport Transport { get; }

    public LocalConfigFile Config { get; }

    public HubLink Link { get; }

    public SyncEngine Engine { get; }

    public List<long> Applied { get; } = [];

    public static async Task<SyncHarness> StartAsync(int tracks = 2, TimeProvider? agentTime = null, int bandwidthKbps = 0)
    {
        var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var hashes = new List<string>();
        for (var i = 1; i <= tracks; i++)
        {
            hashes.Add(await hub.SeedTrackAsync($"Faixa {i}"));
        }

        var playlist = await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/playlists", new { name = "Manhã" }));
        var playlistId = playlist.GetProperty("id").GetInt32();
        await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/playlists/{playlistId}/items", hashes.Select(h => new { hash = h })));
        var store = await HubHarness.JsonAsync(await admin.PostAsJsonAsync("/api/stores", new { name = "Loja 2 – Centro" }));
        var storeId = store.GetProperty("status").GetProperty("id").GetInt32();
        await HubHarness.JsonAsync(await admin.PutAsJsonAsync($"/api/stores/{storeId}/schedules", new object[]
        {
            new { days = new[] { 0, 1, 2, 3, 4, 5, 6 }, start = "00:00", end = "24:00", playlistId },
        }));
        return new SyncHarness(hub, admin, storeId, playlistId, hashes, agentTime, bandwidthKbps);
    }

    /// <summary>Outra instância lendo o mesmo config.json (como depois de reiniciar o agente).</summary>
    public HubLink NewLink() => new(Config, new PlainSecretProtector(), Transport, NullLogger<HubLink>.Instance);

    public async Task<string> EnrollCodeAsync() =>
        (await HubHarness.JsonAsync(await Admin.PostAsync($"/api/stores/{StoreId}/enroll-code", null))).GetProperty("code").GetString()!;

    public async Task<HubLinkInfo> LinkAsync() => await Link.EnrollAsync(HubUrl, await EnrollCodeAsync(), CancellationToken.None);

    /// <summary>Põe uma faixa nova no fim da playlist; o Hub gera uma versão nova do manifesto.</summary>
    public async Task<string> AddTrackAsync(string title)
    {
        var hash = await Hub.SeedTrackAsync(title);
        Hashes.Add(hash);
        await HubHarness.JsonAsync(await Admin.PutAsJsonAsync($"/api/playlists/{PlaylistId}/items", Hashes.Select(h => new { hash = h })));
        return hash;
    }

    public Task<long> HubVersionAsync() =>
        Hub.WithDbAsync(async db => (await ManifestPublisher.LatestAsync(db, StoreId, CancellationToken.None))!.Version);

    /// <summary>Manifesto local antigo (Etapa 2) tocando antes de vincular.</summary>
    public string PublishLocalManifest()
    {
        var hash = Player.AddTrack("Faixa local", 180);
        Player.Publish(new Dictionary<string, Contracts.Manifests.PlaylistDefinition> { ["local"] = PlayerHarness.Playlist(false, hash) });
        return hash;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, string what, int seconds = 10)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition())
        {
            try
            {
                await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Esperou {seconds} s e não aconteceu: {what}");
            }
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 10)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await condition())
        {
            try
            {
                await Task.Delay(50, timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Esperou {seconds} s e não aconteceu: {what}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Engine.Dispose();
        Admin.Dispose();
        await Hub.DisposeAsync();
        Player.Dispose();
    }
}
