using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Bluetooth;

public sealed class BluetoothConnectionChangedEventArgs(bool isConnected, string? endpointId) : EventArgs
{
    public bool IsConnected { get; } = isConnected;

    public string? EndpointId { get; } = endpointId;
}

/// <summary>Estado da conexão com a caixa, consumido pelo player e pelo AudioEndpointManager.</summary>
public interface IBluetoothConnection
{
    bool IsConnected { get; }

    /// <summary>Id do endpoint de áudio da caixa; preenchido quando conectado.</summary>
    string? SpeakerEndpointId { get; }

    BluetoothStatus Status { get; }

    event EventHandler<BluetoothConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>Comando "reconectar Bluetooth": reinicia o escalonamento sem esperar o backoff.</summary>
    void RequestReconnect();

    /// <summary>Comando "reiniciar áudio" (D37): para a música, reinicia o áudio e o Bluetooth do Windows e reconecta.</summary>
    void RequestAudioRestart();
}
