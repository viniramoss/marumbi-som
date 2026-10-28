using System.Globalization;
using System.Text.Json;
using MarumbiSom.Agent.Player;

namespace MarumbiSom.Agent.Simulation;

/// <summary>
/// mpv falso: mantém lista de reprodução, volume, pausa e posição usando o <see cref="TimeProvider"/>, e emite os
/// mesmos eventos do IPC real (<c>start-file</c>, <c>file-loaded</c>, <c>end-file</c>). Usado no modo
/// <c>--simulate</c> e nos testes do player.
/// </summary>
public sealed class SimulatedMpvInstance : IMpvInstance
{
    /// <summary>Texto do <c>file_error</c> do mpv real quando a saída WASAPI não abre.</summary>
    public const string AudioOutputError = "audio output initialization failed";

    private readonly Lock _lock = new();
    private readonly List<string> _playlist = [];
    private readonly List<(DateTimeOffset At, double Volume)> _volumeHistory = [];
    private readonly List<(DateTimeOffset At, string Path)> _starts = [];
    private readonly TimeProvider _time;
    private readonly Func<string, TimeSpan> _durationOf;
    private readonly ILogger<SimulatedMpvInstance> _logger;
    private ITimer? _endTimer;
    private long _startedAt;
    private TimeSpan _positionWhenPaused;
    private int _current = -1;

    public SimulatedMpvInstance(string name, TimeProvider time, TimeSpan trackDuration, ILogger<SimulatedMpvInstance> logger)
        : this(name, time, _ => trackDuration, logger)
    {
    }

    public SimulatedMpvInstance(string name, TimeProvider time, Func<string, TimeSpan> durationOf, ILogger<SimulatedMpvInstance> logger)
    {
        Name = name;
        _time = time;
        _durationOf = durationOf;
        _logger = logger;
    }

    public event EventHandler<MpvEventArgs>? EventReceived;

    public event EventHandler? Exited;

    public string Name { get; }

    public bool IsRunning { get; private set; }

    public int StartCount { get; private set; }

    public string? AudioDevice { get; private set; }

    public double Volume { get; private set; }

    public bool Paused { get; private set; }

