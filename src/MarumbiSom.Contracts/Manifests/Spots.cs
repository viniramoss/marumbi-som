using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Manifests;

/// <summary>Ofertas faladas e seus posicionamentos (seções 10.3 e 11.1).</summary>
public sealed record SpotsSection
{
    /// <summary>Intervalo mínimo entre ofertas da loja, em segundos. Padrão 300.</summary>
    public int MinOfferGapS { get; init; } = 300;

    public IReadOnlyDictionary<string, OfferDefinition> Offers { get; init; } = new Dictionary<string, OfferDefinition>();

    public IReadOnlyList<Placement> Placements { get; init; } = [];
}

public sealed record OfferDefinition
{
    public required string Hash { get; init; }

    public required string Name { get; init; }

    /// <summary>Volume da música durante a oferta, em % do volume da loja (0–100). Padrão 30.</summary>
    public int DuckLevel { get; init; } = 30;

    public required DateTimeOffset ValidFrom { get; init; }

    public required DateTimeOffset ValidUntil { get; init; }

    /// <summary>Regra 14: oferta fora da validade nunca toca.</summary>
    public bool IsValidAt(DateTimeOffset now) => now >= ValidFrom && now < ValidUntil;
}

[JsonConverter(typeof(JsonStringEnumConverter<SpotMode>))]
public enum SpotMode
{
    /// <summary>Entre músicas: a música atual termina, a oferta toca em volume cheio.</summary>
    [JsonStringEnumMemberName("between")] Between,

    /// <summary>Durante a música, com ducking.</summary>
    [JsonStringEnumMemberName("during")] During,
}

/// <summary>Posicionamento de uma oferta (seção 10.3.4). O campo "kind" define o tipo concreto.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(AnchorBetweenPlacement), PlacementKinds.AnchorBetween)]
[JsonDerivedType(typeof(AnchorDuringPlacement), PlacementKinds.AnchorDuring)]
[JsonDerivedType(typeof(EveryTracksPlacement), PlacementKinds.EveryTracks)]
[JsonDerivedType(typeof(EveryMinutesPlacement), PlacementKinds.EveryMinutes)]
[JsonDerivedType(typeof(ClockPlacement), PlacementKinds.Clock)]
public abstract record Placement
{
    public required string Id { get; init; }

    public required string OfferId { get; init; }
}

public static class PlacementKinds
{
    public const string AnchorBetween = "anchorBetween";
    public const string AnchorDuring = "anchorDuring";
    public const string EveryTracks = "everyTracks";
    public const string EveryMinutes = "everyMinutes";
    public const string Clock = "clock";
}

/// <summary>Toca quando o item indicado termina.</summary>
public sealed record AnchorBetweenPlacement : Placement
{
    public required string PlaylistId { get; init; }

    public required string AfterItemId { get; init; }
}

/// <summary>Toca durante o item indicado, aos <see cref="OffsetMs"/> do início.</summary>
public sealed record AnchorDuringPlacement : Placement
{
    public required string PlaylistId { get; init; }

    public required string ItemId { get; init; }

    public required long OffsetMs { get; init; }
}

/// <summary>Regra de recorrência; vale para todas as playlists, exceto se restrita por <see cref="PlaylistIds"/>.</summary>
public abstract record RulePlacement : Placement
{
    public IReadOnlyList<string>? PlaylistIds { get; init; }
}

/// <summary>Toca entre músicas a cada <see cref="N"/> músicas tocadas.</summary>
public sealed record EveryTracksPlacement : RulePlacement
{
    [JsonPropertyName("n")]
    public required int N { get; init; }
}

/// <summary>Toca a cada <see cref="Minutes"/> minutos desde o último disparo desta regra.</summary>
public sealed record EveryMinutesPlacement : RulePlacement
{
    public required int Minutes { get; init; }

    public required SpotMode Mode { get; init; }
}

/// <summary>Toca no horário indicado, nos dias indicados (0 = domingo … 6 = sábado).</summary>
public sealed record ClockPlacement : RulePlacement
{
    public required TimeOnly Time { get; init; }

    public required IReadOnlyList<int> Days { get; init; }

    public required SpotMode Mode { get; init; }
}
