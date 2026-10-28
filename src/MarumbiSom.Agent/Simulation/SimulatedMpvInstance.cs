using System.Text.Json;
using MarumbiSom.Agent.Player;

namespace MarumbiSom.Agent.Simulation;

/// <summary>mpv falso: mantém lista de reprodução, volume, pausa e posição usando o <see cref="TimeProvider"/>.</summary>
public sealed class SimulatedMpvInstance(string name, TimeProvider time, TimeSpan trackDuration, ILogger<SimulatedMpvInstance> logger) : IMpvInstance
{
    private readonly Lock _lock = new();
    private readonly List<string> _playlist = [];
    private readonly List<(TimeSpan At, double Volume)> _volumeHistory = [];
    private ITimer? _endTimer;
    private long _startedAt;
    private TimeSpan _positionWhenPaused;
    private int _current = -1;

    public event EventHandler<MpvEventArgs>? EventReceived;

    public event EventHandler? Exited;

    public string Name { get; } = name;

    public bool IsRunning { get; private set; }

    public string? AudioDevice { get; private set; }

    public double Volume { get; private set; }

    public bool Paused { get; private set; }

    public IReadOnlyList<string> Playlist
    {
        get
        {
            lock (_lock)
            {
                return _playlist.ToList();
            }
        }
    }

    public string? CurrentFile
    {
        get
        {
            lock (_lock)
            {
                return _current >= 0 && _current < _playlist.Count ? _playlist[_current] : null;
            }
        }
    }

    public IReadOnlyList<(TimeSpan At, double Volume)> VolumeHistory
    {
        get
        {
            lock (_lock)
            {
                return _volumeHistory.ToList();
            }
        }
    }

    public Task StartAsync(string audioEndpointId, double volume, CancellationToken cancellationToken)
    {
        IsRunning = true;
        AudioDevice = "wasapi/" + audioEndpointId;
        Volume = volume;
        logger.LogInformation("[simulado] mpv '{Name}' iniciado em {Device}", Name, AudioDevice);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            IsRunning = false;
            _endTimer?.Dispose();
            _playlist.Clear();
            _current = -1;
        }

        return Task.CompletedTask;
    }

    /// <summary>Simula o processo morrendo (ex.: encerrado pelo Gerenciador de Tarefas).</summary>
    public void Crash()
    {
        StopAsync();
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        _endTimer?.Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<JsonElement> CommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            throw new MpvCommandException($"mpv '{Name}' não está em execução.");
        }

        var verb = (string)command[0];
        object? result = null;
        switch (verb)
        {
            case "loadfile":
                LoadFile((string)command[1], (string)command[2] == "append");
                break;
            case "playlist-remove":
                lock (_lock)
                {
                    var index = Convert.ToInt32(command[1], System.Globalization.CultureInfo.InvariantCulture);
                    _playlist.RemoveAt(index);
                    if (index <= _current)
                    {
                        _current--;
                    }
                }

                break;
            case "stop":
                EndCurrent("stop", advance: false);
                break;
            case "seek":
                _startedAt = time.GetTimestamp() - (long)(Convert.ToDouble(command[1], System.Globalization.CultureInfo.InvariantCulture) * time.TimestampFrequency);
                break;
            case "ao-reload":
                break;
            case "set_property":
                SetProperty((string)command[1], command[2]);
                break;
            case "get_property":
                result = GetProperty((string)command[1]);
                break;
            default:
                throw new MpvCommandException($"Comando simulado desconhecido: {verb}");
        }

        return Task.FromResult(JsonSerializer.SerializeToElement(result));
    }

    private void LoadFile(string path, bool append)
    {
        bool start;
        lock (_lock)
        {
            if (!append)
            {
                _playlist.Clear();
                _current = -1;
            }

            _playlist.Add(path);
            start = _current == -1;
        }

        if (start)
        {
            StartEntry(_playlist.Count - 1);
        }
    }

    private void StartEntry(int index)
    {
        lock (_lock)
        {
            _current = index;
            _startedAt = time.GetTimestamp();
            _endTimer?.Dispose();
            _endTimer = time.CreateTimer(_ => EndCurrent("eof", advance: true), null, trackDuration, Timeout.InfiniteTimeSpan);
        }

        Raise("start-file");
    }

    private void EndCurrent(string reason, bool advance)
    {
        int next;
        lock (_lock)
        {
            if (_current < 0)
            {
                return;
            }

            _endTimer?.Dispose();
            next = advance && _current + 1 < _playlist.Count ? _current + 1 : -1;
            if (next < 0)
            {
                _current = -1;
            }
        }

        Raise("end-file", reason);
        if (next >= 0)
        {
            StartEntry(next);
        }
    }

    private void SetProperty(string property, object value)
    {
        switch (property)
        {
            case "volume":
                Volume = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                lock (_lock)
                {
                    _volumeHistory.Add((time.GetUtcNow().TimeOfDay, Volume));
                }

                break;
            case "pause":
                var pause = (bool)value;
                if (pause && !Paused)
                {
                    _positionWhenPaused = time.GetElapsedTime(_startedAt);
                    _endTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
                else if (!pause && Paused)
                {
                    _startedAt = time.GetTimestamp() - (long)(_positionWhenPaused.TotalSeconds * time.TimestampFrequency);
                    _endTimer?.Change(trackDuration - _positionWhenPaused, Timeout.InfiniteTimeSpan);
                }

                Paused = pause;
                break;
            case "audio-device":
                AudioDevice = (string)value;
                break;
            default:
                break;
        }
    }

    private object? GetProperty(string property) => property switch
    {
        "time-pos" when _current < 0 => throw new MpvCommandException("property unavailable"),
        "time-pos" => (Paused ? _positionWhenPaused : time.GetElapsedTime(_startedAt)).TotalSeconds,
        "volume" => Volume,
        "pause" => Paused,
        _ => null,
    };

    private void Raise(string eventName, string? reason = null)
    {
        var payload = reason is null
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["event"] = eventName })
            : JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["event"] = eventName, ["reason"] = reason });
        EventReceived?.Invoke(this, new MpvEventArgs(eventName, payload));
    }
}
