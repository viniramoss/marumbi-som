using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Messages;

/// <summary>Mensagens WebSocket enviadas do Hub para o agente (seção 11.4).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ManifestChangedMessage), "manifest.changed")]
[JsonDerivedType(typeof(CommandMessage), "command")]
public abstract record HubMessage;

public sealed record ManifestChangedMessage(long Version) : HubMessage;

/// <summary>Comando ao vivo. <see cref="Command"/> é um dos valores de <see cref="CommandTypes"/>.</summary>
public sealed record CommandMessage(string Id, string Command, JsonObject? Args = null) : HubMessage;

/// <summary>Tipos de comando (seção 11.2).</summary>
public static class CommandTypes
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Next = "next";
    public const string SetVolume = "setVolume";
    public const string PlayNow = "playNow";
    public const string PlayOffer = "playOffer";
    public const string ReconnectBluetooth = "reconnectBluetooth";
    public const string RestartAgent = "restartAgent";
    public const string UpdateAgent = "updateAgent";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Play, Pause, Next, SetVolume, PlayNow, PlayOffer, ReconnectBluetooth, RestartAgent, UpdateAgent,
    };
}
