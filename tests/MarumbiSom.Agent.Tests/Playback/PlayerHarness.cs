using System.Security.Cryptography;
using System.Text;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Player;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Spots;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Playback;

/// <summary>Player completo com mpv simulados, relógio falso e pasta de dados temporária.</summary>
internal sealed class PlayerHarness : IDisposable
{
    /// <summary>Sexta-feira, 23/10/2026, 09:59:00 em São Paulo.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 23, 9, 59, 0, TimeSpan.FromHours(-3));

    private readonly Dictionary<string, TimeSpan> _durations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackInfo> _tracks = new(StringComparer.Ordinal);
    private readonly bool _ownsDirectory;
    private long _version;

    public PlayerHarness(string? dataDirectory = null, FakeTimeProvider? time = null)
    {
        _ownsDirectory = dataDirectory is null;
        Paths = new AgentDataPaths(dataDirectory ?? Path.Combine(Path.GetTempPath(), "marumbi-tests", Guid.NewGuid().ToString("N")));
        Time = time ?? new FakeTimeProvider(Start);
        Manifests = new ManifestStore(Paths, NullLogger<ManifestStore>.Instance);
        Cache = new CacheManager(Paths, Time, NullLogger<CacheManager>.Instance);
        State = new StateStore(Paths, NullLogger<StateStore>.Instance);
        Events = new PlaybackEventLog(Time, NullLogger<PlaybackEventLog>.Instance);
        Spots = new SpotScheduler(State, Cache, Events, NullLogger<SpotScheduler>.Instance);
        A = new SimulatedMpvInstance("A", Time, DurationOf, NullLogger<SimulatedMpvInstance>.Instance);
        B = new SimulatedMpvInstance("B", Time, DurationOf, NullLogger<SimulatedMpvInstance>.Instance);
        Spot = new SimulatedMpvInstance("ofertas", Time, DurationOf, NullLogger<SimulatedMpvInstance>.Instance);
        State.Load();
        Manifests.Load();
        _version = Manifests.Active?.Version ?? 0;
        Engine = new PlayerEngine(
            A, B, Spot, Bluetooth, Manifests, Cache, State, new PlaylistOrder(new Random(7)), Spots, Events, Paths.Root, Time,
            NullLogger<PlayerEngine>.Instance);
    }

    public AgentDataPaths Paths { get; }

    public FakeTimeProvider Time { get; }

    public ManifestStore Manifests { get; }

    public CacheManager Cache { get; }

    public StateStore State { get; }

    public PlaybackEventLog Events { get; }

    public SpotScheduler Spots { get; }

    public SimulatedMpvInstance A { get; }

    public SimulatedMpvInstance B { get; }

    public SimulatedMpvInstance Spot { get; }

    public FakeBluetooth Bluetooth { get; } = new();

    public PlayerEngine Engine { get; }

    public DateTimeOffset Now => Time.GetUtcNow();

    public static double StoreGain(double volume = 70) => MpvVolume.ToGain(volume);

    /// <summary>Cria um áudio falso no cache e devolve o hash.</summary>
    public string AddTrack(string title, double seconds, TrackKind kind = TrackKind.Music)
    {
        var content = Encoding.UTF8.GetBytes(title + "|" + Guid.NewGuid());
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var path = Cache.PathFor(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        _durations[hash] = TimeSpan.FromSeconds(seconds);
        _tracks[hash] = new TrackInfo { Kind = kind, Size = content.Length, DurationMs = (long)(seconds * 1000), Title = title };
        return hash;
    }

    /// <summary>Registra uma faixa que está no manifesto mas não no cache.</summary>
    public string AddMissingTrack(string title, double seconds)
    {
        var hash = AddTrack(title, seconds);
        File.Delete(Cache.PathFor(hash));
        return hash;
    }

    public string PathOf(string hash) => Cache.PathFor(hash);

    public Manifest Publish(
        IReadOnlyDictionary<string, PlaylistDefinition> playlists,
        IReadOnlyList<ScheduleEntry>? schedules = null,
        int crossfade = 5,
        int volume = 70,
        SpotsSection? spots = null)
    {
        var manifest = new Manifest
        {
            StoreId = "loja-teste",
            Version = ++_version,
            GeneratedAt = Now,
            Timezone = "America/Sao_Paulo",
            Volume = volume,
            CrossfadeS = crossfade,
            Schedules = schedules ?? [Window("10:00", "12:00", playlists.Keys.First())],
            Playlists = playlists,
            Tracks = new Dictionary<string, TrackInfo>(_tracks),
            Spots = spots ?? new SpotsSection(),
        };
        Manifests.SaveActive(manifest);
        return manifest;
    }

    public static ScheduleEntry Window(string start, string end, string playlistId) =>
        new([0, 1, 2, 3, 4, 5, 6], TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            end == "24:00" ? TimeOnly.MaxValue : TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), playlistId);

