using System.Net;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Sync;

public sealed class SyncEngineTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Vincula_baixa_tudo_confere_e_so_entao_troca_a_configuracao_local()
    {
        await using var h = await SyncHarness.StartAsync();
        h.PublishLocalManifest();
        var hub = await h.LinkAsync();

        await h.Engine.SyncOnceAsync(hub, None);

        var active = h.Player.Manifests.Active!;
        Assert.Equal(SyncHarness.StoreSlug, active.StoreId);
        Assert.Equal(await h.HubVersionAsync(), active.Version);
        Assert.Equal("Manhã", Assert.Single(active.Playlists).Value.Name);
        Assert.Null(h.Player.Manifests.Pending);
        Assert.Equal([active.Version], h.Applied);
        foreach (var hash in h.Hashes)
        {
            Assert.True(await h.Player.Cache.VerifyAsync(hash, None));
            Assert.Equal(1, h.Transport.CountFileRequests(hash));
        }
    }

    [Fact]
    public async Task Nao_troca_enquanto_falta_arquivo_e_a_loja_segue_com_o_que_tinha()
    {
        await using var h = await SyncHarness.StartAsync();
        h.PublishLocalManifest();
        var hub = await h.LinkAsync();
        h.Transport.Intercept = (request, response) => Task.FromResult(TestHubTransport.IsFile(request, h.Hashes[1])
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : response);

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Engine.SyncOnceAsync(hub, None));

        Assert.Equal("loja-teste", h.Player.Manifests.Active!.StoreId);
        Assert.Equal(SyncHarness.StoreSlug, h.Player.Manifests.Pending!.StoreId);
        Assert.True(h.Player.Cache.Has(h.Hashes[0]));
        Assert.False(h.Player.Cache.Has(h.Hashes[1]));
        Assert.Equal(new SyncProgress(1, 2), h.Engine.Progress);
        Assert.Empty(h.Applied);

        h.Transport.Intercept = null;
        await h.Engine.SyncOnceAsync(hub, None);

        Assert.Equal(SyncHarness.StoreSlug, h.Player.Manifests.Active!.StoreId);
        Assert.Null(h.Engine.Progress);
        Assert.Single(h.Applied);
        Assert.Equal(1, h.Transport.CountFileRequests(h.Hashes[0]));
    }

    [Fact]
    public async Task Download_cortado_continua_de_onde_parou()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var hub = await h.LinkAsync();
        var hash = h.Hashes[0];
        h.Transport.Intercept = (request, response) => TestHubTransport.IsFile(request, hash)
            ? TestHubTransport.CutAfterAsync(response, 10)
            : Task.FromResult(response);

        await Assert.ThrowsAsync<IOException>(() => h.Engine.SyncOnceAsync(hub, None));

        var part = Path.Combine(h.Player.Paths.PartialDirectory, hash + ".part");
        Assert.Equal(10, new FileInfo(part).Length);
        Assert.False(h.Player.Cache.Has(hash));

        h.Transport.Intercept = null;
        await h.Engine.SyncOnceAsync(hub, None);

        Assert.Equal("bytes=10-", h.Transport.Requests.Last(r => r.Path == $"/agent/files/{hash}").Range);
        Assert.True(await h.Player.Cache.VerifyAsync(hash, None));
        Assert.False(File.Exists(part));
        Assert.Equal(SyncHarness.StoreSlug, h.Player.Manifests.Active!.StoreId);
    }

    [Fact]
    public async Task Arquivo_que_chega_corrompido_tenta_3_vezes_e_depois_espera_10_minutos()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var time = (FakeTimeProvider)h.AgentTime;
        var hub = await h.LinkAsync();
        var hash = h.Hashes[0];
        h.Transport.Intercept = (request, response) => TestHubTransport.IsFile(request, hash)
            ? TestHubTransport.CorruptAsync(response)
            : Task.FromResult(response);

        for (var i = 0; i < 5; i++)
        {
            await h.Engine.SyncOnceAsync(hub, None);
        }

        Assert.Equal(SyncEngine.MaxAttempts, h.Transport.CountFileRequests(hash));
        Assert.False(h.Player.Cache.Has(hash));
        Assert.Null(h.Player.Manifests.Active);

        h.Transport.Intercept = null;
        time.Advance(SyncEngine.RetryAfterBadHash);
        await h.Engine.SyncOnceAsync(hub, None);

        Assert.Equal(SyncEngine.MaxAttempts + 1, h.Transport.CountFileRequests(hash));
        Assert.True(await h.Player.Cache.VerifyAsync(hash, None));
        Assert.NotNull(h.Player.Manifests.Active);
    }

    [Fact]
    public async Task Arquivo_corrompido_no_cache_e_apagado_e_baixado_de_novo()
    {
        await using var h = await SyncHarness.StartAsync();
        var hub = await h.LinkAsync();
        await h.Engine.SyncOnceAsync(hub, None);
        var version = h.Player.Manifests.Active!.Version;
        var hash = h.Hashes[1];
        await File.WriteAllTextAsync(h.Player.Cache.PathFor(hash), "estragado");

        await h.Engine.VerifyCacheAsync(None);
        Assert.False(h.Player.Cache.Has(hash));
        await h.Engine.SyncOnceAsync(hub, None);

        Assert.Equal(2, h.Transport.CountFileRequests(hash));
        Assert.True(await h.Player.Cache.VerifyAsync(hash, None));
        Assert.Equal(version, h.Player.Manifests.Active!.Version);
    }

    [Fact]
    public async Task Versao_nova_no_meio_do_download_substitui_a_pendente_e_aproveita_o_que_ja_baixou()
    {
        await using var h = await SyncHarness.StartAsync();
        var hub = await h.LinkAsync();
        var first = await h.HubVersionAsync();
        var announced = false;
        h.Transport.Intercept = async (request, response) =>
        {
            if (!announced && TestHubTransport.IsFile(request, h.Hashes[0]))
            {
                announced = true;
                await h.AddTrackAsync("Faixa 3");
                h.Engine.Trigger(); // como o manifest.changed do WebSocket
            }

            return response;
        };

        await h.Engine.SyncOnceAsync(hub, None);
        Assert.Equal(first, h.Player.Manifests.Pending!.Version);
        Assert.Empty(h.Applied);

        await h.Engine.SyncOnceAsync(hub, None);

        var second = await h.HubVersionAsync();
        Assert.True(second > first);
        Assert.Equal([second], h.Applied);
        Assert.Equal(3, h.Player.Manifests.Active!.Tracks.Count);
        Assert.All(h.Hashes, hash => Assert.Equal(1, h.Transport.CountFileRequests(hash)));
    }

    [Fact]
    public async Task Hub_fora_do_ar_nao_mexe_no_que_esta_tocando_e_volta_sozinho()
    {
        await using var h = await SyncHarness.StartAsync();
        var hub = await h.LinkAsync();
        await h.Engine.SyncOnceAsync(hub, None);
        var playing = h.Player.Manifests.Active!;
        h.Transport.Offline = true;
        await h.AddTrackAsync("Faixa 3");
        h.Engine.Trigger();

        await h.Engine.StartAsync(None);
        try
        {
            await SyncHarness.WaitUntilAsync(() => h.Engine.LastError is not null, "registrar a falha");
            Assert.Same(playing, h.Player.Manifests.Active);
            Assert.Contains("fora do ar", h.Engine.LastError);

            h.Transport.Offline = false;
            h.Engine.Trigger();
            await SyncHarness.WaitUntilAsync(() => h.Player.Manifests.Active!.Tracks.Count == 3, "aplicar a versão nova");
            await SyncHarness.WaitUntilAsync(() => h.Engine.LastError is null, "limpar o erro");
        }
        finally
        {
            await h.Engine.StopAsync(None);
        }
    }

    [Fact]
    public async Task Token_revogado_no_painel_pede_para_vincular_de_novo()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var hub = await h.LinkAsync();
        (await h.Admin.DeleteAsync($"/api/stores/{h.StoreId}/token")).EnsureSuccessStatusCode();

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => h.Engine.SyncOnceAsync(hub, None));

        Assert.Contains("vincule a loja de novo", error.Message);
    }
}
