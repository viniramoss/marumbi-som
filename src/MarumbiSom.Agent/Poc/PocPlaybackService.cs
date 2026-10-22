using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.Player;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent.Poc;

/// <summary>
/// Prova de conceito da Etapa 1 (seção 15): toca em loop as músicas de uma pasta local na caixa, com
/// faixa seguinte pré-carregada, pausa/retomada quando a caixa cai, watchdog do mpv e, a cada
/// <see cref="PocOptions.SpotIntervalMinutes"/>, uma oferta com ducking no segundo mpv.
/// Será substituído pelo PlayerController + Scheduler da Etapa 2.
/// </summary>
public sealed class PocPlaybackService : BackgroundService
{
    private static readonly string[] AudioExtensions = [".m4a", ".mp3", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma"];
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan VolumeReapplyInterval = TimeSpan.FromSeconds(10);

    private readonly IBluetoothConnection _connection;
    private readonly IMpvInstance _music;
    private readonly IMpvInstance _spot;
    private readonly SpotDucker _ducker;
    private readonly PlayerOptions _player;
    private readonly PocOptions _poc;
    private readonly TimeProvider _time;
    private readonly ILogger<PocPlaybackService> _logger;

    private List<string> _files = [];
    private int _currentIndex;
    private bool _ignoreNextStart;
    private double _resumePosition;
    private bool _pausedForBluetooth;
    private bool _musicCrashed;
    private bool _spotCrashed;
    private double? _lastPosition;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _lastVolumeApply;
    private DateTimeOffset _nextSpotAt;

    public PocPlaybackService(
        IBluetoothConnection connection,
        [FromKeyedServices(PlayerKeys.Music)] IMpvInstance music,
        [FromKeyedServices(PlayerKeys.Spot)] IMpvInstance spot,
        SpotDucker ducker,
        IOptions<PlayerOptions> player,
        IOptions<PocOptions> poc,
        TimeProvider time,
        ILogger<PocPlaybackService> logger)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(poc);
        _connection = connection;
        _music = music;
        _spot = spot;
        _ducker = ducker;
        _player = player.Value;
        _poc = poc.Value;
        _time = time;
        _logger = logger;
        _music.EventReceived += OnMusicEvent;
        _music.Exited += (_, _) => _musicCrashed = true;
        _spot.Exited += (_, _) => _spotCrashed = true;
    }