    public static PlaylistDefinition Playlist(bool shuffle, params string[] hashes) =>
        new(shuffle, hashes.Select((h, i) => new PlaylistItemRef($"it-{i + 1}", h)).ToList());

    /// <summary>Roda o player até o horário indicado (hora local de São Paulo no dia do teste).</summary>
    public Task RunUntilAsync(string localTime) => RunUntilAsync(At(localTime));

    public async Task RunUntilAsync(DateTimeOffset target)
    {
        while (Now < target)
        {
            var delay = await Engine.StepAsync(CancellationToken.None);
            var remaining = target - Now;
            Time.Advance(delay < remaining ? delay : remaining);
        }

        await Engine.StepAsync(CancellationToken.None);
    }

    public Task RunForAsync(TimeSpan duration) => RunUntilAsync(Now + duration);

    public async Task<PlayerCommandResult> CommandAsync(PlayerCommand command)
    {
        var result = Engine.ExecuteAsync(command, CancellationToken.None);
        await Engine.StepAsync(CancellationToken.None);
        return await result;
    }

    public static DateTimeOffset At(string localTime)
    {
        var time = TimeSpan.Parse(localTime, System.Globalization.CultureInfo.InvariantCulture);
        return new DateTimeOffset(Start.Date + time, Start.Offset);
    }

    /// <summary>Instantes em que cada faixa começou (eventos track.started).</summary>
    public IReadOnlyList<(string Hash, DateTimeOffset At)> TrackStarts =>
        Events.Recent.Select(e => e.Message).OfType<TrackStartedMessage>().Select(m => (m.Hash, m.At)).ToList();

    public IReadOnlyList<AgentMessage> Messages => Events.Recent.Select(e => e.Message).ToList();

    /// <summary>Último volume enviado ao mpv até o instante indicado.</summary>
    public static double VolumeAt(SimulatedMpvInstance mpv, DateTimeOffset at) =>
        mpv.VolumeHistory.LastOrDefault(v => v.At <= at, (DateTimeOffset.MinValue, 0)).Volume;

    /// <summary>Ganho de amplitude (0–1) relativo ao volume da loja.</summary>
    public static double GainAt(SimulatedMpvInstance mpv, DateTimeOffset at, double storeVolume = 70) =>
        MpvVolume.ToGain(VolumeAt(mpv, at)) / StoreGain(storeVolume);

    public void Dispose()
    {
        if (_ownsDirectory && Directory.Exists(Paths.Root))
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
    }

    private TimeSpan DurationOf(string path) =>
        path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(1)
        : _durations.GetValueOrDefault(Path.GetFileNameWithoutExtension(path), TimeSpan.FromSeconds(20));
}

internal sealed class FakeBluetooth : IBluetoothConnection
{
    private bool _connected = true;

    /// <summary>Como o guardião: avisa ConnectionChanged a cada troca, sempre com o mesmo endpoint.</summary>
    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (_connected == value)
            {
                return;
            }

            _connected = value;
            ConnectionChanged?.Invoke(this, new BluetoothConnectionChangedEventArgs(value, "sim-endpoint"));
        }
    }

    public string? SpeakerEndpointId => IsConnected ? "sim-endpoint" : null;

    public BluetoothStatus Status => IsConnected ? BluetoothStatus.Connected : BluetoothStatus.Disconnected;

    public int ReconnectRequests { get; private set; }

    public event EventHandler<BluetoothConnectionChangedEventArgs>? ConnectionChanged;

    public void RequestReconnect() => ReconnectRequests++;
}
