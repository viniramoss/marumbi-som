using MarumbiSom.Agent.Windows.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent;

/// <summary><c>MarumbiSom.Agent.exe --list-devices</c>: mostra caixas pareadas e saídas de áudio, para preencher a configuração.</summary>
internal static class DeviceLister
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("Dispositivos Bluetooth pareados:");
        var speakers = new WindowsBluetoothSpeakerControl(NullLogger<WindowsBluetoothSpeakerControl>.Instance);
        foreach (var device in await speakers.ListPairedDevicesAsync(CancellationToken.None))
        {
            Console.WriteLine($"  {device.Address}  {device.Name}  conectado={device.IsConnected}  container={device.ContainerId}");
        }

        Console.WriteLine();
        Console.WriteLine("Saídas de áudio:");
        var endpoints = new CoreAudioEndpoints(NullLogger<CoreAudioEndpoints>.Instance);
        var defaultId = endpoints.GetDefaultRenderEndpointId();
        foreach (var endpoint in endpoints.ListRenderEndpoints())
        {
            var mark = endpoint.Id == defaultId ? " (padrão)" : string.Empty;
            Console.WriteLine($"  [{endpoint.State}] {endpoint.FriendlyName}{mark}");
            Console.WriteLine($"      id={endpoint.Id}  container={endpoint.ContainerId}");
        }

        return 0;
    }
}
