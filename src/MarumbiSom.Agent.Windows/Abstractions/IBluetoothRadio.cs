namespace MarumbiSom.Agent.Windows.Abstractions;

public enum RadioPowerState
{
    On,
    Off,

    /// <summary>Nenhum rádio Bluetooth visível (serviço parado, adaptador desativado ou removido).</summary>
    Unavailable,
}

/// <summary>Rádio Bluetooth local (nível 1 da seção 9.2).</summary>
public interface IBluetoothRadio
{
    Task<RadioPowerState> GetStateAsync(CancellationToken cancellationToken);

    /// <summary>Liga o rádio por software. Retorna <c>true</c> se o rádio ficou ligado.</summary>
    Task<bool> TurnOnAsync(CancellationToken cancellationToken);
}
