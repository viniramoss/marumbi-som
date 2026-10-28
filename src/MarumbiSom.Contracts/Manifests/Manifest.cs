using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Manifests;

/// <summary>
/// Estado desejado de uma loja (seção 11.1). O Hub gera; o agente sincroniza o cache e só então aplica.
/// </summary>
public sealed record Manifest
{
    public required string StoreId { get; init; }

    /// <summary>Inteiro monotônico por loja; nunca diminui (seção 6.4).</summary>
    public required long Version { get; init; }

    /// <summary>"sha256:&lt;hex&gt;" do JSON canônico (ver <see cref="ManifestHasher"/>).</summary>
    public string? ContentHash { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Fuso IANA da loja, ex.: "America/Sao_Paulo".</summary>
    public required string Timezone { get; init; }

    /// <summary>Volume da loja, 0–100.</summary>
    public required int Volume { get; init; }

    /// <summary>Duração da mixagem entre músicas, em segundos (seção 10.2.1). 0 = emenda sem pausa.</summary>
    public int CrossfadeS { get; init; } = 5;

    public IReadOnlyList<ScheduleEntry> Schedules { get; init; } = [];

    public IReadOnlyDictionary<string, PlaylistDefinition> Playlists { get; init; } = new Dictionary<string, PlaylistDefinition>();

    /// <summary>Músicas e áudios de ofertas, indexados pelo SHA-256 do conteúdo (regra 7).</summary>
    public IReadOnlyDictionary<string, TrackInfo> Tracks { get; init; } = new Dictionary<string, TrackInfo>();

    public SpotsSection Spots { get; init; } = new();
}

/// <summary>Janela de agenda. <see cref="Days"/>: 0 = domingo … 6 = sábado.</summary>
public sealed record ScheduleEntry(
    IReadOnlyList<int> Days,
    TimeOnly Start,
    TimeOnly End,
    string PlaylistId);

public sealed record PlaylistDefinition(bool Shuffle, IReadOnlyList<PlaylistItemRef> Items);

/// <summary>Item de playlist. O <see cref="Id"/> é estável ao reordenar; âncoras de ofertas apontam para ele.</summary>
public sealed record PlaylistItemRef(string Id, string Hash);

[JsonConverter(typeof(JsonStringEnumConverter<TrackKind>))]
public enum TrackKind
{
    [JsonStringEnumMemberName("music")] Music,
    [JsonStringEnumMemberName("offer")] Offer,
}

public sealed record TrackInfo
{
    public required TrackKind Kind { get; init; }

    public required long Size { get; init; }

    public required long DurationMs { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }
}
