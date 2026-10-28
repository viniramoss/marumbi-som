using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Spots;

/// <summary>Prioridade das ofertas (seção 10.3.5), da menor para a maior.</summary>
public enum SpotPriority
{
    EveryTracks = 0,
    EveryMinutes = 1,
    Anchor = 2,
    Clock = 3,
    Command = 4,
}

/// <summary>Oferta disparada, aguardando a vez na fila.</summary>
public sealed record SpotRequest(string OfferId, string? PlacementId, SpotMode Mode, SpotPriority Priority, DateTimeOffset TriggeredAt)
{
    /// <summary>Âncoras e o comando "tocar oferta agora" ignoram o intervalo mínimo (seção 10.3.5).</summary>
    public bool IgnoresMinGap => Priority is SpotPriority.Anchor or SpotPriority.Command;
}

/// <summary>Oferta liberada para tocar agora, com o arquivo do cache já conferido.</summary>
public sealed record SpotToPlay(SpotRequest Request, OfferDefinition Offer, string Path);

/// <summary>Situação do player no momento da avaliação das ofertas.</summary>
public sealed record PlaybackContext
{
    public required DateTimeOffset Now { get; init; }

    public bool InWindow { get; init; }

    /// <summary>Playlist da janela de agenda atual.</summary>
    public string? WindowPlaylistId { get; init; }

    public bool PausedByAdmin { get; init; }

    public bool SpeakerConnected { get; init; }

    public bool SpotPlaying { get; init; }

    /// <summary>Faixa tocando agora; nula entre faixas.</summary>
    public QueueItem? Current { get; init; }

    /// <summary>Muda a cada faixa iniciada; âncoras "durante" disparam uma vez por execução da faixa.</summary>
    public long TrackInstance { get; init; }

    public double? PositionS { get; init; }

    public double? DurationS { get; init; }

    public bool MusicPlaying => InWindow && !PausedByAdmin;
}

/// <summary>
/// Avalia âncoras e regras de ofertas, aplica prioridade, intervalo mínimo, validade e a fila de no máximo
/// 3 ofertas (seção 10.3). Não toca nada: o <see cref="PlayerEngine"/> pergunta, nos momentos certos, se há
/// oferta para tocar entre músicas (<see cref="TakeBetween"/>) ou durante a música (<see cref="TakeDuring"/>).
/// Chamado sempre pela mesma thread do player.
/// </summary>
public sealed class SpotScheduler(StateStore state, CacheManager cache, IPlaybackEvents events, ILogger<SpotScheduler> logger)
{
    public const int MaxQueue = 3;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ClockGrace = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AnchorDuringGrace = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AnchorDuringEndMargin = TimeSpan.FromSeconds(5);

    private readonly List<SpotRequest> _queue = [];
    private readonly HashSet<string> _anchorsFiredThisTrack = new(StringComparer.Ordinal);
    private long _anchorTrackInstance = -1;
    private Manifest? _manifest;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;

    public IReadOnlyList<SpotRequest> Queue => _queue.ToList();

    private SpotsSection Spots => _manifest?.Spots ?? new SpotsSection();

    public void SetManifest(Manifest manifest, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
        _zone = zone;
        var placementIds = manifest.Spots.Placements.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in state.State.SpotTrackCounters.Keys.Where(k => !placementIds.Contains(k)).ToList())
        {
            state.State.SpotTrackCounters.Remove(key);
        }

        foreach (var key in state.State.SpotLastTriggered.Keys.Where(k => !placementIds.Contains(k)).ToList())
        {
            state.State.SpotLastTriggered.Remove(key);
        }

