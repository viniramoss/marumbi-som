using System.Security.Cryptography;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests.Sync;

public sealed class HubLinkTests
{
    [Theory]
    [InlineData("hub-escritorio.exemplo.ts.net", "https://hub-escritorio.exemplo.ts.net/")]
    [InlineData(" https://hub-escritorio.exemplo.ts.net ", "https://hub-escritorio.exemplo.ts.net/")]
    [InlineData("https://hub.exemplo.ts.net/som?x=1#y", "https://hub.exemplo.ts.net/som/")]
    [InlineData("http://127.0.0.1:5080", "http://127.0.0.1:5080/")]
    [InlineData("http://localhost:5080/", "http://localhost:5080/")]
    public void Endereco_valido_e_normalizado(string text, string expected)
    {
        Assert.Equal(expected, HubLink.ParseUrl(text, out var error)!.ToString());
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://hub-escritorio.exemplo.ts.net")]
    [InlineData("https://usuario:senha@hub.exemplo.ts.net")]
    [InlineData("ftp://hub.exemplo.ts.net")]
    public void Endereco_invalido_ou_sem_https_e_recusado(string text)
    {
        Assert.Null(HubLink.ParseUrl(text, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Vincula_guarda_o_token_protegido_e_sobrevive_a_reinicio()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var changed = 0;
        h.Link.Changed += (_, _) => changed++;

        var info = await h.LinkAsync();

        Assert.Equal(SyncHarness.StoreSlug, info.StoreId);
        Assert.Equal("Loja 2 – Centro", info.StoreName);
        Assert.Equal(1, changed);
        Assert.Equal("plain:" + info.Token, h.Config.Get(HubLink.Section, "Token"));
        var reloaded = h.NewLink().Current!;
        Assert.Equal(info, reloaded);
    }

    [Fact]
    public async Task Codigo_errado_mostra_o_motivo_e_nao_grava_nada()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);

        var error = await Assert.ThrowsAsync<HubEnrollException>(() => h.Link.EnrollAsync(SyncHarness.HubUrl, "AAAA-BBBB", CancellationToken.None));

        Assert.Contains("código", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.Link.Current);
        Assert.Null(h.Config.Get(HubLink.Section, "Token"));
    }

    [Fact]
    public async Task Hub_fora_do_ar_na_vinculacao_explica_o_problema()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        h.Transport.Offline = true;

        var error = await Assert.ThrowsAsync<HubEnrollException>(() => h.Link.EnrollAsync(SyncHarness.HubUrl, "AAAA-BBBB", CancellationToken.None));

        Assert.Contains("Não foi possível falar com o Hub", error.Message);
    }

    [Fact]
    public async Task Desvincular_apaga_a_vinculacao()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();

        h.Link.Unlink();

        Assert.Null(h.Link.Current);
        Assert.Null(h.NewLink().Current);
        Assert.Null(h.Config.Get(HubLink.Section, "Url"));
    }

    [Fact]
    public async Task Token_que_nao_abre_neste_computador_vira_loja_sem_vinculo()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();

        // Ex.: config.json copiado de outro PC; a DPAPI daqui não abre o token.
        var link = new HubLink(h.Config, new BrokenProtector(), h.Transport, NullLogger<HubLink>.Instance);

        Assert.Null(link.Current);
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(string secret) => secret;

        public string Unprotect(string protectedSecret) => throw new CryptographicException("chave de outro computador");
    }
}
