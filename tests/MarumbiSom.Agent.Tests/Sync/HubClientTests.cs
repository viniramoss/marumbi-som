using System.Diagnostics;
using System.Net.Http.Json;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Contracts.Messages;
using MarumbiSom.Hub.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests.Sync;

public sealed class HubClientTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Loja_vinculada_conecta_aplica_versao_avisada_pelo_hub_e_responde_comandos()
    {
        await using var h = await SyncHarness.StartAsync(agentTime: TimeProvider.System);
        using var client = NewClient(h, new FakeLifetime());
        using var stepping = new CancellationTokenSource();
        var player = Task.Run(async () =>
        {
            while (!stepping.IsCancellationRequested)
            {
                await h.Player.Engine.StepAsync(stepping.Token);
                await Task.Delay(10, stepping.Token);
                // O relógio do player é falso: andando 100 ms por passo, o resumo (snapshot de 500 ms) se atualiza.
                h.Player.Time.Advance(TimeSpan.FromMilliseconds(100));
            }
        });
        await h.Engine.StartAsync(None);
        await client.StartAsync(None);
        try
        {
            await h.LinkAsync();
            var first = await h.HubVersionAsync();
            await SyncHarness.WaitUntilAsync(() => h.Player.Manifests.Active?.Version == first, "aplicar a primeira versão");
            await SyncHarness.WaitUntilAsync(() => client.Connected, "conectar ao Hub");
            await SyncHarness.WaitUntilAsync(
                () => h.Hub.WithDbAsync(db => db.StoreStates.AnyAsync(s => s.StoreId == h.StoreId && s.AppliedVersion == first)),
                "o Hub saber da versão aplicada");
            await SyncHarness.WaitUntilAsync(
                () => h.Hub.WithDbAsync(db => db.StoreStates.AnyAsync(s => s.StoreId == h.StoreId && s.PlaylistKey == "p" + h.PlaylistId && s.QueueJson != null)),
                "o status levar a playlist e a fila (D23 b)");

            var changedAt = Stopwatch.GetTimestamp();
            await h.AddTrackAsync("Faixa 3");
            var second = await h.HubVersionAsync();
            await SyncHarness.WaitUntilAsync(() => h.Player.Manifests.Active!.Version == second, "aplicar a versão avisada pelo WebSocket");
            Assert.True(Stopwatch.GetElapsedTime(changedAt) < SyncEngine.PollInterval / 2, "Aplicou pelo aviso, não pelo polling de 60 s.");

            var bluetooth = await HubHarness.JsonAsync(await h.Admin.PostAsJsonAsync($"/api/stores/{h.StoreId}/commands", new { command = "reconnectBluetooth" }));
            Assert.Equal("ok", bluetooth.GetProperty("result").GetString());
            Assert.Equal(1, h.Player.Bluetooth.ReconnectRequests);

            var audio = await HubHarness.JsonAsync(await h.Admin.PostAsJsonAsync($"/api/stores/{h.StoreId}/commands", new { command = "restartAudio" }));
            Assert.Equal("ok", audio.GetProperty("result").GetString());
            Assert.Equal(1, h.Player.Bluetooth.AudioRestartRequests);

            var volume = await HubHarness.JsonAsync(await h.Admin.PostAsJsonAsync(
                $"/api/stores/{h.StoreId}/commands", new { command = "setVolume", args = new { volume = 40 } }));
            Assert.Equal("ok", volume.GetProperty("result").GetString());
            Assert.Equal(40, h.Player.State.State.VolumeOverride);

            // Sem versão para distribuição o Hub nem manda o comando (a atualização é testada no AgentUpdaterTests).
            var update = await h.Admin.PostAsJsonAsync($"/api/stores/{h.StoreId}/commands", new { command = "updateAgent" });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, update.StatusCode);
        }
        finally
        {
            await client.StopAsync(None);
            await h.Engine.StopAsync(None);
            await stepping.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player);
        }
    }

    [Fact]
    public async Task Hub_fora_do_ar_tenta_de_novo_sozinho_e_desvincular_desconecta()
    {
        await using var h = await SyncHarness.StartAsync(tracks: 1, agentTime: TimeProvider.System);
        await h.LinkAsync();
        h.Transport.Offline = true;
        using var client = NewClient(h, new FakeLifetime());
        await client.StartAsync(None);
        try
        {
            await SyncHarness.WaitUntilAsync(() => client.LastError is not null, "registrar a falha de conexão");
            Assert.False(client.Connected);

            h.Transport.Offline = false;
            await SyncHarness.WaitUntilAsync(() => client.Connected, "reconectar sozinho");
            Assert.Null(client.LastError);

            h.Link.Unlink();
            await SyncHarness.WaitUntilAsync(() => !client.Connected, "desconectar ao desvincular");
        }
        finally
        {
            await client.StopAsync(None);
        }
    }

    [Fact]
    public async Task Reiniciar_pelo_hub_encerra_o_servico_com_codigo_de_falha_para_o_scm_subir_de_novo()
    {
        await using var h = await SyncHarness.StartAsync(agentTime: TimeProvider.System);
        var lifetime = new FakeLifetime();
        int? serviceExitCode = null;
        var restart = new AgentRestart(lifetime, code => serviceExitCode = code);
        using var client = new HubClient(
            h.Link, h.Transport, h.Player.Manifests, h.Engine, h.Player.Engine, h.Player.Bluetooth, h.Player.Events, h.Player.Paths, restart,
            h.NewUpdater(), TimeProvider.System, NullLogger<HubClient>.Instance);
        var exitCode = Environment.ExitCode;
        try
        {
            await client.HandleCommandAsync(new CommandMessage("c1", CommandTypes.RestartAgent), None);

            Assert.True(restart.Requested);
            Assert.Equal(1, lifetime.StopRequests);
            // Sem isto o SCM vê uma parada limpa e a recuperação do serviço não dispara (agente fica parado).
            Assert.Equal(AgentRestart.ExitCode, serviceExitCode);
        }
        finally
        {
            Environment.ExitCode = exitCode;
        }
    }

    [Fact]
    public void Endereco_do_websocket_segue_o_do_hub()
    {
        Assert.Equal("wss://hub.exemplo.ts.net/agent/ws", HubClient.SocketUri(new Uri("https://hub.exemplo.ts.net/")).ToString());
        Assert.Equal("ws://127.0.0.1:5080/agent/ws", HubClient.SocketUri(new Uri("http://127.0.0.1:5080/")).ToString());
    }

    private static HubClient NewClient(SyncHarness h, IHostApplicationLifetime lifetime) => new(
        h.Link, h.Transport, h.Player.Manifests, h.Engine, h.Player.Engine, h.Player.Bluetooth, h.Player.Events, h.Player.Paths,
        new AgentRestart(lifetime), h.NewUpdater(), TimeProvider.System, NullLogger<HubClient>.Instance);

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public int StopRequests { get; private set; }

        public void StopApplication() => StopRequests++;
    }
}