        _queue.RemoveAll(r => !manifest.Spots.Offers.ContainsKey(r.OfferId));
    }

    /// <summary>A cada ciclo do player: descarta ofertas velhas e dispara regras de tempo e âncoras "durante".</summary>
    public void Evaluate(PlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DropStale(context.Now);
        if (_manifest is null || !context.MusicPlaying)
        {
            return;
        }

        var playlistId = context.Current?.PlaylistId ?? context.WindowPlaylistId;
        foreach (var placement in Spots.Placements)
        {
            switch (placement)
            {
                case EveryMinutesPlacement rule when AppliesTo(rule, playlistId):
                    EvaluateEveryMinutes(rule, context.Now);
                    break;
                case ClockPlacement rule when AppliesTo(rule, playlistId):
                    EvaluateClock(rule, context.Now);
                    break;
                case AnchorDuringPlacement anchor:
                    EvaluateAnchorDuring(anchor, context);
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Chamado uma vez por faixa, alguns segundos antes do fim: dispara âncoras "entre" e regras a cada N músicas
    /// e devolve a oferta que deve tocar entre esta faixa e a próxima (sem mixagem), se houver.
    /// </summary>
    public SpotToPlay? TakeBetween(PlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_manifest is not null && context.MusicPlaying)
        {
            var playlistId = context.Current?.PlaylistId ?? context.WindowPlaylistId;
            foreach (var placement in Spots.Placements)
            {
                if (placement is AnchorBetweenPlacement anchor && context.Current is { } current
                    && anchor.PlaylistId == current.PlaylistId && anchor.AfterItemId == current.ItemId)
                {
                    Trigger(anchor, SpotMode.Between, SpotPriority.Anchor, context.Now);
                }
                else if (placement is EveryTracksPlacement rule && AppliesTo(rule, playlistId)
                    && state.State.SpotTrackCounters.GetValueOrDefault(rule.Id) >= Math.Max(1, rule.N))
                {
                    state.State.SpotTrackCounters[rule.Id] = 0;
                    Trigger(rule, SpotMode.Between, SpotPriority.EveryTracks, context.Now);
                }
            }
        }

        return Take(context, SpotMode.Between);
    }

    /// <summary>Oferta que pode começar agora durante a música (ducking), se houver.</summary>
    public SpotToPlay? TakeDuring(PlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Take(context, SpotMode.During);
    }

    /// <summary>Comando "tocar oferta agora": prioridade máxima, ignora intervalo mínimo e agenda, toca durante a música.</summary>
    public string? RequestNow(string offerId, DateTimeOffset now)
    {
        if (!Spots.Offers.TryGetValue(offerId, out var offer))
        {
            return $"Oferta '{offerId}' não está no manifesto.";
        }

        if (!offer.IsValidAt(now))
        {
            return $"Oferta '{offer.Name}' fora da validade.";
        }

        if (!cache.Has(offer.Hash))
        {
            return $"Áudio da oferta '{offer.Name}' não está no cache.";
        }

        _queue.RemoveAll(r => r.OfferId == offerId);
        Enqueue(new SpotRequest(offerId, null, SpotMode.During, SpotPriority.Command, now));
        return null;
    }

    /// <summary>Devolve à fila uma oferta "entre" que não chegou a tocar (ex.: o admin pulou a música).</summary>
    public void Requeue(SpotToPlay spot)
    {
        ArgumentNullException.ThrowIfNull(spot);
        if (_queue.All(r => r.OfferId != spot.Request.OfferId))
        {
            Enqueue(spot.Request);
        }
    }

    public void OnTrackStarted(QueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var rule in Spots.Placements.OfType<EveryTracksPlacement>())
        {
            if (AppliesTo(rule, item.PlaylistId))
            {
                state.State.SpotTrackCounters[rule.Id] = state.State.SpotTrackCounters.GetValueOrDefault(rule.Id) + 1;
            }
        }
    }

    public void OnSpotStarted(SpotToPlay spot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spot);
        logger.LogInformation(
            "Oferta '{Offer}' ({Mode}, {Priority}) iniciada", spot.Offer.Name, spot.Request.Mode, spot.Request.Priority);
        events.Publish(new SpotStartedMessage(spot.Request.OfferId, spot.Request.PlacementId, spot.Request.Mode, now));
    }

    public void OnSpotFinished(SpotToPlay spot, SpotResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spot);
        state.State.LastOfferEndedAt = now;
        state.Save();
        logger.LogInformation("Oferta '{Offer}' terminou: {Result}", spot.Offer.Name, result);
        events.Publish(new SpotFinishedMessage(spot.Request.OfferId, result));
    }

    private SpotToPlay? Take(PlaybackContext context, SpotMode mode)
    {
        if (context.SpotPlaying || !context.SpeakerConnected)
        {
            return null;
        }

        foreach (var request in _queue.ToList())
        {
            if (request.Mode != mode)
            {
                continue;
            }

            var isCommand = request.Priority == SpotPriority.Command;
            if (!isCommand && !context.MusicPlaying)
            {
                continue;
            }

            if (!request.IgnoresMinGap && state.State.LastOfferEndedAt is { } lastEnd
                && context.Now - lastEnd < TimeSpan.FromSeconds(Spots.MinOfferGapS))
            {
                continue;
            }

            _queue.Remove(request);
            if (!Spots.Offers.TryGetValue(request.OfferId, out var offer) || !offer.IsValidAt(context.Now))
            {
                // Regra 14: conferida com o relógio local imediatamente antes de tocar.
                Skip(request, SpotSkipReasons.Expired);
                continue;
            }

            if (!cache.Has(offer.Hash))
            {
                Skip(request, SpotSkipReasons.MissingFile);
                continue;
            }

            return new SpotToPlay(request, offer, cache.PathFor(offer.Hash));
        }

        return null;
    }

    private void EvaluateEveryMinutes(EveryMinutesPlacement rule, DateTimeOffset now)
    {
        if (!state.State.SpotLastTriggered.TryGetValue(rule.Id, out var last))
        {
            // Primeira vez que a regra é vista: o intervalo conta a partir de agora.
            state.State.SpotLastTriggered[rule.Id] = now;
            state.Save();
            return;
        }

        if (now - last >= TimeSpan.FromMinutes(Math.Max(1, rule.Minutes)))
        {
            state.State.SpotLastTriggered[rule.Id] = now;
            state.Save();
            Trigger(rule, rule.Mode, SpotPriority.EveryMinutes, now);
        }
    }

    private void EvaluateClock(ClockPlacement rule, DateTimeOffset now)
    {
        var today = TimeZoneInfo.ConvertTime(now, _zone).Date;
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            if (!rule.Days.Contains((int)date.DayOfWeek))
            {
                continue;
            }

            var local = date + rule.Time.ToTimeSpan();
            var at = new DateTimeOffset(local, _zone.GetUtcOffset(local));
            var last = state.State.SpotLastTriggered.GetValueOrDefault(rule.Id, DateTimeOffset.MinValue);
            if (now >= at && now - at < ClockGrace && last < at)
            {
                state.State.SpotLastTriggered[rule.Id] = now;
                state.Save();
                Trigger(rule, rule.Mode, SpotPriority.Clock, now);
            }
        }
    }

    private void EvaluateAnchorDuring(AnchorDuringPlacement anchor, PlaybackContext context)
    {
        if (context.Current is not { } current || current.PlaylistId != anchor.PlaylistId || current.ItemId != anchor.ItemId
            || context.PositionS is not { } position)
        {
            return;
        }

        if (context.TrackInstance != _anchorTrackInstance)
        {
            _anchorTrackInstance = context.TrackInstance;
            _anchorsFiredThisTrack.Clear();
        }

        if (_anchorsFiredThisTrack.Contains(anchor.Id))
        {
            return;
        }

        // offsetMs deve ser ≤ duração − 5 s; fora disso, o agente limita (seção 10.3.4).
        var offset = TimeSpan.FromMilliseconds(Math.Max(0, anchor.OffsetMs));
        if (context.DurationS is { } duration)
        {
            var max = TimeSpan.FromSeconds(duration) - AnchorDuringEndMargin;
            offset = max < TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(offset.Ticks, max.Ticks));
        }

        var elapsed = TimeSpan.FromSeconds(position);
        if (elapsed < offset)
        {
            return;
        }

        _anchorsFiredThisTrack.Add(anchor.Id);
        if (elapsed - offset > AnchorDuringGrace)
        {
            // Faixa retomada depois do ponto marcado (ex.: após a caixa voltar): não toca fora de hora.
            logger.LogInformation("Âncora {Placement} ignorada: faixa já passou do ponto marcado", anchor.Id);
            return;
        }

        Trigger(anchor, SpotMode.During, SpotPriority.Anchor, context.Now);
    }

    private void Trigger(Placement placement, SpotMode mode, SpotPriority priority, DateTimeOffset now)
    {
        if (!Spots.Offers.ContainsKey(placement.OfferId))
        {
            logger.LogWarning("Posicionamento {Placement} aponta para a oferta inexistente {Offer}", placement.Id, placement.OfferId);
            return;
        }

        var request = new SpotRequest(placement.OfferId, placement.Id, mode, priority, now);
        if (_queue.Any(r => r.OfferId == placement.OfferId))
        {
            // A mesma oferta nunca aparece duas vezes na fila.
            Skip(request, SpotSkipReasons.Duplicate);
            return;
        }

        logger.LogInformation("Oferta {Offer} disparada por {Placement} ({Priority}, {Mode})", placement.OfferId, placement.Id, priority, mode);
        Enqueue(request);
    }

    private void Enqueue(SpotRequest request)
    {
        _queue.Add(request);
        _queue.Sort((a, b) => b.Priority != a.Priority ? b.Priority.CompareTo(a.Priority) : a.TriggeredAt.CompareTo(b.TriggeredAt));
        while (_queue.Count > MaxQueue)
        {
            var dropped = _queue[^1];
            _queue.RemoveAt(_queue.Count - 1);
            Skip(dropped, SpotSkipReasons.QueueFull);
        }
    }

    private void DropStale(DateTimeOffset now)
    {
        foreach (var request in _queue.Where(r => now - r.TriggeredAt > StaleAfter).ToList())
        {
            _queue.Remove(request);
            Skip(request, SpotSkipReasons.Stale);
        }
    }

    private void Skip(SpotRequest request, string reason)
    {
        logger.LogInformation("Oferta {Offer} ({Placement}) descartada: {Reason}", request.OfferId, request.PlacementId, reason);
        events.Publish(new SpotSkippedMessage(request.OfferId, request.PlacementId, reason));
    }

    private static bool AppliesTo(RulePlacement rule, string? playlistId) =>
        rule.PlaylistIds is not { Count: > 0 } ids || (playlistId is not null && ids.Contains(playlistId));
}