    /// <summary>Arquivos que o mpv falso "não consegue abrir" (end-file com reason=error).</summary>
    public ISet<string> BrokenFiles { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A posição parou de avançar (simula o mpv travado).</summary>
    public bool Frozen { get; private set; }

    /// <summary>Processo vivo, mas o IPC não responde: todo comando estoura o tempo.</summary>
    public bool Hung { get; private set; }

    /// <summary>A saída de áudio não abre (endpoint sumiu): todo arquivo termina com erro logo após carregar.</summary>
    public bool AudioOutputMissing { get; set; }

    /// <summary>O processo não abre (ex.: DLL faltando): <see cref="StartAsync"/> falha como o pipe que nunca conecta.</summary>
    public bool FailsToStart { get; set; }

    public int AudioOutputReloads { get; private set; }

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

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                return CurrentPosition();
            }
        }
    }

    public IReadOnlyList<(DateTimeOffset At, double Volume)> VolumeHistory
    {
        get
        {
            lock (_lock)
            {
                return _volumeHistory.ToList();
            }
        }
    }

    /// <summary>Instante em que cada arquivo começou a tocar.</summary>
    public IReadOnlyList<(DateTimeOffset At, string Path)> FileStarts
    {
        get
        {
            lock (_lock)
            {
                return _starts.ToList();
            }
        }
    }

    public Task StartAsync(string audioEndpointId, double volume, CancellationToken cancellationToken)
    {
        if (FailsToStart)
        {
            throw new TimeoutException($"mpv '{Name}' não abriu o pipe (simulado)");
        }

        lock (_lock)
        {
            IsRunning = true;
            StartCount++;
            Paused = false;
            Frozen = false;
            Hung = false;
            AudioDevice = "wasapi/" + audioEndpointId;
            Volume = volume;
            _playlist.Clear();
            _current = -1;
        }

        _logger.LogInformation("[simulado] mpv '{Name}' iniciado em {Device}", Name, AudioDevice);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            IsRunning = false;
            _endTimer?.Dispose();
            _endTimer = null;
            _playlist.Clear();
            _current = -1;
        }

        return Task.CompletedTask;
    }

    /// <summary>Simula o mpv travado: processo vivo, mas a posição não avança e a faixa nunca termina.</summary>
    public void Freeze()
    {
        lock (_lock)
        {
            _positionWhenPaused = CurrentPosition();
            Frozen = true;
            _endTimer?.Dispose();
            _endTimer = null;
        }
    }

    /// <summary>Simula o mpv travado sem resposta no IPC (PC piloto, 27/10/2026: set_property estourou o tempo).</summary>
    public void Hang()
    {
        Freeze();
        Hung = true;
    }

    /// <summary>
    /// Simula o endpoint da caixa desativado no meio do arquivo (PC piloto, 27/10/2026): o mpv encerra o arquivo com
    /// <c>end-file</c> reason=error e file_error "audio output initialization failed", e os próximos também falham.
    /// </summary>
    public void LoseAudioDevice()
    {
        AudioOutputMissing = true;
        EndCurrent("error", advance: true, AudioOutputError);
    }

    /// <summary>
    /// Simula a saída de áudio caindo no meio do arquivo (ex.: a caixa Bluetooth some por um instante): o mpv encerra o
    /// arquivo atual com <c>end-file</c> reason=eof, como se tivesse chegado ao fim.
    /// </summary>
    public void LoseAudioOutput() => EndCurrent("eof", advance: true);

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
        if (Hung)
        {
            throw new MpvCommandException($"Comando '{verb}' falhou no mpv '{Name}': The operation has timed out.", new TimeoutException()) { TimedOut = true };
        }

        object? result = null;
        switch (verb)
        {
            case "loadfile":
                LoadFile((string)command[1], command.Count > 2 && (string)command[2] == "append");
                break;
            case "playlist-remove":
                RemoveEntry(Convert.ToInt32(command[1], CultureInfo.InvariantCulture));
                break;
            case "playlist-clear":
                lock (_lock)
                {
                    var current = CurrentFileUnlocked();
                    _playlist.Clear();
                    if (current is not null)
                    {
                        _playlist.Add(current);
                        _current = 0;
                    }
                }

                break;
            case "stop":
                EndCurrent("stop", advance: false);
                lock (_lock)
                {
                    _playlist.Clear();
                    _current = -1;
                }

                break;
            case "seek":
                Seek(Convert.ToDouble(command[1], CultureInfo.InvariantCulture));
                break;
            case "ao-reload":
                AudioOutputReloads++;
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
            start = !append || _current == -1;
        }

        if (!append)
        {
            EndCurrent("stop", advance: false);
            lock (_lock)
            {
                _playlist.Clear();
                _current = -1;
            }
        }

        int index;
        lock (_lock)
        {
            _playlist.Add(path);
            index = _playlist.Count - 1;
        }

        if (start)
        {
            StartEntry(index);
        }
    }

    private void RemoveEntry(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _playlist.Count)
            {
                throw new MpvCommandException("invalid parameter");
            }

            _playlist.RemoveAt(index);
            if (index < _current)
            {
                _current--;
            }
        }
    }

    private void StartEntry(int index)
    {
        string path;
        lock (_lock)
        {
            _current = index;
            path = _playlist[index];
            _starts.Add((_time.GetUtcNow(), path));
            _startedAt = _time.GetTimestamp();
            _positionWhenPaused = TimeSpan.Zero;
            _endTimer?.Dispose();
            _endTimer = null;
        }

        Raise("start-file");
        if (BrokenFiles.Contains(path) || !File.Exists(path) && !path.StartsWith("sim:", StringComparison.Ordinal))
        {
            EndCurrent("error", advance: true);
            return;
        }

        if (AudioOutputMissing)
        {
            Raise("file-loaded");
            EndCurrent("error", advance: true, AudioOutputError);
            return;
        }

        lock (_lock)
        {
            ScheduleEnd(_durationOf(path));
        }

        Raise("file-loaded");
    }

    private void ScheduleEnd(TimeSpan remaining)
    {
        _endTimer?.Dispose();
        _endTimer = Paused || Frozen ? null : _time.CreateTimer(_ => EndCurrent("eof", advance: true), null, remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining, Timeout.InfiniteTimeSpan);
    }

    private void EndCurrent(string reason, bool advance, string? fileError = null)
    {
        int next;
        lock (_lock)
        {
            if (_current < 0)
            {
                return;
            }

            _endTimer?.Dispose();
            _endTimer = null;
            next = advance && _current + 1 < _playlist.Count ? _current + 1 : -1;
            if (next < 0)
            {
                _current = -1;
            }
        }

        Raise("end-file", reason, fileError);
        if (next >= 0)
        {
            StartEntry(next);
        }
    }

    private void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_current < 0)
            {
                throw new MpvCommandException("property unavailable");
            }

            var target = TimeSpan.FromSeconds(seconds);
            _positionWhenPaused = target;
            _startedAt = _time.GetTimestamp() - (long)(seconds * _time.TimestampFrequency);
            ScheduleEnd(_durationOf(_playlist[_current]) - target);
        }
    }

    private void SetProperty(string property, object value)
    {
        lock (_lock)
        {
            switch (property)
            {
                case "volume":
                    Volume = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    _volumeHistory.Add((_time.GetUtcNow(), Volume));
                    break;
                case "pause":
                    var pause = (bool)value;
                    if (pause && !Paused)
                    {
                        _positionWhenPaused = CurrentPosition();
                        Paused = true;
                        _endTimer?.Dispose();
                        _endTimer = null;
                    }
                    else if (!pause && Paused)
                    {
                        Paused = false;
                        _startedAt = _time.GetTimestamp() - (long)(_positionWhenPaused.TotalSeconds * _time.TimestampFrequency);
                        if (_current >= 0)
                        {
                            ScheduleEnd(_durationOf(_playlist[_current]) - _positionWhenPaused);
                        }
                    }

                    break;
                case "audio-device":
                    AudioDevice = (string)value;
                    break;
                default:
                    break;
            }
        }
    }

    private object? GetProperty(string property)
    {
        lock (_lock)
        {
            return property switch
            {
                "time-pos" when _current < 0 => throw new MpvCommandException("property unavailable"),
                "time-pos" => CurrentPosition().TotalSeconds,
                "duration" when _current < 0 => throw new MpvCommandException("property unavailable"),
                "duration" => _durationOf(_playlist[_current]).TotalSeconds,
                "volume" => Volume,
                "pause" => Paused,
                "path" => CurrentFileUnlocked(),
                _ => null,
            };
        }
    }

    private TimeSpan CurrentPosition()
    {
        if (_current < 0)
        {
            return TimeSpan.Zero;
        }

        if (Paused || Frozen)
        {
            return _positionWhenPaused;
        }

        var position = _time.GetElapsedTime(_startedAt);
        _positionWhenPaused = position;
        return position;
    }

    private string? CurrentFileUnlocked() => _current >= 0 && _current < _playlist.Count ? _playlist[_current] : null;

    private void Raise(string eventName, string? reason = null, string? fileError = null)
    {
        var fields = new Dictionary<string, object> { ["event"] = eventName };
        if (reason is not null)
        {
            fields["reason"] = reason;
        }

        if (fileError is not null)
        {
            fields["file_error"] = fileError;
        }

        EventReceived?.Invoke(this, new MpvEventArgs(eventName, JsonSerializer.SerializeToElement(fields)));
    }
}
