using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Messages;

/// <summary>Mensagens WebSocket enviadas do agente para o Hub (seção 11.4).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(StatusMessage), "status")]
[JsonDerivedType(typeof(CommandResultMessage), "command.result")]
[JsonDerivedType(typeof(ManifestAppliedMessage), "manifest.applied")]
[JsonDerivedType(typeof(AgentEventMessage), "event")]
[JsonDerivedType(typeof(TrackStartedMessage), "track.started")]
[JsonDerivedType(typeof(SpotStartedMessage), "spot.started")]
[JsonDerivedType(typeof(SpotFinishedMessage), "spot.finished")]
[JsonDerivedType(typeof(SpotSkippedMessage), "spot.skipped")]
public abstract record AgentMessage;

public sealed record HelloMessage(string StoreId, string AgentVersion, long ActiveVersion) : AgentMessage;

public sealed record StatusMessage : AgentMessage
{
    public required PlayerStatus Player { get; init; }

    public string? NowPlaying { get; init; }

    public long PositionMs { get; init; }

    public int Volume { get; init; }

    public required BluetoothStatus Bluetooth { get; init; }

    public long? PendingVersion { get; init; }

    public SyncProgress? SyncProgress { get; init; }

    public long DiskFreeMb { get; init; }

    public long UptimeS { get; init; }

    /// <summary>Playlist da agenda em vigor (D23).</summary>
    public string? PlaylistId { get; init; }

    /// <summary>Hashes das próximas até 10 faixas previstas (D23).</summary>
    public IReadOnlyList<string>? Queue { get; init; }

    /// <summary>Próxima oferta prevista (D26 d).</summary>
    public NextSpot? NextSpot { get; init; }

    /// <summary>Atualização do agente em andamento (D27 e).</summary>
    public Releases.UpdateProgress? Update { get; init; }
}

public sealed record SyncProgress(int Done, int Total);

/// <summary>Oferta que o agente prevê tocar em seguida e quando (estimativa: a fila e as regras podem mudar).</summary>
public sealed record NextSpot(string OfferId, DateTimeOffset At);

[JsonConverter(typeof(JsonStringEnumConverter<PlayerStatus>))]
public enum PlayerStatus
{
    [JsonStringEnumMemberName("stopped")] Stopped,
    [JsonStringEnumMemberName("playing")] Playing,
    [JsonStringEnumMemberName("paused")] Paused,
}

[JsonConverter(typeof(JsonStringEnumConverter<BluetoothStatus>))]
public enum BluetoothStatus
{
    [JsonStringEnumMemberName("disconnected")] Disconnected,
    [JsonStringEnumMemberName("connecting")] Connecting,
    [JsonStringEnumMemberName("connected")] Connected,
    [JsonStringEnumMemberName("radioOff")] RadioOff,
}

public sealed record CommandResultMessage(string Id, bool Ok, string? Error = null) : AgentMessage;

public sealed record ManifestAppliedMessage(long Version) : AgentMessage;

/// <summary>Evento genérico do agente, ex.: <c>bluetooth.lost</c>.</summary>
public sealed record AgentEventMessage(string Event, JsonObject? Data = null) : AgentMessage;

public sealed record TrackStartedMessage(string Hash, DateTimeOffset At) : AgentMessage;

public sealed record SpotStartedMessage(string OfferId, string? PlacementId, Manifests.SpotMode Mode, DateTimeOffset At) : AgentMessage;

public sealed record SpotFinishedMessage(string OfferId, SpotResult Result) : AgentMessage;

public sealed record SpotSkippedMessage(string OfferId, string? PlacementId, string Reason) : AgentMessage;

[JsonConverter(typeof(JsonStringEnumConverter<SpotResult>))]
public enum SpotResult
{
    [JsonStringEnumMemberName("played")] Played,
    [JsonStringEnumMemberName("interrupted")] Interrupted,
}

/// <summary>Motivos de descarte de oferta (seção 10.3).</summary>
public static class SpotSkipReasons
{
    public const string Stale = "stale";
    public const string MissingFile = "missing_file";
    public const string Expired = "expired";
    public const string OutsideSchedule = "outside_schedule";
    public const string Duplicate = "duplicate";
    public const string QueueFull = "queue_full";
}

/// <summary>Nomes de eventos enviados em <see cref="AgentEventMessage"/>.</summary>
public static class AgentEvents
{
    public const string BluetoothLost = "bluetooth.lost";
    public const string BluetoothConnected = "bluetooth.connected";
    public const string BluetoothRadioOff = "bluetooth.radioOff";
    public const string BluetoothRecoveryAction = "bluetooth.recovery";
    public const string PlayerRestarted = "player.restarted";
    public const string AgentStarted = "agent.started";

    /// <summary>Atualização recusada ou que falhou antes de instalar (D27 e); <c>data</c> traz version e error.</summary>
    public const string UpdateFailed = "update.failed";

    /// <summary>Agente novo no ar depois de uma atualização (D27 f).</summary>
    public const string UpdateDone = "update.done";

    /// <summary>O Updater restaurou a versão anterior (D27 f).</summary>
    public const string UpdateRolledBack = "update.rolledBack";
}
