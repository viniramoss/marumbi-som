using System.Security.Cryptography;
using System.Text;
using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Spots;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests.Spots;

public sealed class SpotSchedulerTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly DateTimeOffset Friday10h = new(2026, 10, 23, 10, 0, 0, TimeSpan.FromHours(-3));

    private readonly AgentDataPaths _paths = new(Path.Combine(Path.GetTempPath(), "marumbi-tests", Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(Friday10h);
    private readonly CacheManager _cache;
    private readonly StateStore _state;
    private readonly PlaybackEventLog _events;
    private readonly SpotScheduler _scheduler;
    private readonly Dictionary<string, OfferDefinition> _offers = [];

    public SpotSchedulerTests()
    {
        _cache = new CacheManager(_paths, _time, NullLogger<CacheManager>.Instance);
        _state = new StateStore(_paths, NullLogger<StateStore>.Instance);
        _events = new PlaybackEventLog(_time, NullLogger<PlaybackEventLog>.Instance);
        _scheduler = new SpotScheduler(_state, _cache, _events, NullLogger<SpotScheduler>.Instance);
        foreach (var id in new[] { "of-1", "of-2", "of-3", "of-4", "of-5" })
        {
            _offers[id] = Offer(id);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root))
        {
            Directory.Delete(_paths.Root, recursive: true);
        }
    }

    [Fact]
    public void Ancora_entre_dispara_ao_fim_do_item_indicado_e_ignora_o_intervalo_minimo()
    {
        Load(300, new AnchorBetweenPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "tarde", AfterItemId = "it-88" });
        _state.State.LastOfferEndedAt = Now.AddSeconds(-10);

        Assert.Null(_scheduler.TakeBetween(Context(current: new QueueItem("tarde", "it-90", "x"))));
        var spot = _scheduler.TakeBetween(Context(current: new QueueItem("tarde", "it-88", "x")));

        Assert.Equal("of-1", spot?.Request.OfferId);
        Assert.Equal(SpotPriority.Anchor, spot!.Request.Priority);
    }

    [Fact]
    public void Ancora_durante_dispara_no_tempo_marcado_uma_vez_por_execucao_e_limita_o_offset()
    {
        Load(0, new AnchorDuringPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "tarde", ItemId = "it-90", OffsetMs = 500_000 });
        var current = new QueueItem("tarde", "it-90", "x");

        _scheduler.Evaluate(Context(current: current, position: 50, duration: 60, instance: 1));
        Assert.Null(_scheduler.TakeDuring(Context()));

        // offsetMs maior que a música: limitado a duração − 5 s = 55 s.
        _scheduler.Evaluate(Context(current: current, position: 55.1, duration: 60, instance: 1));
        Assert.Equal("of-1", _scheduler.TakeDuring(Context())?.Request.OfferId);

        _scheduler.Evaluate(Context(current: current, position: 56, duration: 60, instance: 1));
        Assert.Null(_scheduler.TakeDuring(Context()));

        // Nova execução da mesma faixa: dispara de novo.
        _scheduler.Evaluate(Context(current: current, position: 55.2, duration: 60, instance: 2));
        Assert.NotNull(_scheduler.TakeDuring(Context()));
    }

    [Fact]
    public void A_cada_n_musicas_conta_as_faixas_e_respeita_playlists()
    {
        Load(0, new EveryTracksPlacement { Id = "pl-1", OfferId = "of-1", N = 3, PlaylistIds = ["tarde"] });

        for (var i = 0; i < 2; i++)
        {
            _scheduler.OnTrackStarted(new QueueItem("tarde", $"it-{i}", "x"));
            Assert.Null(_scheduler.TakeBetween(Context(current: new QueueItem("tarde", $"it-{i}", "x"))));
        }

        _scheduler.OnTrackStarted(new QueueItem("manha", "it-x", "x"));
        _scheduler.OnTrackStarted(new QueueItem("tarde", "it-3", "x"));
        Assert.Equal(SpotMode.Between, _scheduler.TakeBetween(Context(current: new QueueItem("tarde", "it-3", "x")))?.Request.Mode);
        Assert.Equal(0, _state.State.SpotTrackCounters["pl-1"]);
    }

    [Fact]
    public void A_cada_x_minutos_conta_desde_o_ultimo_disparo()
    {
        Load(0, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 15, Mode = SpotMode.During });

        _scheduler.Evaluate(Context());
        Assert.Null(_scheduler.TakeDuring(Context()));

        _time.Advance(TimeSpan.FromMinutes(14));
        _scheduler.Evaluate(Context());
        Assert.Null(_scheduler.TakeDuring(Context()));

        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());
        Assert.NotNull(_scheduler.TakeDuring(Context()));
        Assert.Equal(Now, _state.State.SpotLastTriggered["pl-1"]);
    }

    [Fact]
    public void Horario_fixo_dispara_uma_vez_e_nao_dispara_atrasado()
    {
        Load(0, new ClockPlacement { Id = "pl-1", OfferId = "of-1", Time = new TimeOnly(10, 30), Days = [5], Mode = SpotMode.During });

        _time.Advance(TimeSpan.FromMinutes(29));
        _scheduler.Evaluate(Context());
        Assert.Null(_scheduler.TakeDuring(Context()));

        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());
        Assert.Equal(SpotPriority.Clock, _scheduler.TakeDuring(Context())?.Request.Priority);

        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());
        Assert.Null(_scheduler.TakeDuring(Context()));

        // Agente ligado 20 min depois do horário em outra sexta: não toca fora de hora.
        using var late = new SpotSchedulerTests();
        late.Load(0, new ClockPlacement { Id = "pl-1", OfferId = "of-1", Time = new TimeOnly(10, 30), Days = [5], Mode = SpotMode.During });
        late._time.Advance(TimeSpan.FromMinutes(50));
        late._scheduler.Evaluate(late.Context());
        Assert.Null(late._scheduler.TakeDuring(late.Context()));
    }

    [Fact]
    public void Prioridade_e_intervalo_minimo_entre_ofertas()
    {
        Load(300,
            new EveryMinutesPlacement { Id = "pl-min", OfferId = "of-1", Minutes = 1, Mode = SpotMode.During },
            new ClockPlacement { Id = "pl-clock", OfferId = "of-2", Time = new TimeOnly(10, 1), Days = [5], Mode = SpotMode.During });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());

        Assert.Equal(["of-2", "of-1"], _scheduler.Queue.Select(q => q.OfferId));
        var first = _scheduler.TakeDuring(Context())!;
        Assert.Equal("of-2", first.Request.OfferId);
        _scheduler.OnSpotStarted(first, Now);

        // Outra oferta não começa enquanto uma toca, nem durante o intervalo mínimo.
        Assert.Null(_scheduler.TakeDuring(Context(spotPlaying: true)));
        _time.Advance(TimeSpan.FromSeconds(15));
        _scheduler.OnSpotFinished(first, SpotResult.Played, Now);
        _time.Advance(TimeSpan.FromSeconds(299));
        Assert.Null(_scheduler.TakeDuring(Context()));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("of-1", _scheduler.TakeDuring(Context())?.Request.OfferId);
    }

    [Fact]
    public void Fila_tem_no_maximo_3_e_descarta_a_de_menor_prioridade()
    {
        Load(300,
            new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.Between },
            new EveryMinutesPlacement { Id = "pl-2", OfferId = "of-2", Minutes = 1, Mode = SpotMode.Between },
            new EveryMinutesPlacement { Id = "pl-3", OfferId = "of-3", Minutes = 1, Mode = SpotMode.Between },
            new ClockPlacement { Id = "pl-4", OfferId = "of-4", Time = new TimeOnly(10, 1), Days = [5], Mode = SpotMode.Between });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());

        Assert.Equal(3, _scheduler.Queue.Count);
        Assert.Equal("of-4", _scheduler.Queue[0].OfferId);
        var skipped = _events.Recent.Select(e => e.Message).OfType<SpotSkippedMessage>().Single();
        Assert.Equal(SpotSkipReasons.QueueFull, skipped.Reason);
    }

    [Fact]
    public void Mesma_oferta_nunca_duas_vezes_na_fila()
    {
        Load(300,
            new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.Between },
            new ClockPlacement { Id = "pl-2", OfferId = "of-1", Time = new TimeOnly(10, 1), Days = [5], Mode = SpotMode.Between });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());

        Assert.Single(_scheduler.Queue);
        Assert.Contains(_events.Recent.Select(e => e.Message).OfType<SpotSkippedMessage>(), s => s.Reason == SpotSkipReasons.Duplicate);
    }

    [Fact]
    public void Oferta_na_fila_ha_mais_de_10_min_e_descartada()
    {
        Load(0, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.During });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context(connected: false));
        Assert.Single(_scheduler.Queue);

        _time.Advance(TimeSpan.FromMinutes(10.1));
        _scheduler.Evaluate(Context(inWindow: false));

        Assert.Empty(_scheduler.Queue);
        Assert.Equal(SpotSkipReasons.Stale, _events.Recent.Select(e => e.Message).OfType<SpotSkippedMessage>().Single().Reason);
    }

    [Fact]
    public void Oferta_vencida_nao_toca_mesmo_estando_no_manifesto()
    {
        _offers["of-1"] = Offer("of-1") with { ValidUntil = Friday10h.AddMinutes(1) };
        Load(0, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.During });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());

        Assert.Null(_scheduler.TakeDuring(Context()));
        Assert.Equal(SpotSkipReasons.Expired, _events.Recent.Select(e => e.Message).OfType<SpotSkippedMessage>().Single().Reason);
    }

    [Fact]
    public void Audio_ausente_no_cache_e_descartado()
    {
        Load(0, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.During });
        File.Delete(_cache.PathFor(_offers["of-1"].Hash));
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context());

        Assert.Null(_scheduler.TakeDuring(Context()));
        Assert.Equal(SpotSkipReasons.MissingFile, _events.Recent.Select(e => e.Message).OfType<SpotSkippedMessage>().Single().Reason);
    }

    [Fact]
    public void Regras_nao_tocam_fora_da_agenda_nem_com_a_musica_pausada_mas_o_comando_toca()
    {
        Load(300, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 1, Mode = SpotMode.During });
        _scheduler.Evaluate(Context());
        _time.Advance(TimeSpan.FromMinutes(1));
        _scheduler.Evaluate(Context(paused: true));
        Assert.Empty(_scheduler.Queue);

        Assert.Null(_scheduler.RequestNow("of-2", Now));
        _state.State.LastOfferEndedAt = Now;
        var spot = _scheduler.TakeDuring(Context(inWindow: false));

        Assert.Equal(SpotPriority.Command, spot?.Request.Priority);
        Assert.NotNull(_scheduler.RequestNow("nao-existe", Now));
    }

    [Fact]
    public void Previsao_soma_a_musica_atual_e_a_fila_ate_a_ancora()
    {
        Load(0, new AnchorDuringPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "tarde", ItemId = "it-2", OffsetMs = 20_000 });
        QueueItem[] upcoming = [new("tarde", "it-1", "a"), new("tarde", "it-2", "b")];

        var next = _scheduler.NextPlanned(Context(current: new QueueItem("tarde", "it-0", "x"), position: 30, duration: 200), upcoming, _ => 100, null);

        // 170 s que faltam da atual + 100 s da it-1 + 20 s dentro da it-2.
        Assert.Equal(new NextSpot("of-1", Now.AddSeconds(290)), next);
    }

    [Fact]
    public void Previsao_escolhe_a_regra_mais_proxima_respeitando_intervalo_minimo_e_validade()
    {
        _offers["of-2"] = Offer("of-2") with { ValidUntil = Friday10h.AddMinutes(20) };
        Load(
            600,
            new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 15, Mode = SpotMode.During },
            new ClockPlacement { Id = "pl-2", OfferId = "of-2", Time = new TimeOnly(10, 30), Days = [5], Mode = SpotMode.During },
            new ClockPlacement { Id = "pl-3", OfferId = "of-3", Time = new TimeOnly(10, 12), Days = [5], Mode = SpotMode.During });
        _state.State.SpotLastTriggered["pl-1"] = Now.AddMinutes(-10);
        _state.State.LastOfferEndedAt = Now.AddMinutes(-4);

        var next = _scheduler.NextPlanned(Context(current: new QueueItem("tarde", "it-0", "x"), position: 0, duration: 200), [], _ => 100, null);

        // A cada 15 min vence às 10:05, mas o intervalo mínimo (10 min desde 9:56) segura até 10:06; o of-2 das 10:30
        // já estaria vencido e o of-3 das 10:12 vem depois.
        Assert.Equal(new NextSpot("of-1", Now.AddMinutes(6)), next);
    }

    [Fact]
    public void Sem_musica_tocando_ou_fora_da_janela_nao_ha_previsao()
    {
        Load(0, new EveryMinutesPlacement { Id = "pl-1", OfferId = "of-1", Minutes = 15, Mode = SpotMode.During });

        Assert.Null(_scheduler.NextPlanned(Context(inWindow: false), [], _ => 100, null));
        Assert.Null(_scheduler.NextPlanned(Context(paused: true), [], _ => 100, null));
        Assert.Null(_scheduler.NextPlanned(Context(), [], _ => 100, windowEnd: Now.AddMinutes(10)));
    }

    private void Load(int minGap, params Placement[] placements)
    {
        foreach (var offer in _offers.Values)
        {
            var path = _cache.PathFor(offer.Hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, offer.Name);
        }

        var manifest = new Manifest
        {
            StoreId = "loja-2",
            Version = 1,
            GeneratedAt = Friday10h,
            Timezone = "America/Sao_Paulo",
            Volume = 70,
            Spots = new SpotsSection { MinOfferGapS = minGap, Offers = _offers, Placements = placements },
        };
        _scheduler.SetManifest(manifest, Zone);
    }

    private PlaybackContext Context(
        QueueItem? current = null,
        double? position = null,
        double? duration = null,
        long instance = 1,
        bool spotPlaying = false,
        bool connected = true,
        bool inWindow = true,
        bool paused = false) => new()
    {
        Now = Now,
        InWindow = inWindow,
        WindowPlaylistId = "tarde",
        PausedByAdmin = paused,
        SpeakerConnected = connected,
        SpotPlaying = spotPlaying,
        Current = current,
        TrackInstance = instance,
        PositionS = position,
        DurationS = duration,
    };

    private static OfferDefinition Offer(string id) => new()
    {
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))),
        Name = "Oferta " + id,
        ValidFrom = Friday10h.AddDays(-1),
        ValidUntil = Friday10h.AddDays(2),
    };
}
