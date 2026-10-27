using MarumbiSom.Agent.Audio;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests;

public class AudioEndpointManagerTests
{
    private readonly SimulatedWindowsEnvironment _windows = new(BluetoothAddress.Parse("AA:BB:CC:DD:EE:FF"));
    private readonly FakeConnection _connection = new();

    [Fact]
    public void Volume_do_endpoint_da_caixa_e_fixado_em_100_porcento()
    {
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        using var manager = Create();

        manager.Enforce();

        Assert.Equal(1.0f, _windows.SpeakerEndpointVolume);
    }

    [Fact]
    public void Saida_padrao_volta_para_o_alto_falante_do_pc_quando_o_windows_troca_para_a_caixa()
    {
        using var manager = Create();
        manager.Enforce(); // memoriza o alto-falante do PC como padrão

        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    [Fact]
    public void Sem_padrao_anterior_conhecido_escolhe_outra_saida_ativa()
    {
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        using var manager = Create();

        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    [Fact]
    public void Falha_do_IPolicyConfig_e_registrada_e_a_funcao_desativada_sem_derrubar_o_agente()
    {
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        _windows.PolicyConfigWorks = false;
        using var manager = Create();

        manager.Enforce();
        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.SpeakerEndpointId, _windows.DefaultEndpointId);
        Assert.Equal(1.0f, _windows.SpeakerEndpointVolume);
    }

    [Fact]
    public void Nunca_restaura_para_o_viva_voz_da_propria_caixa()
    {
        // O "Viva-voz" da caixa é outro endpoint no mesmo contêiner. Se ele virou padrão antes, não serve de destino.
        const string handsFree = "{0.0.0.00000000}.{sim-bt-handsfree}";
        _windows.ExtraRenderEndpoints.Add(new AudioEndpoint(handsFree, "Viva-voz (Caixa Simulada)", _windows.SpeakerContainerId, AudioEndpointState.Active));
        _windows.DefaultEndpointId = handsFree;
        using var manager = Create();
        manager.Enforce(); // caixa ainda não conectada: o viva-voz parece uma saída qualquer

        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    [Fact]
    public void Viva_voz_da_caixa_como_padrao_tambem_e_restaurado()
    {
        const string handsFree = "{0.0.0.00000000}.{sim-bt-handsfree}";
        _windows.ExtraRenderEndpoints.Add(new AudioEndpoint(handsFree, "Viva-voz (Caixa Simulada)", _windows.SpeakerContainerId, AudioEndpointState.Active));
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = handsFree;
        using var manager = Create();

        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    [Fact]
    public void Padrao_anterior_desconectado_nao_e_usado()
    {
        const string hdmi = "{0.0.0.00000000}.{sim-hdmi}";
        _windows.ExtraRenderEndpoints.Add(new AudioEndpoint(hdmi, "Monitor (HDMI)", Guid.NewGuid(), AudioEndpointState.Active));
        _windows.DefaultEndpointId = hdmi;
        using var manager = Create();
        manager.Enforce(); // memoriza o monitor

        _windows.ExtraRenderEndpoints[0] = _windows.ExtraRenderEndpoints[0] with { State = AudioEndpointState.Unplugged };
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        manager.Enforce();

        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    [Fact]
    public void Para_de_brigar_quando_o_windows_volta_para_a_caixa_tres_vezes_seguidas()
    {
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        using var manager = Create();
        var restores = 0;
        for (var pass = 0; pass < 6; pass++)
        {
            _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId; // o Windows troca de novo
            manager.Enforce();
            if (_windows.DefaultEndpointId == SimulatedWindowsEnvironment.PcSpeakersId)
            {
                restores++;
            }
        }

        Assert.Equal(3, restores);

        // A caixa reconectou: volta a tentar.
        _connection.Connect(SimulatedWindowsEnvironment.SpeakerEndpointId);
        _windows.DefaultEndpointId = SimulatedWindowsEnvironment.SpeakerEndpointId;
        manager.Enforce();
        Assert.Equal(SimulatedWindowsEnvironment.PcSpeakersId, _windows.DefaultEndpointId);
    }

    private AudioEndpointManager Create() => new(_windows, _connection, TimeProvider.System, NullLogger<AudioEndpointManager>.Instance);

    private sealed class FakeConnection : IBluetoothConnection
    {
        public event EventHandler<BluetoothConnectionChangedEventArgs>? ConnectionChanged;

        public bool IsConnected { get; private set; }

        public string? SpeakerEndpointId { get; private set; }

        public BluetoothStatus Status => IsConnected ? BluetoothStatus.Connected : BluetoothStatus.Disconnected;

        public void Connect(string endpointId)
        {
            IsConnected = true;
            SpeakerEndpointId = endpointId;
            ConnectionChanged?.Invoke(this, new BluetoothConnectionChangedEventArgs(true, endpointId));
        }

        public void RequestReconnect()
        {
        }

        public void RequestAudioRestart()
        {
        }
    }
}
