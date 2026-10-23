using MarumbiSom.Agent.Storage;
using MarumbiSom.Agent.Tools;
using MarumbiSom.Contracts.Manifests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Tools;

public sealed class LocalManifestBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marumbi-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Monta_manifesto_com_musicas_em_ordem_ofertas_e_agenda()
    {
        var music = Folder("musicas", "02 - Banda B - Segunda.mp3", "01 - Primeira.m4a", "leia-me.txt");
        var offers = Folder("ofertas", "Picanha.mp3");
        var paths = new AgentDataPaths(Path.Combine(_root, "dados"));
        var cache = new CacheManager(paths, new FakeTimeProvider(), NullLogger<CacheManager>.Instance);
        var options = LocalManifestBuilder.ParseOptions(
        [
            "--build-manifest", "--music", music, "--offers", offers, "--offer-every-tracks", "3", "--start", "08:00", "--end", "20:00",
            "--days", "1,2,3,4,5,6", "--crossfade", "0", "--volume", "60", "--Agent:DataDirectory=C:\\x",
        ]);

        var manifest = await LocalManifestBuilder.BuildAsync(options, cache, previous: null, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, manifest.Version);
        Assert.Equal(60, manifest.Volume);
        Assert.Equal(0, manifest.CrossfadeS);
        var items = manifest.Playlists["principal"].Items;
        Assert.Equal(2, items.Count);
        Assert.Equal("Primeira", manifest.Tracks[items[0].Hash].Title);
        Assert.Null(manifest.Tracks[items[0].Hash].Artist);
        Assert.Equal("Segunda", manifest.Tracks[items[1].Hash].Title);
        Assert.Equal("Banda B", manifest.Tracks[items[1].Hash].Artist);
        Assert.All(items, i => Assert.True(cache.Has(i.Hash)));
        var schedule = Assert.Single(manifest.Schedules);
        Assert.Equal(new TimeOnly(8, 0), schedule.Start);
        Assert.Equal([1, 2, 3, 4, 5, 6], schedule.Days);
        var offer = Assert.Single(manifest.Spots.Offers);
        Assert.Equal(TrackKind.Offer, manifest.Tracks[offer.Value.Hash].Kind);
        Assert.IsType<EveryTracksPlacement>(Assert.Single(manifest.Spots.Placements));
        Assert.Equal(ManifestHasher.ComputeContentHash(manifest), manifest.ContentHash);
        Assert.False(options.ContainsKey("Agent:DataDirectory=C:\\x"));
    }

    [Fact]
    public async Task Add_acrescenta_playlist_e_horario_mantendo_o_resto()
    {
        var morning = Folder("manha", "a.mp3", "b.mp3");
        var afternoon = Folder("tarde", "c.mp3");
        var offers = Folder("ofertas", "Picanha.mp3");
        var cache = new CacheManager(new AgentDataPaths(Path.Combine(_root, "dados")), new FakeTimeProvider(), NullLogger<CacheManager>.Instance);
        var first = await LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", morning, "--playlist", "manha", "--start", "08:00", "--end", "12:00", "--volume", "55", "--offers", offers, "--offer-every-tracks", "2"]),
            cache, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", afternoon, "--playlist", "tarde", "--start", "12:00", "--end", "20:00", "--add"]),
            cache, first, DateTimeOffset.UtcNow, CancellationToken.None);
        var third = await LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", afternoon, "--playlist", "manha", "--start", "07:00", "--end", "12:00", "--add"]),
            cache, second, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, second.Version);
        Assert.Equal(55, second.Volume);
        Assert.Equal(["manha", "tarde"], second.Playlists.Keys.Order());
        Assert.Equal(["manha", "tarde"], second.Schedules.Select(e => e.PlaylistId));
        Assert.Single(second.Spots.Offers);
        Assert.Single(second.Spots.Placements);
        Assert.Equal(4, second.Tracks.Count);
        Assert.Equal(["tarde", "manha"], third.Schedules.Select(e => e.PlaylistId));
        Assert.Equal(new TimeOnly(7, 0), third.Schedules[1].Start);
        Assert.Equal(2, third.Tracks.Count);
    }

    [Fact]
    public async Task Sem_add_substitui_tudo()
    {
        var morning = Folder("manha", "a.mp3");
        var afternoon = Folder("tarde", "c.mp3");
        var cache = new CacheManager(new AgentDataPaths(Path.Combine(_root, "dados")), new FakeTimeProvider(), NullLogger<CacheManager>.Instance);
        var first = await LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", morning, "--playlist", "manha", "--volume", "55"]), cache, null, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = await LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", afternoon, "--playlist", "tarde"]), cache, first, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(2, second.Version);
        Assert.Equal(70, second.Volume);
        Assert.Equal(["tarde"], second.Playlists.Keys);
    }

    [Fact]
    public async Task Opcao_invalida_e_explicada()
    {
        var music = Folder("musicas", "a.mp3");
        var cache = new CacheManager(new AgentDataPaths(Path.Combine(_root, "dados")), new FakeTimeProvider(), NullLogger<CacheManager>.Instance);

        var error = await Assert.ThrowsAsync<FormatException>(() => LocalManifestBuilder.BuildAsync(
            LocalManifestBuilder.ParseOptions(["--music", music, "--start", "8h"]), cache, null, DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Contains("HH:mm", error.Message);
    }

    private string Folder(string name, params string[] files)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(folder, file), "conteúdo de " + file);
        }

        return folder;
    }
}
