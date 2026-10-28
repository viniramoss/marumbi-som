using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Contracts.Tests;

public class ManifestHasherTests
{
    private static readonly Manifest Sample = ManifestSerializationTests.LoadSample();

    [Fact]
    public void Hash_tem_prefixo_sha256_e_64_digitos_hexadecimais()
    {
        var hash = ManifestHasher.ComputeContentHash(Sample);

        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);
        Assert.Matches("^sha256:[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_ignora_version_generatedAt_e_contentHash()
    {
        var other = Sample with
        {
            Version = 999,
            GeneratedAt = Sample.GeneratedAt.AddDays(3),
            ContentHash = "sha256:qualquer",
        };

        Assert.Equal(ManifestHasher.ComputeContentHash(Sample), ManifestHasher.ComputeContentHash(other));
    }

    [Fact]
    public void Hash_muda_quando_a_ordem_da_playlist_muda()
    {
        var tarde = Sample.Playlists["tarde"];
        var reordered = Sample with
        {
            Playlists = new Dictionary<string, PlaylistDefinition>(Sample.Playlists)
            {
                ["tarde"] = tarde with { Items = tarde.Items.Reverse().ToList() },
            },
        };

        Assert.NotEqual(ManifestHasher.ComputeContentHash(Sample), ManifestHasher.ComputeContentHash(reordered));
    }

    [Fact]
    public void Hash_muda_quando_o_volume_muda()
    {
        Assert.NotEqual(ManifestHasher.ComputeContentHash(Sample), ManifestHasher.ComputeContentHash(Sample with { Volume = 71 }));
    }

    [Fact]
    public void Hash_muda_quando_a_mixagem_muda()
    {
        Assert.NotEqual(ManifestHasher.ComputeContentHash(Sample), ManifestHasher.ComputeContentHash(Sample with { CrossfadeS = 0 }));
    }

    [Fact]
    public void Hash_nao_depende_da_ordem_de_insercao_dos_dicionarios()
    {
        var reversedTracks = Sample with
        {
            Tracks = Sample.Tracks.Reverse().ToDictionary(p => p.Key, p => p.Value),
            Playlists = Sample.Playlists.Reverse().ToDictionary(p => p.Key, p => p.Value),
        };

        Assert.Equal(ManifestHasher.ComputeContentHash(Sample), ManifestHasher.ComputeContentHash(reversedTracks));
    }

    [Fact]
    public void Hash_canonico_e_estavel_entre_versoes_do_codigo()
    {
        // Valor fixo: se este teste quebrar, o formato canônico mudou e todas as lojas receberiam
        // uma nova versão de manifesto sem mudança real. Só atualize com decisão registrada na seção 18.
        Assert.Equal(GoldenHash, ManifestHasher.ComputeContentHash(Sample));
    }

    [Fact]
    public void WithContentHash_preenche_o_campo()
    {
        var withHash = ManifestHasher.WithContentHash(Sample);

        Assert.Equal(ManifestHasher.ComputeContentHash(Sample), withHash.ContentHash);
    }

    private const string GoldenHash = "sha256:c464fd047636eb1666ae7927a9b439d8533ea4dcdaa8fd917d1df26c233e4d86";
}
