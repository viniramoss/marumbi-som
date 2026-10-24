using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Agent.Tests.Sync;
using MarumbiSom.Agent.Updates;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Updates;

/// <summary>Atualização remota do lado do agente (Etapa 6, seção 12.2, D27 e).</summary>
public sealed class AgentUpdaterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Comando_do_hub_baixa_confere_a_assinatura_e_entrega_ao_atualizador()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1, agentTime: TimeProvider.System);
        await h.LinkAsync();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        InstallKey(h, publicKey);
        var package = ReleasePackages.Package("0.6.12", key);
        await ReleasePackages.UploadAsync(h.Admin, package);
        var launcher = new FakeUpdaterLauncher();
        using var updater = h.NewUpdater(launcher);
        var events = Capture(updater);
        using var client = new HubClient(
            h.Link, h.Transport, h.Player.Manifests, h.Engine, h.Player.Engine, h.Player.Bluetooth, h.Player.Events, h.Player.Paths,
            new AgentRestart(new NoLifetime()), updater, TimeProvider.System, NullLogger<HubClient>.Instance);
        await client.StartAsync(None);
        try
        {
            await SyncHarness.WaitUntilAsync(() => client.Connected, "conectar ao Hub");
            var outcome = await HubHarness.JsonAsync(await h.Admin.PostAsJsonAsync($"/api/stores/{h.StoreId}/commands", new { command = "updateAgent" }));
            Assert.Equal("ok", outcome.GetProperty("result").GetString());

            await SyncHarness.WaitUntilAsync(() => !launcher.Launches.IsEmpty, "entregar a instalação ao atualizador");
            Assert.Equal(h.ProgramDirectory, Assert.Single(launcher.Launches));
            var paths = h.Player.Paths.Updates;
            Assert.Equal(package.Zip, await File.ReadAllBytesAsync(paths.PackagePath("0.6.12")));
            var pending = JsonSerializer.Deserialize<PendingUpdate>(await File.ReadAllTextAsync(paths.Pending), ContractJson.Options)!;
            Assert.Equal(("0.6.12", "0.6.10", paths.PackagePath("0.6.12"), h.ProgramDirectory, "MarumbiSom.Agent"),
                (pending.Version, pending.PreviousVersion, pending.PackagePath, pending.ProgramDirectory, pending.ServiceName));
            Assert.Equal(64, pending.Sha256.Length);
            Assert.Equal(UpdateStages.Installing, updater.Progress?.Stage);
            Assert.Contains(h.Transport.Requests, r => r.Path == "/agent/releases/0.6.12/package");
            await SyncHarness.WaitUntilAsync(
                () => h.Hub.WithDbAsync(db => db.StoreStates.AnyAsync(s => s.StoreId == h.StoreId && s.UpdateStage == UpdateStages.Installing)),
                "o painel ver a instalação em andamento");

            // O Updater terminou: o resultado vira evento e o andamento some.
            WriteResult(h, new UpdateResult("0.6.12", "0.6.10", UpdateStages.Done, null, DateTimeOffset.UtcNow));
            updater.CheckResult();
            await SyncHarness.WaitUntilAsync(
                () => h.Hub.WithDbAsync(db => db.StoreStates.AnyAsync(s => s.StoreId == h.StoreId && s.UpdateStage == UpdateStages.Done && s.UpdateVersion == "0.6.12")),
                "o painel ver a atualização concluída");
            Assert.Null(updater.Progress);
            Assert.False(File.Exists(paths.Pending));
            Assert.False(File.Exists(paths.Result));
            Assert.Equal([AgentEvents.UpdateDone], events.Select(e => e.Event));
        }
        finally
        {
            await client.StopAsync(None);
        }
    }

    [Fact]
    public async Task Pacote_com_assinatura_invalida_e_recusado_sem_baixar()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();
        var (_, publicKey) = ReleaseSigning.NewKeyPair();
        InstallKey(h, publicKey);
        var (otherKey, _) = ReleaseSigning.NewKeyPair();
        var info = await PublishAsync(h, ReleasePackages.Package("0.6.12", otherKey));
        var launcher = new FakeUpdaterLauncher();
        using var updater = h.NewUpdater(launcher);
        var events = Capture(updater);

        var refused = updater.TryStart(info);

        Assert.StartsWith("Assinatura inválida", refused, StringComparison.Ordinal);
        var failed = Assert.Single(events);
        Assert.Equal((AgentEvents.UpdateFailed, "0.6.12", refused), (failed.Event, (string?)failed.Data!["version"], (string?)failed.Data["error"]));
        Assert.DoesNotContain(h.Transport.Requests, r => r.Path.Contains("/package", StringComparison.Ordinal));
        Assert.Null(updater.Progress);
        Assert.Empty(launcher.Launches);
    }

    [Fact]
    public async Task Sem_chave_publica_ou_com_versao_que_nao_e_mais_nova_recusa()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        var info = await PublishAsync(h, ReleasePackages.Package("0.6.12", key));
        using var updater = h.NewUpdater(version: "0.6.12");
        var events = Capture(updater);

        Assert.Contains(ReleaseSigning.PublicKeyFileName, updater.TryStart(info), StringComparison.Ordinal);
        Assert.Equal(AgentEvents.UpdateFailed, Assert.Single(events).Event);

        // O arquivo do repositório antes de gerar a chave só tem comentários.
        Directory.CreateDirectory(h.ProgramDirectory);
        await File.WriteAllTextAsync(Path.Combine(h.ProgramDirectory, ReleaseSigning.PublicKeyFileName), "# cole aqui a chave pública\n");
        Assert.Contains(ReleaseSigning.PublicKeyFileName, updater.TryStart(info), StringComparison.Ordinal);

        InstallKey(h, publicKey);
        events.Clear();
        Assert.Equal("O agente já está na versão 0.6.12. Só dá para atualizar para uma versão mais nova.", updater.TryStart(info));
        Assert.Empty(events);
        Assert.Equal("Pedido de atualização inválido.", updater.TryStart(info with { Version = "1.2" }));
        Assert.Equal("Pedido de atualização inválido.", updater.TryStart(null));
    }

    [Fact]
    public async Task Download_interrompido_continua_de_onde_parou()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var h = await SyncHarness.StartAsync(tracks: 1, agentTime: time);
        await h.LinkAsync();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        InstallKey(h, publicKey);
        var info = await PublishAsync(h, ReleasePackages.Package("0.6.12", key, extra: Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(40_000))));
        var cut = 0;
        h.Transport.Intercept = (request, response) =>
            request.RequestUri!.AbsolutePath.EndsWith("/package", StringComparison.Ordinal) && Interlocked.Increment(ref cut) == 1
                ? TestHubTransport.CutAfterAsync(response, 1000)
                : Task.FromResult(response);
        var launcher = new FakeUpdaterLauncher();
        using var updater = h.NewUpdater(launcher);

        Assert.Null(updater.TryStart(info));
        await SyncHarness.WaitUntilAsync(
            () =>
            {
                time.Advance(TimeSpan.FromSeconds(5));
                return !launcher.Launches.IsEmpty;
            },
            "baixar o resto e entregar ao atualizador");

        var requests = h.Transport.Requests.Where(r => r.Path == "/agent/releases/0.6.12/package").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("bytes=1000-", requests[1].Range);
        Assert.Equal(info.SizeBytes, new FileInfo(h.Player.Paths.Updates.PackagePath("0.6.12")).Length);
    }

    [Fact]
    public async Task Pacote_que_chega_diferente_do_assinado_e_apagado_e_reportado()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        InstallKey(h, publicKey);
        var info = await PublishAsync(h, ReleasePackages.Package("0.6.12", key));
        h.Transport.Intercept = (request, response) =>
            request.RequestUri!.AbsolutePath.EndsWith("/package", StringComparison.Ordinal) ? TestHubTransport.CorruptAsync(response) : Task.FromResult(response);
        var launcher = new FakeUpdaterLauncher();
        using var updater = h.NewUpdater(launcher);
        var events = Capture(updater);

        Assert.Null(updater.TryStart(info));
        await updater.Running!;

        var failed = Assert.Single(events);
        Assert.Equal(AgentEvents.UpdateFailed, failed.Event);
        Assert.Contains("SHA-256 diferente", (string?)failed.Data!["error"], StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(h.Player.Paths.Updates.DownloadDirectory));
        Assert.Empty(launcher.Launches);
        Assert.Null(updater.Progress);
        Assert.False(File.Exists(h.Player.Paths.Updates.Pending));
    }

    [Fact]
    public async Task Atualizador_que_nao_inicia_reporta_falha_e_libera_nova_tentativa()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        await h.LinkAsync();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        InstallKey(h, publicKey);
        var info = await PublishAsync(h, ReleasePackages.Package("0.6.12", key));
        var launcher = new FakeUpdaterLauncher { Fail = new InvalidOperationException("O agente instalado não tem o MarumbiSom.Updater.exe.") };
        using var updater = h.NewUpdater(launcher);
        var events = Capture(updater);

        Assert.Null(updater.TryStart(info));
        await updater.Running!;

        Assert.Equal("O agente instalado não tem o MarumbiSom.Updater.exe.", (string?)Assert.Single(events).Data!["error"]);
        Assert.False(File.Exists(h.Player.Paths.Updates.Pending));
        launcher.Fail = null;
        Assert.Null(updater.TryStart(info));
        await updater.Running!;
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public async Task Agente_novo_confirma_depois_do_hello_e_manda_o_resultado_do_atualizador()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var paths = h.Player.Paths.Updates;
        Directory.CreateDirectory(paths.Root);
        WritePending(h, new PendingUpdate("0.6.12", "0.6.10", paths.PackagePath("0.6.12"), new string('a', 64), h.ProgramDirectory, "MarumbiSom.Agent", h.Player.Time.GetUtcNow()));

        // O agente antigo não confirma (o Updater espera a versão nova).
        using (var old = h.NewUpdater(version: "0.6.10"))
        {
            old.Confirm();
            Assert.False(File.Exists(paths.Confirmed));
        }

        using var updater = h.NewUpdater(version: "0.6.12");
        var events = Capture(updater);
        await updater.StartAsync(None);
        try
        {
            await SyncHarness.WaitUntilAsync(() => updater.Progress is not null, "mostrar a instalação em andamento ao subir");
            Assert.Equal(("0.6.12", UpdateStages.Installing), (updater.Progress!.Version, updater.Progress.Stage));
            updater.Confirm();
            var confirmed = JsonSerializer.Deserialize<UpdateConfirmation>(await File.ReadAllTextAsync(paths.Confirmed), ContractJson.Options)!;
            Assert.Equal("0.6.12", confirmed.Version);

            WriteResult(h, new UpdateResult("0.6.12", "0.6.10", UpdateStages.Done, null, h.Player.Time.GetUtcNow()));
            updater.CheckResult();
            var done = Assert.Single(events);
            Assert.Equal((AgentEvents.UpdateDone, "0.6.12", "0.6.10"), (done.Event, (string?)done.Data!["version"], (string?)done.Data["previousVersion"]));
            Assert.Null(updater.Progress);
            Assert.False(File.Exists(paths.Confirmed));
        }
        finally
        {
            await updater.StopAsync(None);
        }
    }

    [Fact]
    public async Task Versao_revertida_ou_atualizador_abandonado_viram_eventos()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1);
        var paths = h.Player.Paths.Updates;
        Directory.CreateDirectory(paths.Root);
        using var updater = h.NewUpdater(version: "0.6.10");
        var events = Capture(updater);

        WriteResult(h, new UpdateResult("0.6.12", "0.6.10", UpdateStages.RolledBack, "A versão nova não confirmou em 2 minutos.", h.Player.Time.GetUtcNow()));
        updater.CheckResult();
        var rolledBack = Assert.Single(events);
        Assert.Equal((AgentEvents.UpdateRolledBack, "A versão nova não confirmou em 2 minutos."), (rolledBack.Event, (string?)rolledBack.Data!["error"]));

        events.Clear();
        WritePending(h, new PendingUpdate("0.6.13", "0.6.10", paths.PackagePath("0.6.13"), new string('a', 64), h.ProgramDirectory, "MarumbiSom.Agent", h.Player.Time.GetUtcNow()));
        updater.CheckResult();
        Assert.Empty(events);
        h.Player.Time.Advance(AgentUpdater.PendingGiveUpAfter + TimeSpan.FromMinutes(1));
        updater.CheckResult();
        var abandoned = Assert.Single(events);
        Assert.Equal(AgentEvents.UpdateFailed, abandoned.Event);
        Assert.Contains("continua na versão 0.6.10", (string?)abandoned.Data!["error"], StringComparison.Ordinal);
        Assert.False(File.Exists(paths.Pending));
    }

    private static void InstallKey(SyncHarness h, string publicKey)
    {
        Directory.CreateDirectory(h.ProgramDirectory);
        File.WriteAllText(Path.Combine(h.ProgramDirectory, ReleaseSigning.PublicKeyFileName), $"# chave pública de teste\n{publicKey}\n");
    }

    private static async Task<AgentReleaseInfo> PublishAsync(SyncHarness h, ReleasePackages.ReleaseFiles package)
    {
        var response = await ReleasePackages.UploadAsync(h.Admin, package);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = h.Link.Current!.Token;
        using var agent = h.Hub.AgentClient(token);
        return (await agent.GetFromJsonAsync<AgentReleaseInfo>("/agent/releases/current", ContractJson.Options))!;
    }

    private static List<AgentEventMessage> Capture(AgentUpdater updater)
    {
        var events = new List<AgentEventMessage>();
        updater.Reported += (_, message) =>
        {
            lock (events)
            {
                events.Add(message);
            }
        };
        return events;
    }

    private static void WritePending(SyncHarness h, PendingUpdate pending) =>
        File.WriteAllText(h.Player.Paths.Updates.Pending, JsonSerializer.Serialize(pending, ContractJson.Options));

    private static void WriteResult(SyncHarness h, UpdateResult result) =>
        File.WriteAllText(h.Player.Paths.Updates.Result, JsonSerializer.Serialize(result, ContractJson.Options));

    private sealed class NoLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
