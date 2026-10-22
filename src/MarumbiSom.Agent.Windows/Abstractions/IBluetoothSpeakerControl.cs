namespace MarumbiSom.Agent.Windows.Abstractions;

public sealed record PairedBluetoothDevice(BluetoothAddress Address, string Name, Guid? ContainerId, bool IsConnected);

/// <summary>Operações sobre a caixa Bluetooth pareada (seções 9.3 e 9.4).</summary>
public interface IBluetoothSpeakerControl
{
    Task<IReadOnlyList<PairedBluetoothDevice>> ListPairedDevicesAsync(CancellationToken cancellationToken);

    /// <summary>ContainerId do dispositivo Bluetooth com o endereço indicado, usado para achar o endpoint de áudio.</summary>
    Task<Guid?> GetContainerIdAsync(BluetoothAddress address, CancellationToken cancellationToken);

    /// <summary>Método A: <c>KSPROPERTY_ONESHOT_RECONNECT</c> no filtro KS do endpoint Bluetooth.</summary>
    Task<bool> RequestReconnectAsync(string endpointId, CancellationToken cancellationToken);

    /// <summary>Método B: desliga e religa o serviço A2DP Sink da caixa pareada.</summary>
    Task<bool> ToggleA2dpSinkAsync(BluetoothAddress address, CancellationToken cancellationToken);
}
