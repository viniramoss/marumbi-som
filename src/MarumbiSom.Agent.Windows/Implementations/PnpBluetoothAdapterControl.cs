using System.Diagnostics;
using System.Management;
using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.Extensions.Logging;

namespace MarumbiSom.Agent.Windows.Implementations;

/// <summary>
/// Desativa e reativa o adaptador Bluetooth USB com <c>pnputil</c> (nível 3 da seção 9.2).
/// O adaptador é o dispositivo da classe Bluetooth cujo InstanceId começa com <c>USB\</c>,
/// ou o indicado em <paramref name="configuredInstanceId"/>.
/// </summary>
public sealed class PnpBluetoothAdapterControl(ILogger<PnpBluetoothAdapterControl> logger, string? configuredInstanceId) : IBluetoothAdapterControl
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    public async Task<bool> ResetAdapterAsync(CancellationToken cancellationToken)
    {
        var instanceId = string.IsNullOrWhiteSpace(configuredInstanceId) ? FindAdapterInstanceId() : configuredInstanceId;
        if (instanceId is null)
        {
            logger.LogError("Adaptador Bluetooth USB não encontrado; configure Bluetooth:AdapterInstanceId");
            return false;
        }

        logger.LogWarning("Reiniciando o adaptador Bluetooth {InstanceId} via PnP", instanceId);
        var disabled = await RunPnputilAsync(["/disable-device", instanceId], cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        var enabled = await RunPnputilAsync(["/enable-device", instanceId], cancellationToken);
        return disabled && enabled;
    }

    private string? FindAdapterInstanceId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth' AND DeviceID LIKE 'USB\\\\%'");
            using var results = searcher.Get();
            return results.Cast<ManagementBaseObject>().Select(o => o["DeviceID"] as string).FirstOrDefault(id => id is not null);
        }
        catch (ManagementException ex)
        {
            logger.LogError(ex, "Falha ao consultar o WMI para localizar o adaptador Bluetooth");
            return null;
        }
    }

    private async Task<bool> RunPnputilAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("pnputil não iniciou.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var text = await output;
        if (process.ExitCode != 0)
        {
            logger.LogWarning("pnputil {Arguments} terminou com código {ExitCode}: {Output}", string.Join(' ', arguments), process.ExitCode, text.Trim());
            return false;
        }

        return true;
    }
}
