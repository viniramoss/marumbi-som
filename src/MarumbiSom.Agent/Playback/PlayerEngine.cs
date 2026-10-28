using System.Collections.Concurrent;
using System.Text.Json;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Player;
using MarumbiSom.Agent.Scheduling;
using MarumbiSom.Agent.Spots;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Playback;

/// <summary>
/// PlayerController (seções 8.2, 10.1, 10.2 e 10.3): agenda, fila, mixagem entre músicas com dois mpv de música
/// alternados, ofertas entre/durante a música no terceiro mpv, pausa do administrador, caixa desconectada e vigia
/// dos processos. Todo o estado pertence a <see cref="StepAsync"/>, chamado em laço por uma única thread; eventos
/// do mpv e comandos entram por filas. O volume de cada mpv é recalculado a cada ciclo como o produto dos
/// envelopes (loja × mixagem × ducking × fim do horário × pausa), por isso nunca muda em degrau.
/// </summary>
public sealed class PlayerEngine
{
    public static readonly TimeSpan SlowTick = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan FastTick = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan WindowEndFade = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan QuickTransition = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DuckDown = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan DuckRestore = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan BetweenSpotGap = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan MaxSpotDuration = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan SpotEarlyEndTolerance = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan PreloadLead = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan VolumeReapply = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinStartBeforeWindowEnd = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] AudioOutputRetry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private static readonly TimeSpan AudioOutputHealthy = TimeSpan.FromSeconds(5);

    private readonly Deck _deckA;
    private readonly Deck _deckB;
    private readonly Deck _spotDeck;
    private readonly IBluetoothConnection _bluetooth;
    private readonly ManifestStore _manifests;
    private readonly CacheManager _cache;
    private readonly StateStore _state;
    private readonly PlaylistOrder _order;
    private readonly SpotScheduler _spots;
    private readonly IPlaybackEvents _events;
    private readonly string _testSoundDirectory;
    private readonly TimeProvider _time;
    private readonly ILogger<PlayerEngine> _logger;
    private readonly ConcurrentQueue<DeckEvent> _deckEvents = new();
    private readonly ConcurrentQueue<PendingCommand> _commands = new();

    private Manifest? _manifest;
    private volatile bool _manifestChanged = true;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private Phase _phase = Phase.Idle;
    private Deck? _active;
    private Deck? _outgoing;
    private bool _transitionDecided;
    private SpotToPlay? _betweenPlanned;
    private ActiveSpot? _spot;
    private ResumePoint? _resume;
    private DateTimeOffset _resumeAt;
    private DateTimeOffset? _fadingWindowEnd;
    private Envelope _storeGain = Envelope.Constant(MpvVolume.ToGain(70));
    private int _storeVolume = -1;
    private Envelope _duck = Envelope.Constant(1);
    private Envelope _windowFade = Envelope.Constant(1);
    private Envelope _pauseFade = Envelope.Constant(1);
    private bool _adminPauseStarted;
    private bool _adminPauseApplied;
    private bool _bluetoothPaused;
    private long _trackInstance;
    private int _consecutiveErrors;
    private int _audioOutputFailures;
    private volatile bool _reloadOutputs;
    private DateTimeOffset _retryAt;
    private DateTimeOffset _lastReapply;
    private string? _lastIdleReason;
    private ScheduleWindow? _window;
    private PlayerSnapshot _snapshot = PlayerSnapshot.Empty;
    private DateTimeOffset _snapshotAt;

    public PlayerEngine(
        IMpvInstance musicA,
        IMpvInstance musicB,
        IMpvInstance spot,
        IBluetoothConnection bluetooth,
        ManifestStore manifests,
        CacheManager cache,
        StateStore state,
        PlaylistOrder order,
        SpotScheduler spots,
        IPlaybackEvents events,
        string testSoundDirectory,
        TimeProvider time,
        ILogger<PlayerEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        _deckA = new Deck(musicA, "A");
        _deckB = new Deck(musicB, "B");
        _spotDeck = new Deck(spot, "ofertas");
        _bluetooth = bluetooth;
        _manifests = manifests;
        _cache = cache;
        _state = state;
        _order = order;
        _spots = spots;
        _events = events;
        _testSoundDirectory = testSoundDirectory;
        _time = time;
        _logger = logger;
        foreach (var deck in AllDecks)
        {
            var target = deck;
            deck.Mpv.EventReceived += (_, e) => _deckEvents.Enqueue(new DeckEvent(target, e.Name, e.Reason, e.FileError));
            deck.Mpv.Exited += (_, _) => _deckEvents.Enqueue(new DeckEvent(target, DeckEvent.Crash, null));
        }

        manifests.ActiveChanged += (_, _) => _manifestChanged = true;

        // Caixa reconectada com o mesmo id de endpoint: o mpv pode ter ficado com a saída antiga, morta (seção 9.5).
        bluetooth.ConnectionChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                _reloadOutputs = true;
            }
        };
    }

    private enum Phase
    {
        /// <summary>Nenhuma faixa carregada: começa a próxima assim que a agenda permitir.</summary>
        Idle,

        Playing,

        /// <summary>Música terminou e a oferta "entre" está tocando em volume cheio.</summary>
        BetweenSpot,

        /// <summary>Oferta "entre" terminou; a próxima música começa 300 ms depois.</summary>
        AfterSpot,
    }

    private enum SpotPhase
    {
        DuckingDown,
        Playing,
    }

    public PlayerSnapshot Snapshot => _snapshot;

    private IEnumerable<Deck> AllDecks => [_deckA, _deckB, _spotDeck];

    private IEnumerable<Deck> MusicDecks => [_deckA, _deckB];

    private bool PausedByAdmin => _state.State.PausedByAdminAt is not null;

    /// <summary>Enfileira um comando; conclui quando o próximo ciclo o executar.</summary>
    public Task<PlayerCommandResult> ExecuteAsync(PlayerCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var pending = new PendingCommand(command, new TaskCompletionSource<PlayerCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        _commands.Enqueue(pending);
        return pending.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Um ciclo de controle. Devolve o tempo até o próximo ciclo (curto durante rampas).</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        RefreshManifest(now);
        await ProcessDeckEventsAsync(now, cancellationToken);
        await ProcessCommandsAsync(now, cancellationToken);
        var connected = await EnsureDecksAsync(now, cancellationToken);
        _window = _manifest is null ? null : ScheduleResolver.ActiveAt(_manifest.Schedules, _zone, now);
        EndAdminPauseOnNewWindow(now);
        _spots.Evaluate(BuildContext(now, _window));
        if (connected)
        {
            await RunPlaybackAsync(now, cancellationToken);
            await TryStartDuringSpotAsync(now, cancellationToken);
        }

        await UpdateSpotAsync(now, cancellationToken);
        await PollAndWatchAsync(now, cancellationToken);
        await ApplyVolumesAsync(now, cancellationToken);
        await FinishOutgoingAsync(now);
        if (now - _snapshotAt >= SnapshotInterval)
        {
            _snapshotAt = now;
            _snapshot = BuildSnapshot(now, connected);
        }

        return IsAnimating(now) ? FastTick : SlowTick;
    }

    /// <summary>Para os três mpv (encerramento do serviço).</summary>
    public async Task StopAsync()
    {
        foreach (var deck in AllDecks)
        {
            try
            {
                await deck.Mpv.StopAsync();
            }
            catch (Exception ex) when (ex is MpvCommandException or InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Falha ao parar o mpv {Deck}", deck.Label);
            }
        }
    }

    private void RefreshManifest(DateTimeOffset now)
    {
        if (!_manifestChanged)
        {
            return;
        }

        _manifestChanged = false;
        var manifest = _manifests.Active;
        if (manifest is null || ReferenceEquals(manifest, _manifest))
        {
            return;
        }

        var previous = _manifest;
        _manifest = manifest;
        _zone = ScheduleResolver.ResolveZone(manifest.Timezone, _logger);
        _spots.SetManifest(manifest, _zone);
        _logger.LogInformation(
            "Player usando o manifesto versão {Version} (mixagem {Crossfade} s, {Playlists} playlists)",
            manifest.Version, manifest.CrossfadeS, manifest.Playlists.Count);

        var state = _state.State;
        if (state.VolumeOverride is not null && state.VolumeOverrideBase != manifest.Volume)
        {
            // Volume da loja mudou no painel: o comando ao vivo anterior deixa de valer.
            state.VolumeOverride = null;
            state.VolumeOverrideBase = null;
            _state.Save();
        }

        SetStoreVolume(state.VolumeOverride ?? manifest.Volume, now, animate: previous is not null);
    }

    private void SetStoreVolume(int volume, DateTimeOffset now, bool animate)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == _storeVolume)
        {
            return;
        }

        _storeVolume = volume;
        var gain = MpvVolume.ToGain(volume);
        _storeGain = animate ? _storeGain.RampTo(gain, now, QuickTransition) : Envelope.Constant(gain);
    }

    private async Task ProcessDeckEventsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        while (_deckEvents.TryDequeue(out var e))
        {
            var deck = e.Deck;
            if (e.Name == DeckEvent.Crash)
            {
                await FailDeckAsync(deck, now, "processo terminou");
                continue;
            }

            if (deck == _spotDeck)
            {
                if (e.Name == "file-loaded" && _spot is { Phase: SpotPhase.Playing } loaded)
                {
                    loaded.LoadedAt = now;
                    await TryAsync(deck, async () => loaded.DurationS = await GetDurationAsync(deck.Mpv, cancellationToken), now);
                }
                else if (e.Name == "end-file" && _spot is { Phase: SpotPhase.Playing } spot && e.Reason is "eof" or "error")
                {
                    await FinishSpotAsync(SpotEndResult(spot, e.Reason, e.FileError, now), now);
                }

                continue;
            }

            switch (e.Name)
            {
                case "end-file" when deck.Track is null:
                    // Resto da fila de eventos de um mpv que já foi parado ou reiniciado.
                    break;
                case "end-file" when e.Reason == "error" && IsAudioOutputError(e.FileError):
                    await OnAudioOutputFailedAsync(deck, e.FileError, now);
                    break;
                case "start-file" when !deck.AwaitingLoad && deck.Appended is not null:
                    await CommitAppendedAsync(deck, now, cancellationToken);
                    break;
                case "file-loaded" when deck.AwaitingLoad:
                    await OnFileLoadedAsync(deck, now, cancellationToken);
                    break;
                case "end-file" when deck.AwaitingLoad:
                    if (e.Reason == "error")
                    {
                        await OnTrackErrorAsync(deck, e.FileError, now);
                    }

                    break;
                case "end-file" when e.Reason is "eof" or "error":
                    if (e.Reason == "error")
                    {
                        await OnTrackErrorAsync(deck, e.FileError, now);
                    }
                    else if (deck.Appended is null)
                    {
                        await OnTrackEndedAsync(deck, now);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private async Task ProcessCommandsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        while (_commands.TryDequeue(out var pending))
        {
            PlayerCommandResult result;
            try
            {
                result = await ExecuteCommandAsync(pending.Command, now, cancellationToken);
            }
            catch (MpvCommandException ex)
            {
                _logger.LogWarning(ex, "Comando {Command} falhou no mpv", pending.Command.Kind);
                result = PlayerCommandResult.Fail(ex.Message);
            }

            _logger.LogInformation("Comando {Command}: {Result}", pending.Command, result.Ok ? "ok" : result.Error);
            pending.Completion.TrySetResult(result);
        }
    }

    private async Task<PlayerCommandResult> ExecuteCommandAsync(PlayerCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = _state.State;
        switch (command.Kind)
        {
            case PlayerCommandKind.Play:
                if (state.PausedByAdminAt is not null)
                {
                    state.PausedByAdminAt = null;
                    _state.Save();
                }

                return PlayerCommandResult.Success;

            case PlayerCommandKind.Pause:
                if (state.PausedByAdminAt is null)
                {
                    state.PausedByAdminAt = now;
                    _state.Save();
                }

                return PlayerCommandResult.Success;

            case PlayerCommandKind.SetVolume when command.Volume is { } volume:
                state.VolumeOverride = Math.Clamp(volume, 0, 100);
                state.VolumeOverrideBase = _manifest?.Volume;
                _state.Save();
                SetStoreVolume(volume, now, animate: true);
                return PlayerCommandResult.Success;

            case PlayerCommandKind.Next:
                return await SkipAsync(now, cancellationToken);

            case PlayerCommandKind.PlayNow when command.Hash is { } hash:
                return await PlayNowAsync(hash, now, cancellationToken);

            case PlayerCommandKind.PlayOffer when command.OfferId is { } offerId:
                return _manifest is null
                    ? PlayerCommandResult.Fail("Sem manifesto ativo.")
                    : _spots.RequestNow(offerId, now) is { } error ? PlayerCommandResult.Fail(error) : PlayerCommandResult.Success;

            case PlayerCommandKind.TestSound:
                return await PlayTestSoundAsync(now, cancellationToken);

            default:
                return PlayerCommandResult.Fail("Comando incompleto.");
        }
    }

    private async Task<PlayerCommandResult> SkipAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_phase == Phase.BetweenSpot && _spot is not null)
        {
            await StopDeckAsync(_spotDeck, now);
            await FinishSpotAsync(SpotResult.Interrupted, now);
            _resumeAt = now;
            return PlayerCommandResult.Success;
        }

        if (_window is null || PausedByAdmin || _active?.Track is null)
        {
            return PlayerCommandResult.Fail("Nenhuma música tocando.");
        }

        if (_betweenPlanned is { } planned)
        {
            _spots.Requeue(planned);
            _betweenPlanned = null;
        }

        var choice = ChooseNext(_window);
        if (choice is null)
        {
            return PlayerCommandResult.Fail("Nenhuma outra faixa disponível.");
        }

        return await StartTrackAsync(choice.Item, choice.Cursor, now, QuickTransition, cancellationToken)
            ? PlayerCommandResult.Success
            : PlayerCommandResult.Fail("Player ocupado; tente de novo.");
    }

    private async Task<PlayerCommandResult> PlayNowAsync(string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_manifest is null || !_manifest.Tracks.TryGetValue(hash, out var info) || info.Kind != TrackKind.Music)
        {
            return PlayerCommandResult.Fail("Música não está no manifesto ativo.");
        }

        if (!_cache.Has(hash))
        {
            return PlayerCommandResult.Fail("Música ainda não está no cache.");
        }

        if (_window is null)
        {
            return PlayerCommandResult.Fail("Fora do horário da agenda.");
        }

        if (_state.State.PausedByAdminAt is not null)
        {
            _state.State.PausedByAdminAt = null;
            _state.Save();
            await ResumeFromAdminPauseAsync(now, cancellationToken);
        }

        if (_phase is Phase.BetweenSpot or Phase.AfterSpot)
        {
            return PlayerCommandResult.Fail("Oferta entre músicas tocando; tente de novo em instantes.");
        }

        if (_betweenPlanned is { } planned)
        {
            _spots.Requeue(planned);
            _betweenPlanned = null;
        }

        // "Tocar agora" não mexe no cursor: depois dela a fila segue de onde estava (seção 10.2, regra 7).
        var transition = _active?.Track is null ? (TimeSpan?)null : QuickTransition;
        return await StartTrackAsync(new QueueItem(null, null, hash), null, now, transition, cancellationToken)
            ? PlayerCommandResult.Success
            : PlayerCommandResult.Fail("Player ocupado; tente de novo.");
    }

    private async Task<PlayerCommandResult> PlayTestSoundAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_bluetooth.IsConnected)
        {
            return PlayerCommandResult.Fail("Caixa não conectada.");
        }

        if (!_spotDeck.IsUp(now))
        {
            // Loja 2 (28/10/2026): com o mpv sem abrir, "caixa não conectada" mandava procurar o defeito no lugar errado.
            return PlayerCommandResult.Fail("O mpv das ofertas não está aberto; veja os últimos registros.");
        }

        if (_spot is not null)
        {
            return PlayerCommandResult.Fail("Uma oferta está tocando.");
        }

        var path = TestSound.EnsureFile(_testSoundDirectory);
        await BeginSpotAsync(new ActiveSpot(null, path, "som de teste", 30, SpotMode.During), now, cancellationToken);
        return PlayerCommandResult.Success;
    }

    /// <summary>Inicia e reinicia os mpv; trata a caixa desconectada e reconectada (seção 9.5).</summary>
    private async Task<bool> EnsureDecksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_bluetooth.IsConnected || _bluetooth.SpeakerEndpointId is not { } endpoint)
        {
            if (!_bluetoothPaused)
            {
                await PauseForBluetoothAsync(now);
            }

            return false;
        }

        var reload = _reloadOutputs;
        _reloadOutputs = false;
        foreach (var deck in AllDecks)
        {
            if (deck.RestartAt is { } restartAt && now < restartAt)
            {
                continue;
            }

            try
            {
                if (!deck.Mpv.IsRunning)
                {
                    await deck.Mpv.StartAsync(endpoint, 0, cancellationToken);
                    deck.ResetAfterStart(endpoint);
                    _logger.LogInformation("mpv {Deck} pronto", deck.Label);
                }
                else if (reload || deck.Endpoint != endpoint)
                {
                    await deck.Mpv.ReloadAudioOutputAsync(endpoint, cancellationToken);
                    deck.Endpoint = endpoint;
                    _logger.LogInformation("mpv {Deck}: saída de áudio reaberta na caixa", deck.Label);
                }
            }
            catch (Exception ex) when (ex is MpvCommandException or InvalidOperationException or IOException or TimeoutException
                or System.ComponentModel.Win32Exception)
            {
                if (deck.Mpv.IsRunning)
                {
                    // Vivo mas sem reabrir a saída (PC piloto, 27/10/2026: travou e ficou mudo até reiniciar o serviço).
                    _logger.LogError(ex, "mpv {Deck} não reabriu a saída de áudio", deck.Label);
                    await FailDeckAsync(deck, now, "não reabriu a saída de áudio");
                    continue;
                }

                _logger.LogError(ex, "Falha ao iniciar o mpv {Deck}; nova tentativa em 5 s", deck.Label);
                deck.RestartAt = now + TimeSpan.FromSeconds(5);
            }
        }

        if (_bluetoothPaused)
        {
            _bluetoothPaused = false;
            if (MusicDecks.Any(d => d.Track is not null) || _resume is not null)
            {
                _logger.LogInformation("Caixa conectada; retomando a música");
            }
            if (!PausedByAdmin)
            {
                await UnpauseMusicAsync(now, cancellationToken);
            }
        }

        return true;
    }

    private async Task PauseForBluetoothAsync(DateTimeOffset now)
    {
        _bluetoothPaused = true;
        if (_spot is not null)
        {
            // Caixa caiu durante a oferta: interrompida e não repetida (seção 10.3.7).
            await StopDeckAsync(_spotDeck, now);
            await FinishSpotAsync(SpotResult.Interrupted, now);
        }

        await CompleteCrossfadeAsync(now);
        foreach (var deck in MusicDecks.Where(d => d.Track is not null && !d.MpvPaused && d.IsUp(now)))
        {
            await PauseDeckAsync(deck, now);
        }

        _pauseFade = Envelope.Constant(0);
        if (_phase is Phase.BetweenSpot or Phase.AfterSpot)
        {
            _phase = _active?.Track is null ? Phase.Idle : Phase.Playing;
        }

        if (_active?.Track is { } track)
        {
            _logger.LogWarning("Caixa desconectada; música pausada em {Position:0.0} s de {Title}", _active.EstimatedPosition(now), TitleOf(track.Item.Hash));
        }
    }

    private async Task RunPlaybackAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_manifest is null || _window is not { } window)
        {
            if (_active is not null || _outgoing is not null || _phase != Phase.Idle || _resume is not null)
            {
                await StopMusicAsync(now, _manifest is null ? "sem manifesto" : "fora do horário da agenda");
            }

            SetIdleReason(_manifest is null ? "Sem manifesto ativo" : "Fora do horário da agenda");
            return;
        }

        if (!window.ContinuesAfterEnd && now >= window.End - WindowEndFade && _fadingWindowEnd != window.End)
        {
            // Fim do horário: fade-out de 3 s terminando exatamente no fim da janela (seção 10.2, regra 3).
            _fadingWindowEnd = window.End;
            _windowFade = _windowFade.RampTo(0, now, window.End - now);
            _logger.LogInformation("Fim do horário às {End:HH:mm}: fade-out", TimeZoneInfo.ConvertTime(window.End, _zone));
        }

        if (PausedByAdmin)
        {
            await ApplyAdminPauseAsync(now);
            SetIdleReason("Pausado pelo administrador");
            return;
        }

        if (_adminPauseStarted)
        {
            await ResumeFromAdminPauseAsync(now, cancellationToken);
        }

        switch (_phase)
        {
            case Phase.Idle:
                await StartFromIdleAsync(window, now, cancellationToken);
                break;
            case Phase.Playing:
                await AdvancePlayingAsync(window, now, cancellationToken);
                break;
            case Phase.AfterSpot when now >= _resumeAt:
                _phase = Phase.Idle;
                await StartFromIdleAsync(window, now, cancellationToken);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Oferta "durante": com música tocando, só enquanto uma faixa está em andamento; fora da agenda ou com a
    /// música pausada, o <see cref="SpotScheduler"/> só libera o comando "tocar oferta agora".
    /// </summary>
    private async Task TryStartDuringSpotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var musicOn = _window is not null && !PausedByAdmin && _manifest is not null;
        if (_spot is not null || (musicOn && _phase != Phase.Playing))
        {
            return;
        }

        if (_spots.TakeDuring(BuildContext(now, _window)) is { } during)
        {
            await BeginSpotAsync(ActiveSpot.From(during), now, cancellationToken);
        }
    }

    /// <summary>Libera o mpv que saiu da mixagem assim que o volume dele chega a zero.</summary>
    private async Task FinishOutgoingAsync(DateTimeOffset now)
    {
        if (_outgoing is { } outgoing && outgoing.Gain.IsFinished(now) && outgoing.Gain.To <= 0)
        {
            _outgoing = null;
            await StopDeckAsync(outgoing, now);
        }
    }

    private async Task StartFromIdleAsync(ScheduleWindow window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now < _retryAt)
        {
            return;
        }

        if (!window.ContinuesAfterEnd && window.End - now < MinStartBeforeWindowEnd)
        {
            SetIdleReason("Horário terminando");
            return;
        }

        if (_resume is { } resume)
        {
            var deck = PickIdleDeck(now);
            if (deck is null)
            {
                return;
            }

            _resume = null;
            _logger.LogInformation("Retomando {Title} em {Position:0.0} s no mpv {Deck}", TitleOf(resume.Track.Item.Hash), resume.PositionS, deck.Label);
            await LoadAsync(deck, resume.Track, now, resume.PositionS > 1 ? resume.PositionS : null, QuickTransition, cancellationToken);
            _active = deck;
            _phase = Phase.Playing;
            _lastIdleReason = null;
            _events.Publish(new AgentEventMessage(AgentEvents.PlayerRestarted));
            return;
        }

        var choice = ChooseNext(window);
        if (choice is null)
        {
            SetIdleReason("Nenhuma música disponível no cache para a playlist " + window.PlaylistId);
            return;
        }

        await StartTrackAsync(choice.Item, choice.Cursor, now, null, cancellationToken);
    }

    private async Task AdvancePlayingAsync(ScheduleWindow window, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deck = _active;
        if (deck?.Track is not { } track)
        {
            _phase = Phase.Idle;
            return;
        }

        if (deck.AwaitingLoad || deck.EstimatedPosition(now) is not { } position || deck.Duration is not { } duration || duration <= 0)
        {
            return;
        }

        var remaining = duration - position;
        var crossfade = CrossfadeFor(duration);
        if (!_transitionDecided && remaining <= Math.Max(crossfade.TotalSeconds, PreloadLead.TotalSeconds) + 0.5)
        {
            _transitionDecided = true;
            _betweenPlanned = _spots.TakeBetween(BuildContext(now, window));
            if (_betweenPlanned is { } spot)
            {
                // Oferta entre músicas: a música termina, a oferta toca e não há mixagem nesta transição (seção 10.2.1, item 4a).
                _logger.LogInformation("Oferta '{Offer}' tocará ao fim de {Title}; sem mixagem nesta transição", spot.Offer.Name, TitleOf(track.Item.Hash));
            }
        }

        var windowEndsFirst = !window.ContinuesAfterEnd && window.End <= now + TimeSpan.FromSeconds(remaining);
        if (_betweenPlanned is not null || windowEndsFirst || _outgoing is not null)
        {
            return;
        }

        if (crossfade > TimeSpan.Zero)
        {
            if (remaining <= crossfade.TotalSeconds && ChooseNext(window) is { } next)
            {
                await StartTrackAsync(next.Item, next.Cursor, now, crossfade, cancellationToken);
            }
        }
        else if (remaining <= PreloadLead.TotalSeconds && deck.Appended is null && ChooseNext(window) is { } next)
        {
            // Mixagem desligada: a próxima faixa entra na lista do mesmo mpv e emenda sem pausa (seção 10.1).
            var appended = new LoadedTrack(next.Item, _cache.PathFor(next.Item.Hash), InfoOf(next.Item.Hash), next.Cursor);
            await deck.Mpv.LoadFileAsync(appended.Path, append: true, cancellationToken);
            deck.Appended = appended;
        }
    }

    /// <summary>Começa uma faixa. Com <paramref name="transition"/>, mistura com a atual (mixagem ou "pular" em 1 s).</summary>
    private async Task<bool> StartTrackAsync(QueueItem item, PlaylistCursorState? cursor, DateTimeOffset now, TimeSpan? transition, CancellationToken cancellationToken)
    {
        if (_outgoing is not null && transition is not null)
        {
            await CompleteCrossfadeAsync(now);
        }

        var deck = PickIdleDeck(now);
        if (deck is null)
        {
            // Uma vez só: com os dois mpv sem abrir, isto se repetia a cada 250 ms no log.
            SetIdleReason("Nenhum mpv de música aberto; tentando de novo");
            return false;
        }

        var track = new LoadedTrack(item, _cache.PathFor(item.Hash), InfoOf(item.Hash), cursor);
        var old = _active?.Track is not null ? _active : null;
        if (transition is { } duration && old is not null)
        {
            deck.Gain = Envelope.Ramp(0, 1, now, duration, EnvelopeCurve.EqualPowerIn);
            old.Gain = old.Gain.RampTo(0, now, duration, EnvelopeCurve.EqualPowerOut);
            if (old.Appended is not null)
            {
                old.Appended = null;
                await TryAsync(old, () => old.Mpv.CommandAsync(["playlist-clear"], cancellationToken), now);
            }

            _outgoing = old;
        }
        else
        {
            deck.Gain = Envelope.Constant(1);
            if (old is not null)
            {
                await StopDeckAsync(old, now);
            }
        }

        await LoadAsync(deck, track, now, null, null, cancellationToken);
        _active = deck;
        _phase = Phase.Playing;
        OnTrackStarted(track, now);
        _logger.LogInformation(
            "Tocando {Title} ({Playlist}) no mpv {Deck}{Mix}",
            TitleOf(item.Hash), item.PlaylistId ?? "tocar agora", deck.Label,
            transition is { } t && old is not null ? $", mixagem de {t.TotalSeconds:0.#} s" : string.Empty);
        return true;
    }

    private async Task LoadAsync(Deck deck, LoadedTrack track, DateTimeOffset now, double? seekS, TimeSpan? fadeIn, CancellationToken cancellationToken)
    {
        deck.Track = track;
        deck.Appended = null;
        deck.AwaitingLoad = true;
        deck.LoadStartedAt = now;
        deck.SeekOnLoad = seekS;
        deck.FadeInOnLoad = fadeIn;
        deck.Position = seekS ?? 0;
        deck.Duration = track.Info is { DurationMs: > 0 } info ? info.DurationMs / 1000.0 : null;
        deck.PolledAt = now;
        deck.StallSince = now;
        deck.StallPosition = null;
        if (fadeIn is not null)
        {
            deck.Gain = Envelope.Constant(0);
        }

        if (deck.MpvPaused)
        {
            await deck.Mpv.SetPausedAsync(false, cancellationToken);
            deck.MpvPaused = false;
        }

        await SendVolumeAsync(deck, MusicVolume(deck, now), force: true, cancellationToken);
        await deck.Mpv.LoadFileAsync(track.Path, append: false, cancellationToken);
    }

    private async Task OnFileLoadedAsync(Deck deck, DateTimeOffset now, CancellationToken cancellationToken)
    {
        deck.AwaitingLoad = false;
        if (deck.SeekOnLoad is { } seek)
        {
            deck.SeekOnLoad = null;
            await TryAsync(deck, () => deck.Mpv.SeekAsync(seek, cancellationToken), now);
            deck.Position = seek;
            deck.PolledAt = now;
        }

        if (deck.FadeInOnLoad is { } fade)
        {
            deck.FadeInOnLoad = null;
            deck.Gain = Envelope.Ramp(0, 1, now, fade);
        }
    }

    private async Task CommitAppendedAsync(Deck deck, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var next = deck.Appended!;
        deck.Appended = null;
        deck.Track = next;
        deck.Position = 0;
        deck.Duration = next.Info is { DurationMs: > 0 } info ? info.DurationMs / 1000.0 : null;
        deck.PolledAt = now;
        deck.StallSince = now;
        OnTrackStarted(next, now);
        _logger.LogInformation("Tocando {Title} ({Playlist}) no mpv {Deck}, emendada sem pausa", TitleOf(next.Item.Hash), next.Item.PlaylistId, deck.Label);
        await TryAsync(deck, () => deck.Mpv.CommandAsync(["playlist-clear"], cancellationToken), now);
    }

    private void OnTrackStarted(LoadedTrack track, DateTimeOffset now)
    {
        _trackInstance++;
        _lastIdleReason = null;
        _transitionDecided = false;
        _betweenPlanned = null;
        _consecutiveErrors = 0;
        if (track.Cursor is { } cursor && track.Item.PlaylistId is { } playlistId)
        {
            // Continuidade (seção 10.2, regra 5): a posição salva passa a ser a da faixa que começou.
            _state.State.Playlists[playlistId] = cursor;
        }

        _spots.OnTrackStarted(track.Item);
        _state.Save();
        _events.Publish(new TrackStartedMessage(track.Item.Hash, now));
    }

    private async Task OnTrackEndedAsync(Deck deck, DateTimeOffset now)
    {
        deck.ClearTrack();
        if (deck == _outgoing)
        {
            _outgoing = null;
            return;
        }

        if (deck != _active)
        {
            return;
        }

        _active = null;
        _phase = Phase.Idle;
        if (_betweenPlanned is { } spot)
        {
            _betweenPlanned = null;
            await BeginSpotAsync(ActiveSpot.From(spot), now, CancellationToken.None);
        }
    }

    private async Task OnTrackErrorAsync(Deck deck, string? error, DateTimeOffset now)
    {
        var hash = deck.Track?.Item.Hash;
        _logger.LogError("mpv {Deck} não conseguiu tocar {Title} ({Hash}): {Error}", deck.Label, hash is null ? "?" : TitleOf(hash), hash, error ?? "sem detalhe");
        if (hash is not null)
        {
            _ = VerifyInBackgroundAsync(hash);
        }

        if (++_consecutiveErrors >= 5)
        {
            _logger.LogError("Várias faixas seguidas com erro; nova tentativa em {Delay}", ErrorBackoff);
            _retryAt = now + ErrorBackoff;
            _consecutiveErrors = 0;
        }

        deck.AwaitingLoad = false;
        await OnTrackEndedAsync(deck, now);
    }

    /// <summary>
    /// A saída de áudio não abriu (endpoint da caixa desativado ou sumindo): a faixa não tem culpa. Em vez de pular para a
    /// próxima, o mpv reinicia e a mesma faixa continua do ponto onde parou, com espera crescente entre as tentativas
    /// (PC piloto, 27/10/2026: 021, 022 e 023 puladas em 1 s enquanto o Windows desativava a caixa).
    /// </summary>
    private async Task OnAudioOutputFailedAsync(Deck deck, string? error, DateTimeOffset now)
    {
        var delay = AudioOutputRetry[Math.Min(_audioOutputFailures, AudioOutputRetry.Length - 1)];
        _audioOutputFailures++;
        _retryAt = now + delay;
        await FailDeckAsync(deck, now, $"a saída de áudio não abriu ({error}); a música continua do mesmo ponto daqui a {delay.TotalSeconds:0} s");
    }

    private static bool IsAudioOutputError(string? error) =>
        error is not null && error.Contains("audio output", StringComparison.OrdinalIgnoreCase);

    private async Task VerifyInBackgroundAsync(string hash)
    {
        try
        {
            await _cache.VerifyAsync(hash, CancellationToken.None);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Falha ao conferir {Hash} no cache", hash);
        }
    }

    private async Task BeginSpotAsync(ActiveSpot spot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _spot = spot;
        if (spot.Spot is { } toPlay)
        {
            _spots.OnSpotStarted(toPlay, now);
        }

        var musicAudible = _active?.Track is not null && !PausedByAdmin && !_bluetoothPaused;
        if (spot.Mode == SpotMode.During && musicAudible)
        {
            // Ducking (seção 10.3.3): música desce ao nível da oferta em 800 ms e só então a oferta começa.
            spot.Phase = SpotPhase.DuckingDown;
            spot.PhaseStartedAt = now;
            _duck = _duck.RampTo(Math.Clamp(spot.DuckLevel, 0, 100) / 100.0, now, DuckDown);
            return;
        }

        if (spot.Mode == SpotMode.Between)
        {
            _phase = Phase.BetweenSpot;
        }

        await PlaySpotFileAsync(spot, now, cancellationToken);
    }

    private async Task UpdateSpotAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_spot is not { } spot)
        {
            return;
        }

        if (spot.Phase == SpotPhase.DuckingDown && now >= spot.PhaseStartedAt + DuckDown)
        {
            await PlaySpotFileAsync(spot, now, cancellationToken);
        }
        else if (spot.Phase == SpotPhase.Playing && now - spot.PhaseStartedAt > MaxSpotDuration)
        {
            _logger.LogWarning("Oferta não terminou em {Max}; interrompendo", MaxSpotDuration);
            await StopDeckAsync(_spotDeck, now);
            await FinishSpotAsync(SpotResult.Interrupted, now);
        }
    }

    private async Task PlaySpotFileAsync(ActiveSpot spot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        spot.Phase = SpotPhase.Playing;
        spot.PhaseStartedAt = now;
        if (!_spotDeck.IsUp(now))
        {
            _logger.LogWarning("mpv de ofertas indisponível; '{Spot}' interrompida", spot.Label);
            await FinishSpotAsync(SpotResult.Interrupted, now);
            return;
        }

        var ok = await TryAsync(_spotDeck, async () =>
        {
            await SendVolumeAsync(_spotDeck, SpotVolume(now), force: true, cancellationToken);
            await _spotDeck.Mpv.LoadFileAsync(spot.Path, append: false, cancellationToken);
        }, now);
        if (!ok && _spot == spot)
        {
            await FinishSpotAsync(SpotResult.Interrupted, now);
        }
    }

    /// <summary>
    /// O mpv também responde eof quando a saída de áudio cai no meio do arquivo (teste no PC piloto, 23/10/2026: oferta de
    /// "entre músicas" cortada logo após o Windows trocar a saída padrão). Oferta que termina bem antes da duração conta
    /// como interrompida, e o log diz quanto tocou.
    /// </summary>
    private SpotResult SpotEndResult(ActiveSpot spot, string? reason, string? error, DateTimeOffset now)
    {
        if (reason == "error")
        {
            _logger.LogWarning("mpv de ofertas não conseguiu tocar {Path}: {Error}", spot.Path, error ?? "sem detalhe");
            return SpotResult.Interrupted;
        }

        if (spot.LoadedAt is { } loadedAt && spot.DurationS is { } duration && (now - loadedAt).TotalSeconds < duration - SpotEarlyEndTolerance.TotalSeconds)
        {
            _logger.LogWarning(
                "Oferta '{Spot}' terminou antes do fim: tocou {Played:F1} s de {Duration:F1} s (a saída de áudio pode ter caído)",
                spot.Label, (now - loadedAt).TotalSeconds, duration);
            return SpotResult.Interrupted;
        }

        return SpotResult.Played;
    }

    private Task FinishSpotAsync(SpotResult result, DateTimeOffset now)
    {
        if (_spot is not { } spot)
        {
            return Task.CompletedTask;
        }

        _spot = null;
        if (spot.Spot is { } toPlay)
        {
            _spots.OnSpotFinished(toPlay, result, now);
        }
        else
        {
            _logger.LogInformation("Som de teste terminou: {Result}", result);
        }

        if (_duck.To < 1 || _duck.IsAnimating(now))
        {
            _duck = _duck.RampTo(1, now, DuckRestore);
        }

        if (_phase == Phase.BetweenSpot)
        {
            _phase = Phase.AfterSpot;
            _resumeAt = now + BetweenSpotGap;
        }

        return Task.CompletedTask;
    }

    private async Task ApplyAdminPauseAsync(DateTimeOffset now)
    {
        if (!_adminPauseStarted)
        {
            _adminPauseStarted = true;
            _pauseFade = _pauseFade.RampTo(0, now, QuickTransition);
            if (_spot is { Spot.Request.Priority: not SpotPriority.Command })
            {
                await StopDeckAsync(_spotDeck, now);
                await FinishSpotAsync(SpotResult.Interrupted, now);
            }

            _logger.LogInformation("Música pausada pelo administrador");
        }

        if (!_adminPauseApplied && _pauseFade.IsFinished(now))
        {
            _adminPauseApplied = true;
            await CompleteCrossfadeAsync(now);
            foreach (var deck in MusicDecks.Where(d => d.Track is not null && !d.MpvPaused && d.IsUp(now)))
            {
                await PauseDeckAsync(deck, now);
            }

            if (_phase is Phase.BetweenSpot or Phase.AfterSpot)
            {
                _phase = _active?.Track is null ? Phase.Idle : Phase.Playing;
            }
        }
    }

    private async Task ResumeFromAdminPauseAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_adminPauseStarted)
        {
            return;
        }

        _adminPauseStarted = false;
        _adminPauseApplied = false;
        _logger.LogInformation("Música retomada");
        await UnpauseMusicAsync(now, cancellationToken);
    }

    private async Task UnpauseMusicAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var deck in MusicDecks.Where(d => d.MpvPaused && d.IsUp(now)))
        {
            await TryAsync(deck, () => deck.Mpv.SetPausedAsync(false, cancellationToken), now);
            deck.MpvPaused = false;
            deck.PolledAt = now;
            deck.StallSince = now;
        }

        _pauseFade = _pauseFade.RampTo(1, now, QuickTransition);
    }

    private void EndAdminPauseOnNewWindow(DateTimeOffset now)
    {
        if (_manifest is null || _state.State.PausedByAdminAt is not { } pausedAt)
        {
            return;
        }

        // A pausa do administrador vale até "tocar" ou até a próxima janela de agenda começar (seção 10.2, regra 8).
        if (ScheduleResolver.AnyStartBetween(_manifest.Schedules, _zone, pausedAt, now))
        {
            _logger.LogInformation("Nova janela de agenda: pausa do administrador encerrada");
            _state.State.PausedByAdminAt = null;
            _state.Save();
        }
    }

    private async Task StopMusicAsync(DateTimeOffset now, string reason)
    {
        _logger.LogInformation("Música parada: {Reason}", reason);
        if (_spot is { Spot.Request.Priority: not SpotPriority.Command })
        {
            await StopDeckAsync(_spotDeck, now);
            await FinishSpotAsync(SpotResult.Interrupted, now);
        }

        foreach (var deck in MusicDecks.Where(d => d.Track is not null))
        {
            await StopDeckAsync(deck, now);
        }

        if (_betweenPlanned is { } planned)
        {
            _spots.Requeue(planned);
        }

        _active = null;
        _outgoing = null;
        _resume = null;
        _betweenPlanned = null;
        _transitionDecided = false;
        _phase = Phase.Idle;
        _windowFade = Envelope.Constant(1);
        _duck = Envelope.Constant(1);
    }

    /// <summary>Encerra uma mixagem em andamento de imediato (pausa, caixa caiu, novo "pular").</summary>
    private async Task CompleteCrossfadeAsync(DateTimeOffset now)
    {
        if (_outgoing is { } outgoing)
        {
            _outgoing = null;
            await StopDeckAsync(outgoing, now);
            if (_active is { } active)
            {
                active.Gain = active.Gain.RampTo(1, now, TimeSpan.Zero);
            }
        }
    }

    /// <summary>Processo morto, travado ou sem resposta: reinicia em 2 s e a faixa continua no outro mpv (seção 10.1 e 10.2.1, item 9).</summary>
    private async Task FailDeckAsync(Deck deck, DateTimeOffset now, string reason)
    {
        if (deck.RestartAt is not null && !deck.Mpv.IsRunning)
        {
            return;
        }

        var position = deck.EstimatedPosition(now);
        var track = deck.Track;
        _logger.LogWarning("mpv {Deck}: {Reason}; reiniciando em {Delay}", deck.Label, reason, RestartDelay);
        try
        {
            await deck.Mpv.StopAsync();
        }
        catch (Exception ex) when (ex is MpvCommandException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "Falha ao encerrar o mpv {Deck}", deck.Label);
        }

        deck.MarkFailed(now + RestartDelay);
        if (deck == _spotDeck)
        {
            await FinishSpotAsync(SpotResult.Interrupted, now);
            return;
        }

        if (deck == _outgoing)
        {
            _outgoing = null;
            if (_active is { } active)
            {
                active.Gain = active.Gain.RampTo(1, now, QuickTransition);
            }

            return;
        }

        if (deck != _active || track is null)
        {
            return;
        }

        _active = null;
        var other = deck == _deckA ? _deckB : _deckA;
        if (_outgoing == other && other.IsUp(now))
        {
            // Morreu o mpv da faixa que entrava na mixagem: ela continua no outro, no lugar da que saía.
            _outgoing = null;
            _logger.LogInformation("Faixa {Title} continua no mpv {Deck}", TitleOf(track.Item.Hash), other.Label);
            await LoadAsync(other, track, now, position is > 1 ? position : null, QuickTransition, CancellationToken.None);
            _active = other;
            return;
        }

        _resume = new ResumePoint(track, position ?? 0);
        _phase = Phase.Idle;
    }

    private async Task PollAndWatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var deck in MusicDecks)
        {
            if (deck.Track is not null && deck.AwaitingLoad && now - deck.LoadStartedAt > LoadTimeout && deck.IsUp(now))
            {
                await FailDeckAsync(deck, now, $"arquivo não carregou em {LoadTimeout.TotalSeconds:0} s");
                continue;
            }

            if (deck.Track is null || deck.AwaitingLoad || deck.MpvPaused || !deck.IsUp(now) || now - deck.PolledAt < PollInterval)
            {
                continue;
            }

            double? position = null;
            double? duration = null;
            var ok = await TryAsync(deck, async () =>
            {
                position = await deck.Mpv.GetPositionAsync(cancellationToken);
                duration = await GetDurationAsync(deck.Mpv, cancellationToken);
            }, now);
            if (!ok)
            {
                continue;
            }

            deck.PolledAt = now;
            if (position is { } p)
            {
                deck.Position = p;
            }

            if (duration is > 0)
            {
                deck.Duration = duration;
            }

            if (position is null || deck.StallPosition is null || Math.Abs(position.Value - deck.StallPosition.Value) > 0.01)
            {
                deck.StallPosition = position;
                deck.StallSince = now;
                if (position is not null && now - deck.LoadStartedAt >= AudioOutputHealthy)
                {
                    _audioOutputFailures = 0;
                }
            }
            else if (now - deck.StallSince >= StallLimit)
            {
                await FailDeckAsync(deck, now, $"posição parada em {position:0.0} s há {StallLimit.TotalSeconds:0} s");
            }
        }
    }

    private async Task ApplyVolumesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Seção 9.5: reaplica o volume a cada 10 s, desfazendo alterações externas.
        var force = now - _lastReapply >= VolumeReapply;
        if (force)
        {
            _lastReapply = now;
        }

        foreach (var deck in MusicDecks.Where(d => d.IsUp(now)))
        {
            await SendVolumeAsync(deck, MusicVolume(deck, now), force, cancellationToken);
        }

        if (_spotDeck.IsUp(now))
        {
            await SendVolumeAsync(_spotDeck, SpotVolume(now), force, cancellationToken);
        }
    }

    private double MusicVolume(Deck deck, DateTimeOffset now) => MpvVolume.FromGain(
        _storeGain.ValueAt(now) * deck.Gain.ValueAt(now) * _duck.ValueAt(now) * _windowFade.ValueAt(now) * _pauseFade.ValueAt(now));

    private double SpotVolume(DateTimeOffset now) => MpvVolume.FromGain(_storeGain.ValueAt(now) * _windowFade.ValueAt(now));

    private async Task SendVolumeAsync(Deck deck, double volume, bool force, CancellationToken cancellationToken)
    {
        volume = Math.Round(Math.Clamp(volume, 0, 100), 2);
        if (!force && Math.Abs(volume - deck.SentVolume) < 0.05)
        {
            return;
        }

        if (await TryAsync(deck, () => deck.Mpv.SetVolumeAsync(volume, cancellationToken), _time.GetUtcNow()))
        {
            deck.SentVolume = volume;
        }
    }

    private async Task PauseDeckAsync(Deck deck, DateTimeOffset now)
    {
        deck.Position = deck.EstimatedPosition(now);
        deck.PolledAt = now;
        await TryAsync(deck, () => deck.Mpv.SetPausedAsync(true, CancellationToken.None), now);
        deck.MpvPaused = true;
    }

    private async Task StopDeckAsync(Deck deck, DateTimeOffset now)
    {
        deck.ClearTrack();
        if (deck.IsUp(now))
        {
            await TryAsync(deck, () => deck.Mpv.CommandAsync(["stop"], CancellationToken.None), now);
        }
    }

    /// <summary>Executa um comando no mpv; falha de IPC é tratada como processo com defeito (reinício pelo vigia).</summary>
    private async Task<bool> TryAsync(Deck deck, Func<Task> action, DateTimeOffset now)
    {
        try
        {
            await action();
            return true;
        }
        catch (MpvCommandException ex)
        {
            _logger.LogWarning(ex, "Comando no mpv {Deck} falhou", deck.Label);
            if (!deck.Mpv.IsRunning || ex.TimedOut)
            {
                // Travado com o processo vivo também reinicia: senão cada ciclo espera o tempo esgotar e o som não volta.
                await FailDeckAsync(deck, now, ex.TimedOut ? "travado, sem resposta" : "sem resposta");
            }

            return false;
        }
    }

    private static async Task<double?> GetDurationAsync(IMpvInstance mpv, CancellationToken cancellationToken)
    {
        try
        {
            var value = await mpv.GetPropertyAsync("duration", cancellationToken);
            return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        }
        catch (MpvCommandException ex) when (!ex.TimedOut)
        {
            return null;
        }
    }

    private Deck? PickIdleDeck(DateTimeOffset now) =>
        MusicDecks.FirstOrDefault(d => d.IsUp(now) && d.Track is null && d != _active && d != _outgoing);

    private QueueChoice? ChooseNext(ScheduleWindow window)
    {
        if (_manifest is null || !_manifest.Playlists.TryGetValue(window.PlaylistId, out var playlist))
        {
            return null;
        }

        _state.State.Playlists.TryGetValue(window.PlaylistId, out var cursor);
        return _order.Peek(window.PlaylistId, playlist, cursor, IsPlayable);
    }

    private bool IsPlayable(string hash) =>
        _manifest is not null && _manifest.Tracks.TryGetValue(hash, out var info) && info.Kind == TrackKind.Music && _cache.Has(hash);

    /// <summary>Duração da mixagem para uma faixa: <c>crossfadeS</c>, ou um terço da faixa se ela for mais curta que 3× (seção 10.2.1, item 3).</summary>
    private TimeSpan CrossfadeFor(double durationS)
    {
        var configured = Math.Max(0, _manifest?.CrossfadeS ?? 0);
        if (configured == 0)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(durationS < 3 * configured ? durationS / 3 : configured);
    }

    private PlaybackContext BuildContext(DateTimeOffset now, ScheduleWindow? window) => new()
    {
        Now = now,
        InWindow = window is not null,
        WindowPlaylistId = window?.PlaylistId,
        PausedByAdmin = PausedByAdmin,
        SpeakerConnected = _bluetooth.IsConnected && !_bluetoothPaused,
        SpotPlaying = _spot is not null,
        Current = _active?.Track?.Item,
        TrackInstance = _trackInstance,
        PositionS = _active?.EstimatedPosition(now),
        DurationS = _active?.Duration,
    };

    private bool IsAnimating(DateTimeOffset now) =>
        _storeGain.IsAnimating(now) || _duck.IsAnimating(now) || _windowFade.IsAnimating(now) || _pauseFade.IsAnimating(now)
        || MusicDecks.Any(d => d.Gain.IsAnimating(now)) || _spot is { Phase: SpotPhase.DuckingDown } || _phase == Phase.AfterSpot;

    private PlayerSnapshot BuildSnapshot(DateTimeOffset now, bool connected)
    {
        var track = _active?.Track;
        var upcoming = new List<QueueItem>();
        if (_window is { } window && _manifest is not null && _manifest.Playlists.TryGetValue(window.PlaylistId, out var playlist))
        {
            _state.State.Playlists.TryGetValue(window.PlaylistId, out var cursor);
            upcoming.AddRange(_order.Upcoming(window.PlaylistId, playlist, cursor, IsPlayable, 10));
        }

        var status = track is null ? PlayerStatus.Stopped
            : PausedByAdmin || !connected ? PlayerStatus.Paused
            : PlayerStatus.Playing;
        return new PlayerSnapshot
        {
            Status = status,
            Reason = status == PlayerStatus.Playing ? null : _lastIdleReason ?? (!connected ? "Caixa desconectada" : null),
            ManifestVersion = _manifest?.Version,
            NowPlayingHash = track?.Item.Hash,
            Title = track?.Info?.Title,
            Artist = track?.Info?.Artist,
            PlaylistId = track?.Item.PlaylistId,
            PositionS = _active?.EstimatedPosition(now),
            DurationS = _active?.Duration,
            Volume = Math.Max(0, _storeVolume),
            Crossfading = _outgoing is not null,
            WindowPlaylistId = _window?.PlaylistId,
            WindowEnd = _window?.End,
            PausedByAdmin = PausedByAdmin,
            SpeakerConnected = connected,
            Spot = _spot?.Label,
            QueuedSpots = _spots.Queue.Select(q => $"{q.OfferId} ({q.Priority}, {q.Mode})").ToList(),
            Upcoming = upcoming.Select(i => TitleOf(i.Hash)).ToList(),
            UpcomingHashes = upcoming.Select(i => i.Hash).ToList(),
        };
    }

    private void SetIdleReason(string reason)
    {
        if (_lastIdleReason != reason)
        {
            _lastIdleReason = reason;
            _logger.LogInformation("Player: {Reason}", reason);
        }
    }

    private TrackInfo? InfoOf(string hash) => _manifest?.Tracks.GetValueOrDefault(hash);

    private string TitleOf(string hash) => InfoOf(hash) is { } info
        ? info.Artist is { Length: > 0 } artist ? $"{artist} - {info.Title}" : info.Title
        : hash[..Math.Min(12, hash.Length)];

    private sealed record DeckEvent(Deck Deck, string Name, string? Reason, string? FileError = null)
    {
        public const string Crash = "__crash";
    }

    private sealed record PendingCommand(PlayerCommand Command, TaskCompletionSource<PlayerCommandResult> Completion);

    private sealed record LoadedTrack(QueueItem Item, string Path, TrackInfo? Info, PlaylistCursorState? Cursor);

    private sealed record ResumePoint(LoadedTrack Track, double PositionS);

    private sealed class ActiveSpot(SpotToPlay? spot, string path, string label, int duckLevel, SpotMode mode)
    {
        public SpotToPlay? Spot { get; } = spot;

        public string Path { get; } = path;

        public string Label { get; } = label;

        public int DuckLevel { get; } = duckLevel;

        public SpotMode Mode { get; } = mode;

        public SpotPhase Phase { get; set; }

        public DateTimeOffset PhaseStartedAt { get; set; }

        /// <summary>Quando o mpv carregou o arquivo e a duração que informou (para notar fim antecipado).</summary>
        public DateTimeOffset? LoadedAt { get; set; }

        public double? DurationS { get; set; }

        public static ActiveSpot From(SpotToPlay spot) =>
            new(spot, spot.Path, spot.Offer.Name, spot.Offer.DuckLevel, spot.Request.Mode);
    }

    private sealed class Deck(IMpvInstance mpv, string label)
    {
        public IMpvInstance Mpv { get; } = mpv;

        public string Label { get; } = label;

        public string? Endpoint { get; set; }

        public LoadedTrack? Track { get; set; }

        /// <summary>Faixa seguinte já na lista do mpv (mixagem desligada).</summary>
        public LoadedTrack? Appended { get; set; }

        /// <summary>Ganho da mixagem deste mpv. Sem faixa carregada fica em zero.</summary>
        public Envelope Gain { get; set; } = Envelope.Constant(0);

        public bool AwaitingLoad { get; set; }

        public DateTimeOffset LoadStartedAt { get; set; }

        public double? SeekOnLoad { get; set; }

        public TimeSpan? FadeInOnLoad { get; set; }

        public double? Position { get; set; }

        public double? Duration { get; set; }

        public DateTimeOffset PolledAt { get; set; }

        public bool MpvPaused { get; set; }

        public double? StallPosition { get; set; }

        public DateTimeOffset StallSince { get; set; }

        public DateTimeOffset? RestartAt { get; set; }

        public double SentVolume { get; set; } = double.NaN;

        public bool IsUp(DateTimeOffset now) => Mpv.IsRunning && (RestartAt is null || now >= RestartAt);

        public double? EstimatedPosition(DateTimeOffset now) =>
            Position is not { } position ? null
            : MpvPaused || AwaitingLoad ? position
            : position + (now - PolledAt).TotalSeconds;

        public void ClearTrack()
        {
            Track = null;
            Appended = null;
            AwaitingLoad = false;
            SeekOnLoad = null;
            FadeInOnLoad = null;
            Position = null;
            Duration = null;
            StallPosition = null;
            Gain = Envelope.Constant(0);
        }

        public void MarkFailed(DateTimeOffset restartAt)
        {
            ClearTrack();
            RestartAt = restartAt;
            MpvPaused = false;
            Endpoint = null;
            SentVolume = double.NaN;
        }

        public void ResetAfterStart(string endpoint)
        {
            Endpoint = endpoint;
            RestartAt = null;
            MpvPaused = false;
            SentVolume = 0;
        }
    }
}
