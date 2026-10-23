using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Playback;

public enum PlayerCommandKind
{
    Play,
    Pause,
    Next,
    SetVolume,
    PlayNow,
    PlayOffer,
    TestSound,
}

/// <summary>Comando ao vivo para o player (seção 11.2), vindo do Hub ou da configuração local.</summary>
public sealed record PlayerCommand(PlayerCommandKind Kind, int? Volume = null, string? Hash = null, string? OfferId = null)
{
    /// <summary>Converte um comando do Hub. Devolve nulo para comandos que não são do player.</summary>
    public static PlayerCommand? FromHub(CommandMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Command switch
        {
            CommandTypes.Play => new PlayerCommand(PlayerCommandKind.Play),
            CommandTypes.Pause => new PlayerCommand(PlayerCommandKind.Pause),
            CommandTypes.Next => new PlayerCommand(PlayerCommandKind.Next),
            CommandTypes.SetVolume => new PlayerCommand(PlayerCommandKind.SetVolume, Volume: (int?)message.Args?["volume"]),
            CommandTypes.PlayNow => new PlayerCommand(PlayerCommandKind.PlayNow, Hash: (string?)message.Args?["hash"]),
            CommandTypes.PlayOffer => new PlayerCommand(PlayerCommandKind.PlayOffer, OfferId: (string?)message.Args?["offerId"]),
            _ => null,
        };
    }
}

public sealed record PlayerCommandResult(bool Ok, string? Error = null)
{
    public static PlayerCommandResult Success { get; } = new(true);

    public static PlayerCommandResult Fail(string error) => new(false, error);
}

/// <summary>Fotografia do player para o status (configuração local e, na Etapa 3, o Hub).</summary>
public sealed record PlayerSnapshot
{
    public static PlayerSnapshot Empty { get; } = new() { Status = PlayerStatus.Stopped };

    public required PlayerStatus Status { get; init; }

    public string? Reason { get; init; }

    public long? ManifestVersion { get; init; }

    public string? NowPlayingHash { get; init; }

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? PlaylistId { get; init; }

    public double? PositionS { get; init; }

    public double? DurationS { get; init; }

    public int Volume { get; init; }

    public bool Crossfading { get; init; }

    public string? WindowPlaylistId { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public bool PausedByAdmin { get; init; }

    public bool SpeakerConnected { get; init; }

    public string? Spot { get; init; }

    public IReadOnlyList<string> QueuedSpots { get; init; } = [];

    public IReadOnlyList<string> Upcoming { get; init; } = [];

    /// <summary>Hashes das mesmas faixas de <see cref="Upcoming"/>, para o Hub.</summary>
    public IReadOnlyList<string> UpcomingHashes { get; init; } = [];
}
