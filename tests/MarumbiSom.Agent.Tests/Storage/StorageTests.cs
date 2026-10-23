using System.Security.Cryptography;
using System.Text;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Storage;

public sealed class StorageTests : IDisposable
{
    private readonly AgentDataPaths _paths = new(Path.Combine(Path.GetTempPath(), "marumbi-tests", Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(-3)));

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Gravacao_atomica_substitui_sem_deixar_temporario()
    {
        var file = Path.Combine(_paths.Root, "x", "state.json");

        AtomicFile.WriteAllText(file, "um");
        AtomicFile.WriteAllText(file, "dois");

        Assert.Equal("dois", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Manifesto_pendente_menor_ou_igual_ao_ativo_e_rejeitado()
    {
        var store = NewManifestStore();
        store.SaveActive(Manifest(5));

        Assert.False(store.SavePending(Manifest(5)));
        Assert.False(store.SavePending(Manifest(4)));
        Assert.True(store.SavePending(Manifest(6)));
        Assert.Throws<InvalidOperationException>(() => store.SaveActive(Manifest(3)));
    }

    [Fact]
    public void Promover_pendente_grava_ativo_e_apaga_o_pendente()
    {
        var store = NewManifestStore();
        store.SaveActive(Manifest(1));
        store.SavePending(Manifest(2));
        Manifest? changed = null;
        store.ActiveChanged += (_, m) => changed = m;

        store.PromotePending();

        Assert.Equal(2, changed?.Version);
        Assert.False(File.Exists(_paths.PendingManifest));
        var reloaded = NewManifestStore();
        reloaded.Load();
        Assert.Equal(2, reloaded.Active?.Version);
        Assert.Null(reloaded.Pending);
    }

    [Fact]
    public void Manifesto_trocado_no_disco_e_recarregado_e_corrompido_e_ignorado()
    {
        var store = NewManifestStore();
        store.SaveActive(Manifest(1));
        Assert.False(store.ReloadIfChangedOnDisk());

        File.WriteAllText(_paths.ActiveManifest, ManifestStore.Serialize(Manifest(7)));
        File.SetLastWriteTimeUtc(_paths.ActiveManifest, DateTime.UtcNow.AddMinutes(1));
        Assert.True(store.ReloadIfChangedOnDisk());
        Assert.Equal(7, store.Active?.Version);

        File.WriteAllText(_paths.ActiveManifest, "{ quebrado");
        File.SetLastWriteTimeUtc(_paths.ActiveManifest, DateTime.UtcNow.AddMinutes(2));
        Assert.False(store.ReloadIfChangedOnDisk());
        Assert.Equal(7, store.Active?.Version);
    }

    [Fact]
    public async Task Cache_identifica_ausentes_e_apaga_arquivo_corrompido()
    {
        var cache = NewCache();
        var good = await ImportAsync(cache, "boa");
        var bad = await ImportAsync(cache, "ruim");
        await File.WriteAllTextAsync(cache.PathFor(bad), "conteúdo trocado");
        var absent = Hash("ausente");
        var manifest = Manifest(1) with { Tracks = Tracks(good, bad, absent) };

        Assert.Equal([absent], cache.Missing(manifest));
        Assert.True(await cache.VerifyAsync(good, CancellationToken.None));
        Assert.False(await cache.VerifyAsync(bad, CancellationToken.None));
        Assert.False(cache.Has(bad));
    }

    [Fact]
    public void Hash_invalido_nao_vira_caminho()
    {
        var cache = NewCache();

        Assert.Throws<ArgumentException>(() => cache.PathFor(@"..\..\Windows\win.ini"));
        Assert.False(cache.Has("ABC"));
    }

    [Fact]
    public async Task Limpeza_nunca_apaga_referenciado_e_apaga_nao_referenciado_apos_7_dias()
    {
        var cache = NewCache();
        var used = await ImportAsync(cache, "usada");
        var old = await ImportAsync(cache, "antiga");
        var since = new Dictionary<string, DateTimeOffset>();
        var referenced = new HashSet<string> { used };

        Assert.Equal(0, cache.Cleanup(referenced, since));
        Assert.True(since.ContainsKey(old));

        _time.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, cache.Cleanup(referenced, since));
        Assert.True(cache.Has(used));
        Assert.False(cache.Has(old));
        Assert.Empty(since);
    }

    [Fact]
    public async Task Acima_do_limite_remove_os_nao_referenciados_mais_antigos_primeiro()
    {
        var cache = NewCache();
        var used = await ImportAsync(cache, new string('u', 100));
        var older = await ImportAsync(cache, new string('o', 100));
        var since = new Dictionary<string, DateTimeOffset>();
        cache.Cleanup(new HashSet<string> { used }, since);
        _time.Advance(TimeSpan.FromDays(1));
        var newer = await ImportAsync(cache, new string('n', 100));

        cache.Cleanup(new HashSet<string> { used }, since, maxBytes: 250);

        Assert.True(cache.Has(used));
        Assert.False(cache.Has(older));
        Assert.True(cache.Has(newer));

        // Mesmo muito acima do limite, o referenciado fica.
        cache.Cleanup(new HashSet<string> { used }, since, maxBytes: 1);
        Assert.True(cache.Has(used));
    }

    [Fact]
    public void Estado_persiste_e_arquivo_invalido_comeca_vazio()
    {
        var state = new StateStore(_paths, NullLogger<StateStore>.Instance);
        state.State.Playlists["manha"] = new PlaylistCursorState { Index = 3, LastItemId = "it-4", Order = ["it-1"] };
        state.State.VolumeOverride = 55;
        state.State.SpotTrackCounters["pl-1"] = 2;
        state.Save();

        var reloaded = new StateStore(_paths, NullLogger<StateStore>.Instance);
        reloaded.Load();
        Assert.Equal("it-4", reloaded.State.Playlists["manha"].LastItemId);
        Assert.Equal(55, reloaded.State.VolumeOverride);
        Assert.Equal(2, reloaded.State.SpotTrackCounters["pl-1"]);

        File.WriteAllText(_paths.StateFile, "lixo");
        var broken = new StateStore(_paths, NullLogger<StateStore>.Instance);
        broken.Load();
        Assert.Empty(broken.State.Playlists);
    }

    private ManifestStore NewManifestStore() => new(_paths, NullLogger<ManifestStore>.Instance);

    private CacheManager NewCache() => new(_paths, _time, NullLogger<CacheManager>.Instance);

    private async Task<string> ImportAsync(CacheManager cache, string content)
    {
        var source = Path.Combine(_paths.Root, "origem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_paths.Root);
        await File.WriteAllTextAsync(source, content);
        return await cache.ImportAsync(source, CancellationToken.None);
    }

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static Dictionary<string, TrackInfo> Tracks(params string[] hashes) =>
        hashes.ToDictionary(h => h, h => new TrackInfo { Kind = TrackKind.Music, Size = 1, DurationMs = 1000, Title = h[..6] });

    private static Manifest Manifest(long version) => new()
    {
        StoreId = "loja-2",
        Version = version,
        GeneratedAt = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(-3)),
        Timezone = "America/Sao_Paulo",
        Volume = 70,
    };
}
