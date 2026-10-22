using System.ComponentModel;
using System.Runtime.InteropServices;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Agent.Windows.Interop;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace MarumbiSom.Agent.Windows.Implementations;

/// <summary>Caixa Bluetooth: listagem de pareados (WinRT), reconexão KS (método A) e serviço A2DP (método B).</summary>
public sealed class WindowsBluetoothSpeakerControl(ILogger<WindowsBluetoothSpeakerControl> logger) : IBluetoothSpeakerControl
{
    private const string ContainerIdProperty = "System.Devices.Aep.ContainerId";
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";

    public async Task<IReadOnlyList<PairedBluetoothDevice>> ListPairedDevicesAsync(CancellationToken cancellationToken)
    {
        var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var infos = await DeviceInformation.FindAllAsync(selector, [ContainerIdProperty, IsConnectedProperty], DeviceInformationKind.AssociationEndpoint)
            .AsTask(cancellationToken);
        var result = new List<PairedBluetoothDevice>(infos.Count);
        foreach (var info in infos)
        {
            try
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id).AsTask(cancellationToken);
                if (device is null)
                {
                    continue;
                }

                result.Add(new PairedBluetoothDevice(
                    new BluetoothAddress(device.BluetoothAddress),
                    device.Name,
                    info.Properties.TryGetValue(ContainerIdProperty, out var c) ? c as Guid? : null,
                    device.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Falha ao ler o dispositivo Bluetooth pareado {DeviceId}", info.Id);
            }
        }

        return result;
    }

    public async Task<Guid?> GetContainerIdAsync(BluetoothAddress address, CancellationToken cancellationToken)
    {
        var devices = await ListPairedDevicesAsync(cancellationToken);
        return devices.FirstOrDefault(d => d.Address == address)?.ContainerId;
    }

    public Task<bool> RequestReconnectAsync(string endpointId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        var sent = false;
        CoreAudioEndpoints.WithDevice(endpointId, device =>
        {
            var topologyId = InterfaceIds.IDeviceTopology;
            Marshal.ThrowExceptionForHR(device.Activate(ref topologyId, ClsCtx.All, IntPtr.Zero, out var topologyObj));
            var topology = (IDeviceTopology)topologyObj;
            try
            {
                Marshal.ThrowExceptionForHR(topology.GetConnectorCount(out var count));
                for (uint i = 0; i < count && !sent; i++)
                {
                    sent = TrySendReconnect(topology, i);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(topology);
            }
        });

        if (!sent)
        {
            logger.LogWarning("Nenhum filtro KS do endpoint {EndpointId} aceitou KSPROPERTY_ONESHOT_RECONNECT", endpointId);
        }

        return Task.FromResult(sent);
    }

    public async Task<bool> ToggleA2dpSinkAsync(BluetoothAddress address, CancellationToken cancellationToken)
    {
        var findParams = new BluetoothFindRadioParams { Size = (uint)Marshal.SizeOf<BluetoothFindRadioParams>() };
        var find = BluetoothApis.BluetoothFindFirstRadio(ref findParams, out var radio);
        if (find == IntPtr.Zero)
        {
            logger.LogWarning("Nenhum rádio Bluetooth encontrado para o método B (erro {Error})", Marshal.GetLastPInvokeError());
            return false;
        }

        try
        {
            var info = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>(), Address = address.Value, Name = string.Empty };
            var error = BluetoothApis.BluetoothGetDeviceInfo(radio, ref info);
            if (error != 0)
            {
                logger.LogWarning("BluetoothGetDeviceInfo falhou para {Address}: {Error}", address, new Win32Exception((int)error).Message);
                return false;
            }

            var service = BluetoothApis.AudioSinkServiceClass;
            error = BluetoothApis.BluetoothSetServiceState(radio, ref info, ref service, BluetoothApis.BLUETOOTH_SERVICE_DISABLE);
            if (error != 0)
            {
                logger.LogWarning("Desligar o serviço A2DP de {Address} falhou: {Error}", address, new Win32Exception((int)error).Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            error = BluetoothApis.BluetoothSetServiceState(radio, ref info, ref service, BluetoothApis.BLUETOOTH_SERVICE_ENABLE);
            if (error != 0)
            {
                logger.LogWarning("Religar o serviço A2DP de {Address} falhou: {Error}", address, new Win32Exception((int)error).Message);
                return false;
            }

            return true;
        }
        finally
        {
            BluetoothApis.CloseHandle(radio);
            BluetoothApis.BluetoothFindRadioClose(find);
        }
    }

    private bool TrySendReconnect(IDeviceTopology topology, uint connectorIndex)
    {
        if (topology.GetConnector(connectorIndex, out var connector) != 0)
        {
            return false;
        }

        try
        {
            if (connector.GetConnectedTo(out var other) != 0)
            {
                return false;
            }

            try
            {
                if (other is not IPart part)
                {
                    return false;
                }

                var ksId = InterfaceIds.IKsControl;
                if (part.Activate(ClsCtx.InprocServer, ref ksId, out var ksObj) != 0)
                {
                    return false;
                }

                var ks = (IKsControl)ksObj;
                try
                {
                    var property = new KsProperty
                    {
                        Set = KsConstants.KSPROPSETID_BtAudio,
                        Id = KsConstants.KSPROPERTY_ONESHOT_RECONNECT,
                        Flags = KsConstants.KSPROPERTY_TYPE_GET,
                    };
                    var hr = ks.KsProperty(ref property, (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _);
                    if (hr != 0)
                    {
                        logger.LogDebug("KsProperty(ONESHOT_RECONNECT) retornou 0x{Hr:X8}", hr);
                    }

                    return hr == 0;
                }
                finally
                {
                    Marshal.ReleaseComObject(ks);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(other);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(connector);
        }
    }
}