    public string? CurrentFile => _files.Count > 0 ? _files[_currentIndex % _files.Count] : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_poc.Enabled)
        {
            _logger.LogInformation("Reprodução da prova de conceito desativada (Poc:Enabled = false)");
            return;
        }

        _nextSpotAt = _time.GetUtcNow() + TimeSpan.FromMinutes(_poc.SpotIntervalMinutes);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no ciclo da reprodução da prova de conceito");
                await Task.Delay(TimeSpan.FromSeconds(2), _time, stoppingToken);
            }

            await Task.Delay(Tick, _time, stoppingToken);
        }

        await _music.StopAsync();
        await _spot.StopAsync();
    }

    /// <summary>Um ciclo de controle. Público para testes.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_files.Count == 0 && !LoadFiles())
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _time, cancellationToken);
            return;
        }

        if (!_connection.IsConnected || _connection.SpeakerEndpointId is not { } endpoint)
        {
            await PauseForBluetoothAsync(cancellationToken);
            return;
        }

        if (_musicCrashed || !_music.IsRunning)
        {
            await RestartMusicAsync(endpoint, cancellationToken);
            return;
        }

        if (_spotCrashed || !_spot.IsRunning)
        {
            _spotCrashed = false;
            await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken);
            await _spot.StartAsync(endpoint, _player.Volume, cancellationToken);
        }

        if (_pausedForBluetooth)
        {
            _logger.LogInformation("Caixa voltou; retomando a música");
            await _music.ReloadAudioOutputAsync(endpoint, cancellationToken);
            await _spot.ReloadAudioOutputAsync(endpoint, cancellationToken);
            await _music.SetPausedAsync(false, cancellationToken);
            _pausedForBluetooth = false;
            _lastPosition = null;
        }

        await CheckStallAsync(cancellationToken);

        var now = _time.GetUtcNow();
        if (!_ducker.IsDucking && now - _lastVolumeApply >= VolumeReapplyInterval)
        {
            // Seção 9.5: reaplica o volume da loja, desfazendo alterações externas.
            await _music.SetVolumeAsync(_player.Volume, cancellationToken);
            await _spot.SetVolumeAsync(_player.Volume, cancellationToken);
            _lastVolumeApply = now;
        }

        if (_poc.SpotFile is { } spotFile && now >= _nextSpotAt)
        {
            _nextSpotAt = now + TimeSpan.FromMinutes(_poc.SpotIntervalMinutes);
            if (File.Exists(spotFile))
            {
                var result = await _ducker.PlayDuringAsync(_music, _spot, spotFile, _player.Volume, _poc.DuckLevel, cancellationToken);
                _logger.LogInformation("Oferta de teste terminou: {Result}", result);
                _lastPosition = null;
            }
            else
            {
                _logger.LogWarning("Arquivo de oferta de teste não encontrado: {File}", spotFile);
            }
        }
    }

    private bool LoadFiles()
    {
        if (!Directory.Exists(_poc.MusicFolder))
        {
            _logger.LogError("Pasta de músicas da prova de conceito não existe: {Folder}", _poc.MusicFolder);
            return false;
        }

        _files = Directory.EnumerateFiles(_poc.MusicFolder)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_files.Count == 0)
        {
            _logger.LogError("Nenhuma música encontrada em {Folder}", _poc.MusicFolder);
            return false;
        }

        _logger.LogInformation("{Count} músicas carregadas de {Folder}", _files.Count, _poc.MusicFolder);
        return true;
    }

    private async Task PauseForBluetoothAsync(CancellationToken cancellationToken)
    {
        if (_pausedForBluetooth || !_music.IsRunning)
        {
            return;
        }

        // Seção 9.5: sem caixa, pausa e guarda a posição; ao reconectar, retoma do mesmo ponto.
        _resumePosition = await _music.GetPositionAsync(cancellationToken) ?? 0;
        try
        {
            await _music.SetPausedAsync(true, cancellationToken);
        }
        catch (MpvCommandException ex)
        {
            _logger.LogWarning(ex, "Falha ao pausar a música após a queda da caixa");
        }

        _pausedForBluetooth = true;
        _logger.LogWarning("Caixa desconectada; música pausada em {Position:0.0} s de {File}", _resumePosition, Path.GetFileName(CurrentFile));
    }

    private async Task RestartMusicAsync(string endpoint, CancellationToken cancellationToken)
    {
        if (_musicCrashed)
        {
            // Watchdog (seção 10.1): reinicia 2 s após o processo morrer.
            await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken);
        }

        _musicCrashed = false;
        _pausedForBluetooth = false;
        await _music.StartAsync(endpoint, _player.Volume, cancellationToken);
        await PlayFromCurrentAsync(cancellationToken);
    }

    private async Task PlayFromCurrentAsync(CancellationToken cancellationToken)
    {
        var current = CurrentFile!;
        _ignoreNextStart = true;
        await _music.LoadFileAsync(current, append: false, cancellationToken);
        await _music.LoadFileAsync(_files[(_currentIndex + 1) % _files.Count], append: true, cancellationToken);
        if (_resumePosition > 1)
        {
            await WaitForFileLoadedAsync(cancellationToken);
            await _music.SeekAsync(_resumePosition, cancellationToken);
        }

        _logger.LogInformation("Tocando {File} a partir de {Position:0.0} s", Path.GetFileName(current), _resumePosition);
        _resumePosition = 0;
        _lastPosition = null;
    }

    private async Task WaitForFileLoadedAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await _music.GetPositionAsync(cancellationToken) is not null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), _time, cancellationToken);
        }
    }

    private async Task CheckStallAsync(CancellationToken cancellationToken)
    {
        var position = await _music.GetPositionAsync(cancellationToken);
        var now = _time.GetUtcNow();
        if (position is null || _lastPosition is null || Math.Abs(position.Value - _lastPosition.Value) > 0.01)
        {
            _lastPosition = position;
            _lastProgressAt = now;
            return;
        }

        if (now - _lastProgressAt < StallLimit)
        {
            return;
        }

        // Watchdog (seção 10.1): "tocando" sem a posição avançar por 30 s → mata e reinicia.
        _logger.LogWarning("mpv de música parado em {Position:0.0} s há {Elapsed}; reiniciando", position, now - _lastProgressAt);
        _resumePosition = position.Value;
        await _music.StopAsync();
        _musicCrashed = false;
        _lastPosition = null;
    }

    private void OnMusicEvent(object? sender, MpvEventArgs e)
    {
        if (e.Name != "start-file")
        {
            return;
        }

        if (_ignoreNextStart)
        {
            _ignoreNextStart = false;
            return;
        }

        // A faixa pré-carregada começou: remove a anterior da lista do mpv e pré-carrega a seguinte.
        _currentIndex = (_currentIndex + 1) % _files.Count;
        var next = _files[(_currentIndex + 1) % _files.Count];
        _logger.LogInformation("Faixa iniciada: {File}", Path.GetFileName(CurrentFile));
        _ = AdvanceQueueAsync(next);
    }

    private async Task AdvanceQueueAsync(string next)
    {
        try
        {
            await _music.CommandAsync(["playlist-remove", 0], CancellationToken.None);
            await _music.LoadFileAsync(next, append: true, CancellationToken.None);
        }
        catch (MpvCommandException ex)
        {
            _logger.LogWarning(ex, "Falha ao pré-carregar a próxima faixa {File}", Path.GetFileName(next));
        }
    }
}

public static class PlayerKeys
{
    public const string Music = "music";
    public const string Spot = "spot";
}
