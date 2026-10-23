using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Simulation;

/// <summary>
/// Windows falso para o modo <c>--simulate</c> e para os testes: rádio, serviços, adaptador,
/// caixa e endpoints de áudio em memória, com contadores de cada ação de recuperação.
/// </summary>
public sealed class SimulatedWindowsEnvironment : IBluetoothRadio, ISystemServices, IBluetoothAdapterControl, IBluetoothSpeakerControl, IAudioEndpoints
{
    public const string PcSpeakersId = "{0.0.0.00000000}.{sim-pc-speakers}";
    public const string SpeakerEndpointId = "{0.0.0.00000000}.{sim-bt-speaker}";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _stoppedServices = new(StringComparer.OrdinalIgnoreCase);

    public SimulatedWindowsEnvironment(BluetoothAddress speakerAddress)
    {
        SpeakerAddress = speakerAddress;
        DefaultEndpointId = PcSpeakersId;
    }

    public BluetoothAddress SpeakerAddress { get; }

    public Guid SpeakerContainerId { get; } = Guid.Parse("6f1d3a52-0000-4000-8000-00000000b10e");

    /// <summary>Rádio ligado por software.</summary>
    public bool RadioOn { get; set; } = true;

    /// <summary>Se <c>false</c>, <see cref="TurnOnAsync"/> não consegue ligar o rádio (simula falha do nível 1).</summary>
    public bool RadioSoftwareControllable { get; set; } = true;

    /// <summary>Adaptador habilitado no Gerenciador de Dispositivos.</summary>
    public bool AdapterEnabled { get; set; } = true;

    /// <summary>Caixa fisicamente ligada.</summary>
    public bool SpeakerPoweredOn { get; set; } = true;

    /// <summary>Link A2DP estabelecido entre o PC e a caixa.</summary>
    public bool SpeakerLinked { get; set; } = true;

    /// <summary>Métodos de reconexão que funcionam quando a caixa está ligada.</summary>
    public HashSet<string> WorkingMethods { get; } = ["A", "B", "C"];

    public string? DefaultEndpointId { get; set; }

    public float SpeakerEndpointVolume { get; private set; } = 0.5f;

    public bool PolicyConfigWorks { get; set; } = true;

    public int TurnOnCalls { get; private set; }

    public int ServiceRestarts { get; private set; }

    public int AdapterResets { get; private set; }

    public int KsReconnects { get; private set; }

    public int A2dpToggles { get; private set; }

    public bool SpeakerActive => RadioOn && AdapterEnabled && SpeakerPoweredOn && SpeakerLinked && IsRunning(WindowsServiceNames.Bluetooth);

    public void StopService(string name)
    {
        lock (_lock)
        {
            _stoppedServices.Add(name);
        }
    }

    public Task<RadioPowerState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (!AdapterEnabled || !IsRunning(WindowsServiceNames.Bluetooth))
        {
            return Task.FromResult(RadioPowerState.Unavailable);
        }

        return Task.FromResult(RadioOn ? RadioPowerState.On : RadioPowerState.Off);
    }

    public Task<bool> TurnOnAsync(CancellationToken cancellationToken)
    {
        TurnOnCalls++;
        if (RadioSoftwareControllable && AdapterEnabled && IsRunning(WindowsServiceNames.Bluetooth))
        {
            RadioOn = true;
        }

        return Task.FromResult(RadioOn);
    }

    public bool IsRunning(string serviceName)
    {
        lock (_lock)
        {
            return !_stoppedServices.Contains(serviceName);
        }
    }

    public Task StartAsync(string serviceName, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _stoppedServices.Remove(serviceName);
        }

        return Task.CompletedTask;
    }

    public Task RestartAsync(string serviceName, CancellationToken cancellationToken)
    {
        ServiceRestarts++;
        lock (_lock)
        {
            _stoppedServices.Remove(serviceName);
        }

        // Reiniciar o bthserv religa o rádio quando ele estava "travado".
        RadioSoftwareControllable = true;
        return Task.CompletedTask;
    }

    public Task<bool> ResetAdapterAsync(CancellationToken cancellationToken)
    {
        AdapterResets++;
        AdapterEnabled = true;
        RadioOn = true;
        RadioSoftwareControllable = true;
        LinkIfWorks("C");
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PairedBluetoothDevice>> ListPairedDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PairedBluetoothDevice>>([new PairedBluetoothDevice(SpeakerAddress, "Caixa Simulada", SpeakerContainerId, SpeakerActive)]);

    public Task<Guid?> GetContainerIdAsync(BluetoothAddress address, CancellationToken cancellationToken) =>
        Task.FromResult<Guid?>(address == SpeakerAddress ? SpeakerContainerId : null);

    public Task<bool> RequestReconnectAsync(string endpointId, CancellationToken cancellationToken)
    {
        KsReconnects++;
        return Task.FromResult(LinkIfWorks("A"));
    }

    public Task<bool> ToggleA2dpSinkAsync(BluetoothAddress address, CancellationToken cancellationToken)
    {
        A2dpToggles++;
        return Task.FromResult(LinkIfWorks("B"));
    }

    /// <summary>Saídas além do alto-falante do PC e da caixa (ex.: o "Viva-voz" da caixa, um monitor HDMI).</summary>
    public List<AudioEndpoint> ExtraRenderEndpoints { get; } = [];

    public IReadOnlyList<AudioEndpoint> ListRenderEndpoints() =>
    [
        new(PcSpeakersId, "Alto-falantes (Realtek)", Guid.Parse("00000000-0000-0000-ffff-ffffffffffff"), AudioEndpointState.Active),
        new(SpeakerEndpointId, "Fones de ouvido (Caixa Simulada)", SpeakerContainerId, SpeakerActive ? AudioEndpointState.Active : AudioEndpointState.Unplugged),
        .. ExtraRenderEndpoints,
    ];

    public string? GetDefaultRenderEndpointId() => DefaultEndpointId;

    public void SetDefaultRenderEndpoint(string endpointId)
    {
        if (!PolicyConfigWorks)
        {
#pragma warning disable CA2201 // Simula exatamente a exceção que o Windows lança
            throw new System.Runtime.InteropServices.COMException("IPolicyConfig indisponível (simulado)");
#pragma warning restore CA2201
        }

        DefaultEndpointId = endpointId;
    }

    public void SetEndpointVolume(string endpointId, float scalar)
    {
        if (endpointId == SpeakerEndpointId)
        {
            SpeakerEndpointVolume = scalar;
        }
    }

    private bool LinkIfWorks(string method)
    {
        if (SpeakerPoweredOn && RadioOn && AdapterEnabled && WorkingMethods.Contains(method))
        {
            SpeakerLinked = true;
            DefaultEndpointId = SpeakerEndpointId; // o Windows costuma trocar a saída padrão para a caixa
        }

        return SpeakerLinked;
    }
}
