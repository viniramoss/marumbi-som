using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Contracts.Tests;

/// <summary>Assinatura Ed25519 dos pacotes do agente e do Hub (seção 12.2, D27 a, D32 a).</summary>
public sealed class ReleaseSigningTests
{
    private const string Sha = "ad9be2eacc2a5404ab8b5fa4db84049fd6f1f163214d9af159684cd0cb47c659";

    [Fact]
    public void Assinatura_confere_so_com_a_mesma_versao_o_mesmo_pacote_e_a_chave_certa()
    {
        var (privateKey, publicKey) = ReleaseSigning.NewKeyPair();
        var signature = ReleaseSigning.Sign(privateKey, "0.6.41", Sha);

        Assert.Equal(publicKey, ReleaseSigning.PublicKeyOf(privateKey));
        Assert.True(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, signature));
        Assert.True(ReleaseSigning.Verify(publicKey, "0.6.41", Sha.ToUpperInvariant(), signature));
        // Pacote antigo rotulado como novo, outro pacote, outra chave, assinatura adulterada ou lixo: tudo recusado.
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.42", Sha, signature));
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.41", Sha.Replace('a', 'b'), signature));
        Assert.False(ReleaseSigning.Verify(ReleaseSigning.NewKeyPair().PublicKey, "0.6.41", Sha, signature));
        var tampered = Convert.FromBase64String(signature);
        tampered[10] ^= 1;
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, Convert.ToBase64String(tampered)));
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, "não é base64"));
        Assert.False(ReleaseSigning.Verify("AAAA", "0.6.41", Sha, signature));
    }

    [Fact]
    public void Assinatura_do_agente_nao_vale_para_o_hub_nem_o_contrario()
    {
        var (privateKey, publicKey) = ReleaseSigning.NewKeyPair();
        var agent = ReleaseSigning.Sign(privateKey, "0.6.41", Sha);
        var hub = ReleaseSigning.Sign(privateKey, "0.6.41", Sha, ReleaseProducts.Hub);

        Assert.True(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, hub, ReleaseProducts.Hub));
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, agent, ReleaseProducts.Hub));
        Assert.False(ReleaseSigning.Verify(publicKey, "0.6.41", Sha, hub));
        Assert.Throws<ArgumentException>(() => ReleaseSigning.Sign(privateKey, "0.6.41", Sha, "painel"));
    }

    [Fact]
    public void Arquivo_de_chave_privada_tem_formato_proprio()
    {
        var (privateKey, _) = ReleaseSigning.NewKeyPair();

        Assert.StartsWith("marumbi-som-ed25519-private:", privateKey, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => ReleaseSigning.Sign("chave qualquer", "0.6.41", Sha));
        // Comentários antes da chave são aceitos (o new-signing-key escreve um aviso no topo).
        Assert.Equal(ReleaseSigning.PublicKeyOf(privateKey), ReleaseSigning.PublicKeyOf("# não compartilhe\n" + privateKey + "\n"));
    }

    [Fact]
    public void Chave_publica_ignora_comentarios_e_linhas_vazias()
    {
        Assert.Equal("abc=", ReleaseSigning.ReadPublicKey("# Chave pública\r\n\r\n  abc=  \r\n"));
        Assert.Null(ReleaseSigning.ReadPublicKey("# ainda não gerada\n"));
    }

    [Fact]
    public void Versoes_sao_comparadas_como_numeros()
    {
        Assert.True(ReleaseSigning.IsVersion("0.6.41"));
        Assert.False(ReleaseSigning.IsVersion("0.6"));
        Assert.False(ReleaseSigning.IsVersion("0.6.41-beta"));
        Assert.Equal("0.6.41", ReleaseSigning.ShortVersion("0.6.41+36672a7"));
        Assert.True(ReleaseSigning.CompareVersions("0.6.9", "0.6.10") < 0);
        Assert.True(ReleaseSigning.CompareVersions("1.0.0", "0.9.99") > 0);
        Assert.Equal(0, ReleaseSigning.CompareVersions("0.6.41", "0.6.41"));
    }
}
