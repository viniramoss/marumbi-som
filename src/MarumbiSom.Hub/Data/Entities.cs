using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Hub.Data;

// Modelo de dados da seção 6.3. Datas sempre em UTC; o fuso de cada loja só entra na agenda e no manifesto.

public sealed class User
{
    public int Id { get; set; }

    public required string Username { get; set; }

    public required string PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Track
{
    /// <summary>SHA-256 (hex minúsculo) do arquivo processado (regra 7).</summary>
    public required string Hash { get; set; }

    public TrackKind Kind { get; set; }

    public required string Title { get; set; }

    public string? Artist { get; set; }

    public long DurationMs { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Loudness integrado do arquivo enviado, antes da normalização.</summary>
    public double? LoudnessLufs { get; set; }

    public required string OriginalName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class Playlist
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public bool Shuffle { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PlaylistItem> Items { get; set; } = [];
}

public sealed class PlaylistItem
{
    /// <summary>UUID estável: preservado ao reordenar; âncoras de ofertas apontam para ele.</summary>
    public required string Id { get; set; }

    public int PlaylistId { get; set; }

    public int Position { get; set; }

    public required string TrackHash { get; set; }
}

public sealed class Store
{
    public int Id { get; set; }

    /// <summary>Identificador da loja no manifesto (<c>storeId</c>), ex.: "loja-2".</summary>
    public required string Slug { get; set; }

    public required string Name { get; set; }

    public int Volume { get; set; } = 70;

    public string? BtDeviceName { get; set; }

    public string Timezone { get; set; } = "America/Sao_Paulo";

    public int MinOfferGapS { get; set; } = 300;

    public int CrossfadeS { get; set; } = 5;

    /// <summary>SHA-256 do token do agente; o token em si nunca é guardado (seção 8.5).</summary>
    public string? TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Schedule> Schedules { get; set; } = [];
}

public sealed class Schedule
{
    public int Id { get; set; }

    public int StoreId { get; set; }

    public int PlaylistId { get; set; }

    /// <summary>Bit 0 = domingo … bit 6 = sábado.</summary>
    public int DaysMask { get; set; }

    public TimeOnly StartTime { get; set; }

    /// <summary><see cref="TimeOnly.MaxValue"/> representa 24:00.</summary>
    public TimeOnly EndTime { get; set; }
}

public sealed class StoreManifest
{
    public int StoreId { get; set; }

    public long Version { get; set; }

    public required string ContentHash { get; set; }

    public required string Json { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class StoreState
{
    public int StoreId { get; set; }

    public long? AppliedVersion { get; set; }

    public long? PendingVersion { get; set; }

    public int? SyncDone { get; set; }

    public int? SyncTotal { get; set; }

    public string? NowPlayingHash { get; set; }

    public long? PositionMs { get; set; }

    public string? PlayerState { get; set; }

    public string? BtState { get; set; }

    public int? Volume { get; set; }

    public long? DiskFreeMb { get; set; }

    public string? AgentVersion { get; set; }

    public long? UptimeS { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
}

public sealed class EnrollCode
{
    public required string CodeHash { get; set; }

    public int StoreId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? UsedAt { get; set; }
}

public sealed class StoreCommand
{
    public int Id { get; set; }

    public int StoreId { get; set; }

    public required string Type { get; set; }

    public string? ArgsJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    /// <summary><c>ok</c>, <c>error</c>, <c>offline</c> ou <c>timeout</c>; nulo enquanto aguarda.</summary>
    public string? Result { get; set; }

    public string? Error { get; set; }
}

public sealed class StoreEvent
{
    public long Id { get; set; }

    public int StoreId { get; set; }

    public required string Type { get; set; }

    public string? DataJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PlayHistory
{
    public long Id { get; set; }

    public int StoreId { get; set; }

    public required string TrackHash { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

public sealed class Offer
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public required string AudioHash { get; set; }

    public DateTimeOffset ValidFrom { get; set; }

    public DateTimeOffset ValidUntil { get; set; }

    public int DuckLevel { get; set; } = 30;

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class OfferStore
{
    public int OfferId { get; set; }

    public int StoreId { get; set; }
}

/// <summary>Posicionamento de oferta (seção 10.3.4); os campos usados dependem de <see cref="Kind"/>.</summary>
public sealed class OfferPlacement
{
    public int Id { get; set; }

    public int OfferId { get; set; }

    /// <summary>Um dos valores de <see cref="PlacementKinds"/>.</summary>
    public required string Kind { get; set; }

    public SpotMode? Mode { get; set; }

    public int? PlaylistId { get; set; }

    public string? PlaylistItemId { get; set; }

    public long? OffsetMs { get; set; }

    public int? EveryN { get; set; }

    public int? EveryMinutes { get; set; }

    public TimeOnly? ClockTime { get; set; }

    public int? DaysMask { get; set; }

    /// <summary>Lista JSON de ids de playlist que restringem a regra; nulo = todas.</summary>
    public string? PlaylistIdsJson { get; set; }
}

public sealed class OfferPlay
{
    public long Id { get; set; }

    public int StoreId { get; set; }

    public int? OfferId { get; set; }

    public int? PlacementId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary><c>played</c>, <c>interrupted</c> ou <c>skipped</c>.</summary>
    public string? Result { get; set; }

    public string? Reason { get; set; }
}

public sealed class AuditEntry
{
    public long Id { get; set; }

    public int? UserId { get; set; }

    public required string Action { get; set; }

    public string? Target { get; set; }

    public string? DataJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
