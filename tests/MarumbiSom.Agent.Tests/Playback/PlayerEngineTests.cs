using MarumbiSom.Agent.Playback;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Tests.Playback;

public sealed class PlayerEngineTests : IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(300);
    private readonly PlayerHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Fora_da_agenda_fica_em_silencio_e_comeca_no_inicio_da_janela()
    {
        var t1 = _h.AddTrack("Um", 60);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });

        await _h.RunUntilAsync("09:59:59");
        Assert.Null(_h.A.CurrentFile);
        Assert.Null(_h.B.CurrentFile);

        await _h.RunUntilAsync("10:00:00.3");
        Assert.Equal(_h.PathOf(t1), _h.A.CurrentFile);
        Assert.Equal(PlayerStatus.Playing, (await SnapshotAsync()).Status);
    }

    [Fact]
    public async Task Mixagem_comeca_a_crossfadeS_do_fim_com_potencia_constante()
    {
        var t1 = _h.AddTrack("Um", 60);
        var t2 = _h.AddTrack("Dois", 60);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) });

        await _h.RunUntilAsync("10:01:10");

        var starts = _h.TrackStarts;
        Assert.Equal([t1, t2], starts.Select(s => s.Hash));
        var mixStart = starts[1].At - starts[0].At;
        Assert.InRange(mixStart, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(55) + Step);
        Assert.Equal(_h.PathOf(t2), _h.B.CurrentFile);
        Assert.Null(_h.A.CurrentFile);

        // Potência constante: gA² + gB² ≈ 1 durante toda a mixagem, e o ganho de B só sobe.
        var previousB = 0.0;
        for (var t = starts[1].At + TimeSpan.FromMilliseconds(100); t < starts[1].At + TimeSpan.FromSeconds(4.9); t += TimeSpan.FromMilliseconds(100))
        {
            var a = PlayerHarness.GainAt(_h.A, t);
            var b = PlayerHarness.GainAt(_h.B, t);
            Assert.InRange((a * a) + (b * b), 0.93, 1.07);
            Assert.True(b >= previousB - 1e-9);
            previousB = b;
        }

        Assert.InRange(PlayerHarness.GainAt(_h.B, starts[1].At + TimeSpan.FromSeconds(5.1)), 0.99, 1.01);
    }

    [Fact]
    public async Task Faixa_curta_mistura_por_um_terco_da_duracao()
    {
        var t1 = _h.AddTrack("Curta", 12);
        var t2 = _h.AddTrack("Dois", 60);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) });

        await _h.RunUntilAsync("10:00:20");

        var starts = _h.TrackStarts;
        Assert.InRange(starts[1].At - starts[0].At, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(8) + Step);
    }

    [Fact]
    public async Task Com_mixagem_zero_a_proxima_faixa_emenda_no_mesmo_mpv()
    {
        var t1 = _h.AddTrack("Um", 30);
        var t2 = _h.AddTrack("Dois", 30);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) }, crossfade: 0);

        await _h.RunUntilAsync("10:00:27");
        Assert.Equal([_h.PathOf(t1), _h.PathOf(t2)], _h.A.Playlist);

        await _h.RunUntilAsync("10:00:35");
        var starts = _h.TrackStarts;
        Assert.Equal([t1, t2], starts.Select(s => s.Hash));
        Assert.Equal(_h.PathOf(t2), _h.A.CurrentFile);
        Assert.Equal([_h.PathOf(t2)], _h.A.Playlist);
        Assert.Empty(_h.B.Playlist);
        Assert.Equal(0, _h.B.VolumeHistory.Count(v => v.Volume > 0));
        // Sem pausa: a segunda começa no instante exato em que a primeira termina (dentro de um ciclo).
        Assert.InRange(starts[1].At - starts[0].At, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30) + Step);
    }

    [Fact]
    public async Task Troca_de_janela_termina_a_faixa_atual_e_entra_a_nova_playlist_com_mixagem()
    {
        var m1 = _h.AddTrack("Manhã 1", 90);
        var m2 = _h.AddTrack("Manhã 2", 90);
        var t1 = _h.AddTrack("Tarde 1", 90);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition>
            {
                ["manha"] = PlayerHarness.Playlist(false, m1, m2),
                ["tarde"] = PlayerHarness.Playlist(false, t1),
            },
            [PlayerHarness.Window("10:00", "10:01", "manha"), PlayerHarness.Window("10:01", "12:00", "tarde")]);

        await _h.RunUntilAsync("10:01:00.5");
        Assert.Equal(_h.PathOf(m1), _h.A.CurrentFile);
        Assert.InRange(PlayerHarness.GainAt(_h.A, _h.Now), 0.99, 1.01);

        await _h.RunUntilAsync("10:01:30");
        Assert.Equal([m1, t1], _h.TrackStarts.Select(s => s.Hash));
        Assert.InRange(_h.TrackStarts[1].At - PlayerHarness.At("10:01:25"), -Step, Step);
    }

    [Fact]
    public async Task Fim_do_horario_faz_fade_de_3_s_e_para_no_horario_exato()
    {
        var t1 = _h.AddTrack("Longa", 300);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) },
            [PlayerHarness.Window("10:00", "10:01", "manha")]);

        await _h.RunUntilAsync("10:01:00.3");

        Assert.InRange(PlayerHarness.VolumeAt(_h.A, PlayerHarness.At("10:00:56.9")), 69.9, 70.1);
        var middle = PlayerHarness.VolumeAt(_h.A, PlayerHarness.At("10:00:58.5"));
        Assert.InRange(middle, 20, 50);
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, PlayerHarness.At("10:01:00")), 0, 1);
        Assert.Null(_h.A.CurrentFile);
        Assert.Equal(PlayerStatus.Stopped, (await SnapshotAsync()).Status);
    }

    [Fact]
    public async Task Apos_reiniciar_continua_da_faixa_seguinte()
    {
        var t1 = _h.AddTrack("Um", 30);
        var t2 = _h.AddTrack("Dois", 30);
        var t3 = _h.AddTrack("Três", 30);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2, t3) });
        await _h.RunUntilAsync("10:00:40");
        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));

        using var restarted = new PlayerHarness(_h.Paths.Root, _h.Time);
        await restarted.RunForAsync(TimeSpan.FromSeconds(1));

        Assert.Equal([t3], restarted.TrackStarts.Select(s => s.Hash));
        Assert.Equal(_h.PathOf(t3), restarted.A.CurrentFile);
    }

    [Fact]
    public async Task Faixa_ausente_no_cache_e_pulada()
    {
        var missing = _h.AddMissingTrack("Ausente", 30);
        var t2 = _h.AddTrack("Dois", 30);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, missing, t2) });

        await _h.RunUntilAsync("10:00:01");

        Assert.Equal([t2], _h.TrackStarts.Select(s => s.Hash));
    }

    [Fact]
    public async Task Mudanca_de_manifesto_nao_corta_a_faixa_e_segue_a_nova_ordem()
    {
        var t1 = _h.AddTrack("Um", 60);
        var t2 = _h.AddTrack("Dois", 60);
        var t3 = _h.AddTrack("Três", 60);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2, t3) });
        await _h.RunUntilAsync("10:00:20");

        // Nova ordem: Três, Um, Dois (os ids dos itens acompanham as faixas).
        _h.Publish(new Dictionary<string, PlaylistDefinition>
        {
            ["manha"] = new(false, [new("it-3", t3), new("it-1", t1), new("it-2", t2)]),
        });
        await _h.RunUntilAsync("10:01:00");

        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));
        Assert.InRange(_h.TrackStarts[1].At - _h.TrackStarts[0].At, TimeSpan.FromSeconds(55), TimeSpan.FromSeconds(55) + Step);
    }

    [Fact]
    public async Task Oferta_entre_musicas_suspende_a_mixagem_e_a_proxima_comeca_300_ms_depois()
    {
        var t1 = _h.AddTrack("Um", 60);
        var t2 = _h.AddTrack("Dois", 60);
        var offer = _h.AddTrack("Picanha", 10, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) },
            spots: Spots(offer, new AnchorBetweenPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", AfterItemId = "it-1" }));

        await _h.RunUntilAsync("10:01:20");

        var starts = _h.TrackStarts;
        Assert.Equal([t1, t2], starts.Select(s => s.Hash));
        var spotStarted = _h.Messages.OfType<SpotStartedMessage>().Single();
        Assert.Equal(SpotMode.Between, spotStarted.Mode);
        // A música termina inteira (sem mixagem), a oferta toca e a próxima entra 300 ms após a oferta.
        Assert.InRange(spotStarted.At - starts[0].At, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60) + Step);
        Assert.InRange(starts[1].At - spotStarted.At, TimeSpan.FromSeconds(10.3), TimeSpan.FromSeconds(10.3) + Step);
        Assert.Equal(SpotResult.Played, _h.Messages.OfType<SpotFinishedMessage>().Single().Result);
        Assert.InRange(PlayerHarness.VolumeAt(_h.Spot, spotStarted.At + TimeSpan.FromSeconds(5)), 69.9, 70.1);
        Assert.Empty(_h.B.FileStarts);
        Assert.Equal(_h.PathOf(t2), _h.A.CurrentFile);
        Assert.InRange(PlayerHarness.GainAt(_h.A, starts[1].At + TimeSpan.FromSeconds(1)), 0.99, 1.01);
    }

    [Fact]
    public async Task Oferta_que_termina_antes_da_duracao_conta_como_interrompida()
    {
        // PC piloto, 23/10/2026: a saída de áudio caiu no meio da oferta e o mpv respondeu eof, registrado como "tocou".
        var t1 = _h.AddTrack("Um", 60);
        var t2 = _h.AddTrack("Dois", 60);
        var offer = _h.AddTrack("Picanha", 20, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) },
            spots: Spots(offer, new AnchorBetweenPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", AfterItemId = "it-1" }));
        await _h.RunUntilAsync("10:01:05");
        Assert.Equal(_h.PathOf(offer), _h.Spot.CurrentFile);

        _h.Spot.LoseAudioOutput();
        await _h.RunForAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SpotResult.Interrupted, _h.Messages.OfType<SpotFinishedMessage>().Single().Result);
        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));
    }

    [Fact]
    public async Task Oferta_durante_a_musica_faz_ducking_de_800_ms_e_volta_em_1500_ms()
    {
        var t1 = _h.AddTrack("Um", 120);
        var offer = _h.AddTrack("Picanha", 10, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) },
            spots: Spots(offer, new AnchorDuringPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", ItemId = "it-1", OffsetMs = 20_000 }));

        await _h.RunUntilAsync("10:00:45");

        var trackStart = _h.TrackStarts[0].At;
        var spot = _h.Messages.OfType<SpotStartedMessage>().Single();
        Assert.Equal(SpotMode.During, spot.Mode);
        Assert.InRange(spot.At - trackStart, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20) + Step);

        // T0 + 800 ms: música em 46,86 (70 × ∛0,3, decisão D17) e só então a oferta começa.
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, spot.At + TimeSpan.FromMilliseconds(850)), 46.7, 47.0);
        var spotLoaded = _h.Spot.FileStarts.Single().At;
        Assert.InRange(spotLoaded - spot.At, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(850));
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, spot.At + TimeSpan.FromSeconds(5)), 46.7, 47.0);

        // Rampa sem degraus: passos de no máximo 40 ms durante a descida.
        var ramp = _h.A.VolumeHistory.Where(v => v.At >= spot.At && v.At <= spot.At + TimeSpan.FromMilliseconds(800)).ToList();
        Assert.True(ramp.Count >= 15);
        Assert.All(ramp.Zip(ramp.Skip(1)), p => Assert.True(p.Second.At - p.First.At <= TimeSpan.FromMilliseconds(40)));

        // Fim da oferta (10,8 s após o disparo) + 1.500 ms: volta ao volume da loja.
        var restored = spot.At + TimeSpan.FromSeconds(10.8 + 1.5) + Step;
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, restored), 69.9, 70.1);
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, spot.At + TimeSpan.FromSeconds(10.8 + 0.75) + Step), 50, 69);
    }

    [Fact]
    public async Task Mpv_morto_a_faixa_continua_no_outro_em_menos_de_5_s()
    {
        var t1 = _h.AddTrack("Um", 120);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:30");

        _h.A.Crash();
        await _h.RunForAsync(TimeSpan.FromSeconds(1.5));

        Assert.Equal(_h.PathOf(t1), _h.B.CurrentFile);
        Assert.InRange(_h.B.Position.TotalSeconds, 30, 32.5);
        Assert.InRange(PlayerHarness.GainAt(_h.B, _h.Now), 0.99, 1.01);

        await _h.RunForAsync(TimeSpan.FromSeconds(2));
        Assert.True(_h.A.IsRunning);
        Assert.Equal(2, _h.A.StartCount);
        Assert.Single(_h.TrackStarts);
    }

    [Fact]
    public async Task Mpv_travado_por_30_s_e_reiniciado()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:10");

        _h.A.Freeze();
        await _h.RunForAsync(TimeSpan.FromSeconds(29));
        Assert.Null(_h.B.CurrentFile);

        await _h.RunForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(_h.PathOf(t1), _h.B.CurrentFile);
        Assert.InRange(_h.B.Position.TotalSeconds, 9, 13);
    }

    [Fact]
    public async Task Mpv_sem_resposta_no_ipc_e_reiniciado_e_a_faixa_continua_no_outro()
    {
        // PC piloto, 27/10/2026: set_property estourou o tempo e o mpv ficou mudo até reiniciar o serviço.
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        _h.A.Hang();
        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(_h.PathOf(t1), _h.B.CurrentFile);
        Assert.InRange(_h.B.Position.TotalSeconds, 19, 21.5);

        await _h.RunForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, _h.A.StartCount);
        Assert.False(_h.A.Hung);
        Assert.Single(_h.TrackStarts);
    }

    [Fact]
    public async Task Saida_de_audio_que_nao_abre_nao_pula_a_musica_e_ela_continua_do_mesmo_ponto()
    {
        // PC piloto, 27/10/2026: com a caixa desativada no Windows, "Failed to find device" fez o agente pular 021, 022 e 023.
        var t1 = _h.AddTrack("Um", 300);
        var t2 = _h.AddTrack("Dois", 300);
        var t3 = _h.AddTrack("Três", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2, t3) });
        await _h.RunUntilAsync("10:00:30");

        _h.B.AudioOutputMissing = true;
        _h.Spot.AudioOutputMissing = true;
        _h.A.LoseAudioDevice();
        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        _h.Bluetooth.IsConnected = false;
        await _h.RunForAsync(TimeSpan.FromSeconds(5));

        foreach (var mpv in new[] { _h.A, _h.B, _h.Spot })
        {
            mpv.AudioOutputMissing = false;
        }

        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromSeconds(3));

        Assert.Equal([t1], _h.TrackStarts.Select(s => s.Hash));
        var playing = new[] { _h.A, _h.B }.Single(m => m.CurrentFile is not null);
        Assert.Equal(_h.PathOf(t1), playing.CurrentFile);
        Assert.InRange(playing.Position.TotalSeconds, 31, 34);
        Assert.InRange(PlayerHarness.GainAt(playing, _h.Now), 0.99, 1.01);
    }

    [Fact]
    public async Task Saida_de_audio_que_segue_falhando_tenta_de_novo_com_espera_crescente()
    {
        var t1 = _h.AddTrack("Um", 300);
        var t2 = _h.AddTrack("Dois", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) });
        await _h.RunUntilAsync("10:00:30");
        var loadsBefore = _h.A.FileStarts.Count + _h.B.FileStarts.Count;

        // O endpoint continua "ativo" para o guardião, mas a saída não abre: tentativas em +2 s, +5 s e +15 s.
        _h.B.AudioOutputMissing = true;
        _h.A.LoseAudioDevice();
        await _h.RunUntilAsync("10:01:15");
        Assert.Equal(3, _h.A.FileStarts.Count + _h.B.FileStarts.Count - loadsBefore);

        // Próxima tentativa 30 s depois da última (10:00:52 → 10:01:22), já com a saída de volta.
        _h.A.AudioOutputMissing = false;
        _h.B.AudioOutputMissing = false;
        await _h.RunUntilAsync("10:01:25");

        Assert.Equal([t1], _h.TrackStarts.Select(s => s.Hash));
        var playing = new[] { _h.A, _h.B }.Single(m => m.CurrentFile is not null);
        Assert.Equal(_h.PathOf(t1), playing.CurrentFile);
        Assert.InRange(playing.Position.TotalSeconds, 30, 34);
    }

    [Fact]
    public async Task Caixa_reconectada_reabre_a_saida_de_audio_dos_mpv()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        _h.Bluetooth.IsConnected = false;
        await _h.RunForAsync(TimeSpan.FromSeconds(3));
        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromSeconds(1));

        // Mesmo id de endpoint: sem o aviso da reconexão o mpv seguiria na saída antiga (seção 9.5).
        Assert.Equal(1, _h.A.AudioOutputReloads);
        Assert.Equal(1, _h.B.AudioOutputReloads);
        Assert.Equal(1, _h.Spot.AudioOutputReloads);
        Assert.False(_h.A.Paused);
        Assert.Equal(_h.PathOf(t1), _h.A.CurrentFile);
    }

    [Fact]
    public async Task Mpv_que_trava_ao_reabrir_a_saida_e_reiniciado()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        _h.Bluetooth.IsConnected = false;
        await _h.RunForAsync(TimeSpan.FromSeconds(3));
        _h.A.Hang();
        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(_h.PathOf(t1), _h.B.CurrentFile);
        Assert.InRange(_h.B.Position.TotalSeconds, 19.5, 21.5);

        await _h.RunForAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, _h.A.StartCount);
    }

    [Fact]
    public async Task Reiniciar_audio_fecha_os_mpv_e_a_musica_volta_do_mesmo_ponto_em_mpv_novos()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        _h.Bluetooth.RestartingAudio = true;
        _h.Bluetooth.IsConnected = false;
        var snapshot = await SnapshotAsync();

        // Os três mpv fecham antes de o Windows derrubar o áudio (D37 b) e não reabrem enquanto ele reinicia.
        Assert.All(new[] { _h.A, _h.B, _h.Spot }, m => Assert.False(m.IsRunning));
        Assert.Equal(PlayerStatus.Paused, snapshot.Status);
        Assert.Equal(t1, snapshot.NowPlayingHash);
        Assert.Equal("Reiniciando o áudio do Windows", snapshot.Reason);
        Assert.InRange(snapshot.PositionS ?? 0, 19.5, 21);
        await _h.RunForAsync(TimeSpan.FromSeconds(20));
        Assert.All(new[] { _h.A, _h.B, _h.Spot }, m => Assert.Equal(1, m.StartCount));

        _h.Bluetooth.RestartingAudio = false;
        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromSeconds(1));

        Assert.All(new[] { _h.A, _h.B, _h.Spot }, m => Assert.Equal(2, m.StartCount));
        var playing = new[] { _h.A, _h.B }.Single(m => m.CurrentFile is not null);
        Assert.Equal(_h.PathOf(t1), playing.CurrentFile);
        Assert.False(playing.Paused);
        Assert.InRange(playing.Position.TotalSeconds, 19.5, 22);
        Assert.Equal(PlayerStatus.Playing, (await SnapshotAsync()).Status);
    }

    [Fact]
    public async Task Caixa_desconectada_pausa_e_retoma_da_mesma_posicao()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        _h.Bluetooth.IsConnected = false;
        await _h.RunUntilAsync("10:01:00");
        Assert.True(_h.A.Paused);
        Assert.InRange(_h.A.Position.TotalSeconds, 19.5, 20.5);
        Assert.Equal(PlayerStatus.Paused, (await SnapshotAsync()).Status);

        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(_h.A.Paused);
        Assert.InRange(_h.A.Position.TotalSeconds, 19.5, 21);
        Assert.True(PlayerHarness.GainAt(_h.A, _h.Now) < 0.9);
        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        Assert.InRange(PlayerHarness.GainAt(_h.A, _h.Now), 0.99, 1.01);
    }

    [Fact]
    public async Task Pausa_do_admin_vale_ate_tocar()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:20");

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.Pause))).Ok);
        await _h.RunForAsync(TimeSpan.FromSeconds(1.5));
        Assert.True(_h.A.Paused);
        Assert.InRange(_h.A.VolumeHistory[^1].Volume, 0, 0.5);

        await _h.RunForAsync(TimeSpan.FromSeconds(30));
        Assert.True(_h.A.Paused);
        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.Play))).Ok);
        await _h.RunForAsync(TimeSpan.FromSeconds(1.5));
        Assert.False(_h.A.Paused);
        Assert.InRange(_h.A.Position.TotalSeconds, 21, 23);
        Assert.Single(_h.TrackStarts);
    }

    [Fact]
    public async Task Pausa_do_admin_termina_quando_a_proxima_janela_comeca()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) },
            [PlayerHarness.Window("10:00", "10:01", "manha"), PlayerHarness.Window("10:02", "11:00", "manha")]);
        await _h.RunUntilAsync("10:00:20");

        await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.Pause));
        await _h.RunUntilAsync("10:01:30");
        Assert.NotNull(_h.State.State.PausedByAdminAt);

        await _h.RunUntilAsync("10:02:01");
        Assert.Null(_h.State.State.PausedByAdminAt);
        Assert.Equal(PlayerStatus.Playing, (await SnapshotAsync()).Status);
    }

    [Fact]
    public async Task Pular_faz_transicao_de_1_s_e_avanca_a_fila()
    {
        var t1 = _h.AddTrack("Um", 120);
        var t2 = _h.AddTrack("Dois", 120);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) });
        await _h.RunUntilAsync("10:00:10");

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.Next))).Ok);
        var skippedAt = _h.Now;
        await _h.RunForAsync(TimeSpan.FromSeconds(0.5));
        var halfway = PlayerHarness.GainAt(_h.A, _h.Now);
        Assert.InRange(halfway, 0.3, 0.95);

        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));
        Assert.Equal(_h.PathOf(t2), _h.B.CurrentFile);
        Assert.Null(_h.A.CurrentFile);
        Assert.InRange(_h.TrackStarts[1].At - skippedAt, TimeSpan.Zero, Step);
        Assert.Equal("it-2", _h.State.State.Playlists["manha"].LastItemId);
    }

    [Fact]
    public async Task Tocar_agora_nao_altera_a_fila()
    {
        var t1 = _h.AddTrack("Um", 30);
        var t2 = _h.AddTrack("Dois", 30);
        var t3 = _h.AddTrack("Três", 30);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2, t3) });
        await _h.RunUntilAsync("10:00:10");

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.PlayNow, Hash: t3))).Ok);
        await _h.RunUntilAsync("10:00:45");

        Assert.Equal([t1, t3, t2], _h.TrackStarts.Select(s => s.Hash));
    }

    [Fact]
    public async Task Volume_ao_vivo_muda_em_rampa_e_persiste()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("10:00:10");

        await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.SetVolume, Volume: 40));
        await _h.RunForAsync(TimeSpan.FromMilliseconds(500));
        Assert.InRange(_h.A.Volume, 45, 65);
        await _h.RunForAsync(TimeSpan.FromSeconds(1));
        Assert.InRange(_h.A.Volume, 39.9, 40.1);
        Assert.Equal(40, _h.State.State.VolumeOverride);
    }

    [Fact]
    public async Task Tocar_oferta_agora_toca_mesmo_fora_da_agenda()
    {
        var t1 = _h.AddTrack("Um", 30);
        var offer = _h.AddTrack("Picanha", 10, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) },
            spots: Spots(offer));
        await _h.RunUntilAsync("09:59:10");

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.PlayOffer, OfferId: "of-1"))).Ok);
        await _h.RunForAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(_h.PathOf(offer), _h.Spot.CurrentFile);
        Assert.Null(_h.A.CurrentFile);
    }

    [Fact]
    public async Task Som_de_teste_toca_no_mpv_de_ofertas()
    {
        var t1 = _h.AddTrack("Um", 30);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        await _h.RunUntilAsync("09:59:10");

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.TestSound))).Ok);
        await _h.RunForAsync(TimeSpan.FromMilliseconds(300));

        Assert.EndsWith("som-de-teste.wav", _h.Spot.CurrentFile);
    }

    [Fact]
    public async Task Som_de_teste_com_o_mpv_de_ofertas_fechado_nao_culpa_a_caixa()
    {
        _h.Spot.FailsToStart = true;
        await _h.RunUntilAsync("09:59:10");

        var result = await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.TestSound));

        Assert.False(result.Ok);
        Assert.Contains("mpv das ofertas", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_mpv_de_musica_aberto_o_motivo_aparece_no_status_e_a_musica_comeca_quando_abrir()
    {
        var t1 = _h.AddTrack("Um", 300);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) });
        _h.A.FailsToStart = true;
        _h.B.FailsToStart = true;
        await _h.RunUntilAsync("10:00:10");

        var snapshot = _h.Engine.Snapshot;
        Assert.Equal(PlayerStatus.Stopped, snapshot.Status);
        Assert.Equal("Nenhum mpv de música aberto; tentando de novo", snapshot.Reason);

        _h.A.FailsToStart = false;
        _h.B.FailsToStart = false;
        await _h.RunForAsync(TimeSpan.FromSeconds(7));
        Assert.NotNull(new[] { _h.A, _h.B }.SingleOrDefault(m => m.CurrentFile == _h.PathOf(t1)));
    }

    [Fact]
    public async Task Mpv_da_faixa_que_entrava_morre_na_mixagem_e_ela_continua_no_outro()
    {
        var t1 = _h.AddTrack("Um", 60);
        var t2 = _h.AddTrack("Dois", 60);
        _h.Publish(new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) });
        await _h.RunUntilAsync("10:00:57");
        Assert.Equal(_h.PathOf(t2), _h.B.CurrentFile);

        _h.B.Crash();
        await _h.RunForAsync(TimeSpan.FromSeconds(1.5));

        Assert.Equal(_h.PathOf(t2), _h.A.CurrentFile);
        Assert.InRange(_h.A.Position.TotalSeconds, 1.5, 4);
        Assert.InRange(PlayerHarness.GainAt(_h.A, _h.Now), 0.99, 1.01);
        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));
    }

    [Fact]
    public async Task Musica_que_termina_durante_a_oferta_entra_no_nivel_do_ducking()
    {
        var t1 = _h.AddTrack("Um", 30);
        var t2 = _h.AddTrack("Dois", 60);
        var offer = _h.AddTrack("Picanha", 15, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) },
            spots: Spots(offer, new AnchorDuringPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", ItemId = "it-1", OffsetMs = 20_000 }));

        await _h.RunUntilAsync("10:00:30");
        var mixStart = _h.TrackStarts[1].At;
        Assert.InRange(PlayerHarness.VolumeAt(_h.B, mixStart + TimeSpan.FromSeconds(5.2)), 46.5, 47.1);

        await _h.RunUntilAsync("10:00:40");
        Assert.InRange(PlayerHarness.VolumeAt(_h.B, _h.Now), 69.9, 70.1);
    }

    [Fact]
    public async Task Caixa_cai_durante_a_oferta_interrompe_e_nao_repete()
    {
        var t1 = _h.AddTrack("Um", 120);
        var offer = _h.AddTrack("Picanha", 10, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1) },
            spots: Spots(offer, new AnchorDuringPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", ItemId = "it-1", OffsetMs = 20_000 }));
        await _h.RunUntilAsync("10:00:24");

        _h.Bluetooth.IsConnected = false;
        await _h.RunForAsync(TimeSpan.FromSeconds(5));
        _h.Bluetooth.IsConnected = true;
        await _h.RunForAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SpotResult.Interrupted, _h.Messages.OfType<SpotFinishedMessage>().Single().Result);
        Assert.Single(_h.Messages.OfType<SpotStartedMessage>());
        Assert.InRange(PlayerHarness.VolumeAt(_h.A, _h.Now), 69.9, 70.1);
    }

    [Fact]
    public async Task Pular_durante_oferta_entre_musicas_interrompe_e_segue()
    {
        var t1 = _h.AddTrack("Um", 30);
        var t2 = _h.AddTrack("Dois", 60);
        var offer = _h.AddTrack("Picanha", 20, TrackKind.Offer);
        _h.Publish(
            new Dictionary<string, PlaylistDefinition> { ["manha"] = PlayerHarness.Playlist(false, t1, t2) },
            spots: Spots(offer, new AnchorBetweenPlacement { Id = "pl-1", OfferId = "of-1", PlaylistId = "manha", AfterItemId = "it-1" }));
        await _h.RunUntilAsync("10:00:35");
        Assert.Equal(_h.PathOf(offer), _h.Spot.CurrentFile);

        Assert.True((await _h.CommandAsync(new PlayerCommand(PlayerCommandKind.Next))).Ok);
        await _h.RunForAsync(TimeSpan.FromSeconds(1));

        Assert.Null(_h.Spot.CurrentFile);
        Assert.Equal([t1, t2], _h.TrackStarts.Select(s => s.Hash));
        Assert.Equal(SpotResult.Interrupted, _h.Messages.OfType<SpotFinishedMessage>().Single().Result);
    }

    private async Task<PlayerSnapshot> SnapshotAsync()
    {
        await _h.RunForAsync(TimeSpan.FromMilliseconds(600));
        return _h.Engine.Snapshot;
    }

    private static SpotsSection Spots(string offerHash, params Placement[] placements) => new()
    {
        MinOfferGapS = 0,
        Offers = new Dictionary<string, OfferDefinition>
        {
            ["of-1"] = new()
            {
                Hash = offerHash,
                Name = "Picanha R$ 69,90",
                ValidFrom = PlayerHarness.Start.AddDays(-1),
                ValidUntil = PlayerHarness.Start.AddDays(2),
            },
        },
        Placements = placements,
    };
}
