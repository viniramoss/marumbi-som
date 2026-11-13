using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests;

public class BluetoothGuardianTests
{
    private static readonly BluetoothAddress Speaker = BluetoothAddress.Parse("AA:BB:CC:DD:EE:FF");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(-3)));
    private readonly SimulatedWindowsEnvironment _windows = new(Speaker);
    private readonly BluetoothGuardian _guardian;
    private readonly List<string> _actions = [];
    private readonly List<bool> _changes = [];

    public BluetoothGuardianTests()
    {
        var options = Options.Create(new BluetoothOptions { SpeakerAddress = Speaker.ToString() });
        _guardian = new BluetoothGuardian(_windows, _windows, _windows, _windows, _windows, options, _time, NullLogger<BluetoothGuardian>.Instance);
        _guardian.RecoveryAction += (_, a) => _actions.Add(a);
        _guardian.ConnectionChanged += (_, e) => _changes.Add(e.IsConnected);
    }

    [Fact]
    public async Task Com_a_caixa_ativa_conecta_sem_nenhuma_acao_de_recuperacao()
    {
        await RunUntilAsync(GuardianState.Connected);

        Assert.True(_guardian.IsConnected);
        Assert.Equal(SimulatedWindowsEnvironment.SpeakerEndpointId, _guardian.SpeakerEndpointId);
        Assert.Empty(_actions);
        Assert.Equal([true], _changes);
    }

    [Fact]
    public async Task Servico_parado_e_iniciado_antes_de_verificar_o_radio()
    {
        _windows.StopService(WindowsServiceNames.Audio);

        await RunUntilAsync(GuardianState.Connected);

        Assert.True(_windows.IsRunning(WindowsServiceNames.Audio));
    }

    [Fact]
    public async Task Radio_desligado_por_software_e_religado_no_nivel_1()
    {
        _windows.RadioOn = false;

        var elapsed = await RunUntilAsync(GuardianState.Connected);

        Assert.Equal(["radio.level1"], _actions);
        Assert.True(_windows.RadioOn);
        Assert.True(elapsed <= TimeSpan.FromSeconds(15), $"meta da seção 9.6: ≤ 15 s; levou {elapsed}");
    }

    [Fact]
    public async Task Radio_que_nao_liga_por_software_escala_para_reiniciar_o_bthserv()
    {
        _windows.RadioOn = false;
        _windows.RadioSoftwareControllable = false;
        _windows.SpeakerLinked = true;

        await RunUntilAsync(GuardianState.Connected);

        Assert.Equal(["radio.level1", "radio.level2", "radio.level1"], _actions);
        Assert.Equal(1, _windows.ServiceRestarts);
    }

    [Fact]
    public async Task Adaptador_desativado_e_reativado_no_nivel_3()
    {
        _windows.AdapterEnabled = false;

        await RunUntilAsync(GuardianState.Connected);

        Assert.Contains("adapter.reset", _actions);
        Assert.Equal(1, _windows.AdapterResets);
        Assert.True(_windows.AdapterEnabled);
    }

    [Fact]
    public async Task Caixa_desconectada_reconecta_pelo_metodo_A()
    {
        await RunUntilAsync(GuardianState.Connected);
        _windows.SpeakerLinked = false;

        var elapsed = await RunUntilAsync(GuardianState.Connected, afterLeaving: true);

        Assert.Equal(["connect.A"], _actions);
        Assert.Equal([true, false, true], _changes);
        Assert.True(elapsed <= TimeSpan.FromSeconds(30), $"meta da seção 9.6: ≤ 30 s; levou {elapsed}");
    }

    [Fact]
    public async Task Metodos_escalam_A_B_C_com_backoff_crescente()
    {
        _windows.SpeakerLinked = false;
        _windows.WorkingMethods.Clear();
        _windows.WorkingMethods.Add("C");

        await RunUntilAsync(GuardianState.Connected);

        Assert.Equal(["connect.A", "connect.B", "adapter.reset"], _actions);
    }

    [Fact]
    public async Task Reinicio_do_adaptador_respeita_o_intervalo_minimo_de_10_minutos()
    {
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;

        // Método C a cada 3 tentativas (~3 min com backoff de 60 s), mas o adaptador só reinicia 1 vez a cada 10 min.
        await RunForAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(1, _windows.AdapterResets);

        await RunForAsync(TimeSpan.FromMinutes(6));
        Assert.Equal(2, _windows.AdapterResets);
    }

    [Fact]
    public async Task Backoff_chega_a_60_segundos_e_para_de_crescer()
    {
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;
        var attempts = new List<DateTimeOffset>();
        _guardian.RecoveryAction += (_, a) =>
        {
            if (a.StartsWith("connect.", StringComparison.Ordinal) || a == "adapter.reset")
            {
                attempts.Add(_time.GetUtcNow());
            }
        };

        await RunForAsync(TimeSpan.FromMinutes(8));

        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Count >= 6);
        Assert.InRange(gaps[0].TotalSeconds, 5, 15);
        Assert.All(gaps.Skip(5), g => Assert.InRange(g.TotalSeconds, 60, 75));
    }

    [Fact]
    public async Task Caixa_religada_durante_a_espera_e_detectada_sem_esperar_o_backoff()
    {
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;
        await RunForAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(GuardianState.Waiting, _guardian.State);

        // A caixa religa e se reconecta sozinha ao PC.
        _windows.SpeakerPoweredOn = true;
        _windows.SpeakerLinked = true;
        var elapsed = await RunUntilAsync(GuardianState.Connected);

        Assert.True(elapsed <= TimeSpan.FromSeconds(3), $"levou {elapsed}");
    }

    [Fact]
    public async Task Comando_reconectar_reinicia_o_escalonamento()
    {
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;
        await RunForAsync(TimeSpan.FromMinutes(5));
        Assert.True(_guardian.FailedAttempts > 3);

        _windows.SpeakerPoweredOn = true;
        _actions.Clear();
        _guardian.RequestReconnect();
        await RunUntilAsync(GuardianState.Connected);

        Assert.Equal("connect.A", _actions[0]);
    }

    [Fact]
    public async Task Reiniciar_audio_desconecta_reinicia_servicos_e_adaptador_e_reconecta()
    {
        await RunUntilAsync(GuardianState.Connected);
        _actions.Clear();

        _guardian.RequestAudioRestart();
        var grace = await _guardian.StepAsync(CancellationToken.None);

        // Antes de mexer no Windows, o player tem tempo de pausar e fechar os mpv (D37 b).
        Assert.Equal(GuardianState.RestartingAudio, _guardian.State);
        Assert.Equal(BluetoothStatus.RestartingAudio, _guardian.Status);
        Assert.False(_guardian.IsConnected);
        Assert.Equal(BluetoothGuardian.AudioRestartGrace, grace);
        Assert.Empty(_windows.RestartedServices);
        Assert.Equal([true, false], _changes);

        // "Reconectar caixa" no meio não cancela o reinício.
        _guardian.RequestReconnect();
        _time.Advance(grace);
        await _guardian.StepAsync(CancellationToken.None);
        // Entre o reinício e a caixa voltar, o painel continua vendo "reiniciando o áudio" (D37 d), sem piscar vermelho.
        Assert.Equal(GuardianState.Initializing, _guardian.State);
        Assert.Equal(BluetoothStatus.RestartingAudio, _guardian.Status);
        var elapsed = await RunUntilAsync(GuardianState.Connected);
        Assert.Equal(BluetoothStatus.Connected, _guardian.Status);

        Assert.Equal([WindowsServiceNames.AudioEndpointBuilder, WindowsServiceNames.Bluetooth], _windows.RestartedServices);
        Assert.Equal(1, _windows.AdapterResets);
        Assert.Equal(["audio.restart", "adapter.reset"], _actions);
        Assert.Equal([true, false, true], _changes);
        Assert.True(elapsed <= TimeSpan.FromSeconds(30), $"levou {elapsed}");
    }

    [Fact]
    public async Task Reiniciar_audio_ignora_o_intervalo_do_adaptador_mas_conta_para_o_automatico()
    {
        _windows.AdapterEnabled = false;
        await RunUntilAsync(GuardianState.Connected);
        Assert.Equal(1, _windows.AdapterResets);

        _guardian.RequestAudioRestart();
        await RunUntilAsync(GuardianState.Connected, afterLeaving: true);
        Assert.Equal(2, _windows.AdapterResets);

        // Logo depois, o método C automático não reinicia o adaptador de novo antes de 10 minutos.
        _windows.SpeakerLinked = false;
        _windows.WorkingMethods.Clear();
        await RunForAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(2, _windows.AdapterResets);
    }

    [Fact]
    public async Task Reiniciar_audio_com_servico_que_falha_segue_as_outras_etapas_e_reconecta()
    {
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;
        await RunForAsync(TimeSpan.FromMinutes(1));
        _windows.SpeakerPoweredOn = true;
        _windows.FailingRestarts.Add(WindowsServiceNames.AudioEndpointBuilder);
        var resetsBefore = _windows.AdapterResets;
        _windows.RestartedServices.Clear();

        // Pedido com a caixa fora (estado Aguardando): o reinício acontece do mesmo jeito.
        _guardian.RequestAudioRestart();
        await RunUntilAsync(GuardianState.Connected);

        Assert.Equal([WindowsServiceNames.AudioEndpointBuilder, WindowsServiceNames.Bluetooth], _windows.RestartedServices);
        Assert.Equal(resetsBefore + 1, _windows.AdapterResets);
    }

    [Fact]
    public async Task Reiniciar_audio_sem_a_caixa_voltar_deixa_de_mostrar_reiniciando_depois_de_90_s()
    {
        await RunUntilAsync(GuardianState.Connected);
        _windows.SpeakerPoweredOn = false;
        _windows.SpeakerLinked = false;

        _guardian.RequestAudioRestart();
        await RunForAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(BluetoothStatus.RestartingAudio, _guardian.Status);

        await RunForAsync(TimeSpan.FromSeconds(40));
        Assert.NotEqual(BluetoothStatus.RestartingAudio, _guardian.Status);
        Assert.False(_guardian.IsConnected);
    }

    [Fact]
    public async Task Sem_endereco_configurado_nao_conecta_e_nao_lanca_excecao()
    {
        var guardian = new BluetoothGuardian(_windows, _windows, _windows, _windows, _windows, Options.Create(new BluetoothOptions()), _time, NullLogger<BluetoothGuardian>.Instance);

        for (var i = 0; i < 20; i++)
        {
            _time.Advance(await guardian.StepAsync(CancellationToken.None));
        }

        Assert.False(guardian.IsConnected);
    }

    private async Task<TimeSpan> RunUntilAsync(GuardianState target, bool afterLeaving = false, int maxSteps = 500)
    {
        var start = _time.GetUtcNow();
        var left = !afterLeaving;
        for (var i = 0; i < maxSteps; i++)
        {
            var delay = await _guardian.StepAsync(CancellationToken.None);
            left |= _guardian.State != target;
            if (left && _guardian.State == target)
            {
                return _time.GetUtcNow() - start;
            }

            _time.Advance(delay);
        }

        throw new Xunit.Sdk.XunitException($"Estado {target} não alcançado; atual {_guardian.State}");
    }

    private async Task RunForAsync(TimeSpan duration)
    {
        var end = _time.GetUtcNow() + duration;
        while (_time.GetUtcNow() < end)
        {
            var delay = await _guardian.StepAsync(CancellationToken.None);
            _time.Advance(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1));
        }
    }
}
