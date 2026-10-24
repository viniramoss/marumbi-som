using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Contracts.Releases;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Tests;

/// <summary>Versões do agente para distribuição e o comando "atualizar agente" (Etapa 6, D27 d).</summary>
public sealed class ReleasesTests
{
    [Fact]
    public async Task Envio_confere_o_sha_e_o_conteudo_e_a_ultima_versao_vira_a_de_distribuicao()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (key, _) = ReleaseSigning.NewKeyPair();

        var first = await ReleasePackages.UploadAsync(admin, ReleasePackages.Package("0.6.10", key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var info = await HubHarness.JsonAsync(first);
        Assert.Equal(("0.6.10", true, 0), (info.GetProperty("version").GetString(), info.GetProperty("isCurrent").GetBoolean(), info.GetProperty("storeCount").GetInt32()));

        var second = ReleasePackages.Package("0.6.11", key);
        Assert.Equal(HttpStatusCode.Created, (await ReleasePackages.UploadAsync(admin, second)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReleasePackages.UploadAsync(admin, second)).StatusCode);

        var list = (await HubHarness.JsonAsync(await admin.GetAsync("/api/releases"))).EnumerateArray().ToList();
        Assert.Equal(["0.6.11", "0.6.10"], list.Select(r => r.GetProperty("version").GetString()));
        Assert.Equal([true, false], list.Select(r => r.GetProperty("isCurrent").GetBoolean()));
        Assert.Equal(second.Zip.LongLength, list[0].GetProperty("sizeBytes").GetInt64());
        Assert.True(File.Exists(Path.Combine(hub.Paths.Releases, "MarumbiSom.Agent-0.6.11.zip")));
        Assert.Empty(Directory.GetFiles(hub.Paths.Releases, ".envio-*"));
        Assert.Equal(2, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "releases.upload")));
    }

    [Fact]
    public async Task Envio_recusa_pacote_que_nao_confere_com_a_assinatura_ou_sem_os_arquivos_do_agente()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (key, _) = ReleaseSigning.NewKeyPair();

        async Task<string> RejectedAsync(ReleasePackages.ReleaseFiles files)
        {
            var response = await ReleasePackages.UploadAsync(admin, files);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await ErrorAsync(response);
        }

        var good = ReleasePackages.Package("0.6.10", key);
        var other = ReleasePackages.Package("0.6.10", key, extra: "outro conteúdo");
        Assert.Contains("SHA-256", await RejectedAsync(good with { Zip = other.Zip }), StringComparison.Ordinal);
        Assert.Contains("MarumbiSom.Updater.exe", await RejectedAsync(ReleasePackages.Package("0.6.10", key, files: ["MarumbiSom.Agent.exe", ReleaseSigning.PublicKeyFileName])), StringComparison.Ordinal);
        Assert.Contains("caminhos inválidos", await RejectedAsync(ReleasePackages.Package("0.6.10", key, extra: "x", extraName: "../fora.txt")), StringComparison.Ordinal);
        Assert.Contains("não é um .zip", await RejectedAsync(ReleasePackages.SignedZip("0.6.10", key, Encoding.UTF8.GetBytes("isto não é zip"))), StringComparison.Ordinal);
        Assert.Contains("assinatura inválido", await RejectedAsync(good with { Signature = "{\"version\":\"1.2\"}"u8.ToArray() }), StringComparison.Ordinal);
        Assert.Contains("dois arquivos", await RejectedAsync(good with { Signature = null }), StringComparison.Ordinal);

        Assert.Empty(await hub.WithDbAsync(db => db.AgentReleases.ToListAsync()));
        Assert.Empty(Directory.GetFiles(hub.Paths.Releases));
    }

    [Fact]
    public async Task Escolher_e_remover_versoes()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        var (key, _) = ReleaseSigning.NewKeyPair();
        await ReleasePackages.UploadAsync(admin, ReleasePackages.Package("0.6.10", key));
        await ReleasePackages.UploadAsync(admin, ReleasePackages.Package("0.6.11", key));

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync("/api/releases/0.6.11")).StatusCode);
        var current = await HubHarness.JsonAsync(await admin.PutAsJsonAsync("/api/releases/current", new { version = "0.6.10" }));
        Assert.True(current.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/releases/current", new { version = "9.9.9" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/releases/0.6.11")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/releases/0.6.11")).StatusCode);
        Assert.False(File.Exists(Path.Combine(hub.Paths.Releases, "MarumbiSom.Agent-0.6.11.zip")));
        var remaining = Assert.Single((await HubHarness.JsonAsync(await admin.GetAsync("/api/releases"))).EnumerateArray());
        Assert.Equal(("0.6.10", true), (remaining.GetProperty("version").GetString(), remaining.GetProperty("isCurrent").GetBoolean()));
    }

    [Fact]
    public async Task Agente_ve_a_versao_atual_e_baixa_o_pacote_so_com_o_token()
    {
        await using var hub = await HubHarness.StartAsync();
        var (_, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var agent = hub.AgentClient(token);
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/agent/releases/current")).StatusCode);

        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        var package = ReleasePackages.Package("0.6.12", key);
        await ReleasePackages.UploadAsync(admin, package);

        var info = (await agent.GetFromJsonAsync<AgentReleaseInfo>("/agent/releases/current", ContractJson.Options))!;
        Assert.Equal(("0.6.12", package.Zip.LongLength), (info.Version, info.SizeBytes));
        Assert.True(ReleaseSigning.Verify(publicKey, info.Version, info.Sha256, info.Signature));
        Assert.Equal(package.Zip, await agent.GetByteArrayAsync("/agent/releases/0.6.12/package"));

        // Retomar o download do ponto em que parou.
        using var partial = new HttpRequestMessage(HttpMethod.Get, "/agent/releases/0.6.12/package");
        partial.Headers.Range = new RangeHeaderValue(100, null);
        var rest = await agent.SendAsync(partial);
        Assert.Equal(HttpStatusCode.PartialContent, rest.StatusCode);
        Assert.Equal(package.Zip[100..], await rest.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/agent/releases/0.6.99/package")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hub.AgentClient("token-que-nao-existe-de-jeito-nenhum").GetAsync("/agent/releases/0.6.12/package")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hub.AnonymousClient().GetAsync("/agent/releases/current")).StatusCode);
    }

    [Fact]
    public async Task Atualizar_agente_leva_versao_sha_e_assinatura_e_o_painel_acompanha_o_andamento()
    {
        await using var hub = await HubHarness.StartAsync();
        var (hash, token) = await AgentApiTests.EnrolledStoreAsync(hub);
        var admin = await hub.AdminClientAsync();
        var storeId = await hub.WithDbAsync(db => db.Stores.Select(s => s.Id).SingleAsync());
        using var agent = await PanelTests.ConnectAgentAsync(hub, token);
        await PanelTests.SendAsync(agent, new HelloMessage("loja-2-centro", "0.6.10", 0));
        await PanelTests.ReceiveAgentAsync(agent);

        var none = await admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "updateAgent" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("Configurações", await ErrorAsync(none), StringComparison.Ordinal);

        var (key, _) = ReleaseSigning.NewKeyPair();
        var package = ReleasePackages.Package("0.6.12", key);
        await ReleasePackages.UploadAsync(admin, package);
        var commandTask = admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "updateAgent" });
        var command = Assert.IsType<CommandMessage>(await PanelTests.ReceiveAgentAsync(agent));
        await PanelTests.SendAsync(agent, new CommandResultMessage(command.Id, true));
        Assert.Equal("ok", (await HubHarness.JsonAsync(await commandTask)).GetProperty("result").GetString());

        var args = command.Args.Deserialize<AgentReleaseInfo>(ContractJson.Options)!;
        Assert.Equal(new AgentReleaseInfo("0.6.12", ReleasePackages.Sha(package.Zip), package.Zip.LongLength, package.SignatureText), args);
        var wrong = await admin.PostAsJsonAsync($"/api/stores/{storeId}/commands", new { command = "updateAgent", args = new { version = "0.5.0" } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        await PanelTests.SendAsync(agent, PanelTests.Status(hash, BluetoothStatus.Connected) with
        {
            Update = new UpdateProgress("0.6.12", UpdateStages.Downloading, 1000, package.Zip.LongLength),
        });
        var downloading = (await PanelTests.WaitForAsync(admin, $"/api/stores/{storeId}", d => d.GetProperty("status").GetProperty("update").ValueKind == JsonValueKind.Object))
            .GetProperty("status").GetProperty("update");
        Assert.Equal(("0.6.12", "downloading", 1000L), (downloading.GetProperty("version").GetString(), downloading.GetProperty("stage").GetString(), downloading.GetProperty("doneBytes").GetInt64()));

        // O agente novo sobe sem andamento (some da tela) e manda o resultado por evento.
        await PanelTests.SendAsync(agent, PanelTests.Status(hash, BluetoothStatus.Connected));
        await PanelTests.WaitForAsync(admin, $"/api/stores/{storeId}", d => d.GetProperty("status").GetProperty("update").ValueKind == JsonValueKind.Null);
        await PanelTests.SendAsync(agent, new AgentEventMessage(AgentEvents.UpdateRolledBack, new() { ["version"] = "0.6.12", ["error"] = "A versão nova não confirmou em 2 minutos." }));
        var rolledBack = (await PanelTests.WaitForAsync(admin, $"/api/stores/{storeId}", d => d.GetProperty("status").GetProperty("update").ValueKind == JsonValueKind.Object))
            .GetProperty("status").GetProperty("update");
        Assert.Equal(("rolledBack", "A versão nova não confirmou em 2 minutos."), (rolledBack.GetProperty("stage").GetString(), rolledBack.GetProperty("error").GetString()));

        // O resultado fica 24 horas no painel.
        hub.Time.Advance(TimeSpan.FromHours(25));
        var later = await HubHarness.JsonAsync(await admin.GetAsync($"/api/stores/{storeId}"));
        Assert.Equal(JsonValueKind.Null, later.GetProperty("status").GetProperty("update").ValueKind);
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

}
