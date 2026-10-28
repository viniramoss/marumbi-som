namespace MarumbiSom.Agent.Windows.Abstractions;

/// <summary>Adaptador Bluetooth USB (nível 3 da seção 9.2).</summary>
public interface IBluetoothAdapterControl
{
    /// <summary>Desativa e reativa o adaptador via PnP. Retorna <c>true</c> se os dois passos tiveram sucesso.</summary>
    Task<bool> ResetAdapterAsync(CancellationToken cancellationToken);
}
