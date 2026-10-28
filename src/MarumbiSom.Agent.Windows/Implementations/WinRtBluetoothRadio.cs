using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Devices.Radios;

namespace MarumbiSom.Agent.Windows.Implementations;

/// <summary>Rádio Bluetooth via <c>Windows.Devices.Radios</c> (nível 1 da seção 9.2).</summary>
public sealed class WinRtBluetoothRadio(ILogger<WinRtBluetoothRadio> logger) : IBluetoothRadio
{
    public async Task<RadioPowerState> GetStateAsync(CancellationToken cancellationToken)
    {
        var radio = await FindAsync(cancellationToken);
        return radio?.State switch
        {
            null => RadioPowerState.Unavailable,
            RadioState.On => RadioPowerState.On,
            RadioState.Disabled => RadioPowerState.Unavailable,
            _ => RadioPowerState.Off,
        };
    }

    public async Task<bool> TurnOnAsync(CancellationToken cancellationToken)
    {
        var access = await Radio.RequestAccessAsync().AsTask(cancellationToken);
        if (access != RadioAccessStatus.Allowed)
        {
            logger.LogWarning("Acesso ao rádio negado pelo Windows: {Access}", access);
            return false;
        }

        var radio = await FindAsync(cancellationToken);
        if (radio is null)
        {
            return false;
        }

        var result = await radio.SetStateAsync(RadioState.On).AsTask(cancellationToken);
        if (result != RadioAccessStatus.Allowed)
        {
            logger.LogWarning("Windows recusou ligar o rádio Bluetooth: {Result}", result);
        }

        return result == RadioAccessStatus.Allowed && radio.State == RadioState.On;
    }

    private static async Task<Radio?> FindAsync(CancellationToken cancellationToken)
    {
        var radios = await Radio.GetRadiosAsync().AsTask(cancellationToken);
        return radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
    }
}
