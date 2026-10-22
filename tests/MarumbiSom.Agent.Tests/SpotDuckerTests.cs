using MarumbiSom.Agent.Player;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests;

public class SpotDuckerTests
{
    // Relógio real: as rampas usam Task.Delay e a linha do tempo é medida com tolerância.
    private static readonly TimeProvider Time = TimeProvider.System;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Linha_do_tempo_do_ducking_segue_800_ms_oferta_1500_ms()
    {
        var music = new SimulatedMpvInstance("música", Time, TimeSpan.FromMinutes(4), NullLogger<SimulatedMpvInstance>.Instance);
        var spot = new SimulatedMpvInstance("ofertas", Time, TimeSpan.FromMilliseconds(500), NullLogger<SimulatedMpvInstance>.Instance);
        await music.StartAsync("caixa", 70, CancellationToken.None);
        await spot.StartAsync("caixa", 70, CancellationToken.None);
        await music.LoadFileAsync("musica.m4a", append: false, CancellationToken.None);
        var ducker = new SpotDucker(Time, NullLogger<SpotDucker>.Instance);
        var t0 = Time.GetUtcNow().TimeOfDay;
        DateTimeOffset? spotStartedAt = null;
        spot.EventReceived += (_, e) =>
        {
            if (e.Name == "start-file")
            {
                spotStartedAt = Time.GetUtcNow();
            }
        };

        Assert.Equal(SpotResult.Played, await ducker.PlayDuringAsync(music, spot, "oferta.m4a", 70, 30, CancellationToken.None));
        Assert.False(ducker.IsDucking);

        var history = music.VolumeHistory.Select(h => (At: h.At - t0, h.Volume)).ToList();
        var duckedAt = history.First(h => Math.Abs(h.Volume - 21) < 1e-9).At;
        Assert.InRange(duckedAt, SpotDucker.DuckDown, SpotDucker.DuckDown + Tolerance);

        var spotOffset = spotStartedAt!.Value.TimeOfDay - t0;
        Assert.InRange(spotOffset, SpotDucker.DuckDown, SpotDucker.DuckDown + Tolerance);
        Assert.Equal(70, spot.Volume);

        Assert.Equal(70, history[^1].Volume);
        var restoreDuration = history[^1].At - history.Last(h => Math.Abs(h.Volume - 21) < 1e-9).At;
        Assert.InRange(restoreDuration, SpotDucker.Restore, SpotDucker.Restore + Tolerance);
        Assert.True(history.Min(h => h.Volume) >= 21 - 1e-9);
    }

    [Fact]
    public async Task Oferta_interrompida_quando_o_mpv_de_ofertas_morre()
    {
        var music = new SimulatedMpvInstance("música", Time, TimeSpan.FromMinutes(4), NullLogger<SimulatedMpvInstance>.Instance);
        var spot = new SimulatedMpvInstance("ofertas", Time, TimeSpan.FromSeconds(10), NullLogger<SimulatedMpvInstance>.Instance);
        await music.StartAsync("caixa", 70, CancellationToken.None);
        await spot.StartAsync("caixa", 70, CancellationToken.None);
        var ducker = new SpotDucker(Time, NullLogger<SpotDucker>.Instance);

        var play = ducker.PlayDuringAsync(music, spot, "oferta.m4a", 70, 30, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        spot.Crash();

        Assert.Equal(SpotResult.Interrupted, await play);
        Assert.Equal(70, music.Volume);
    }
}
