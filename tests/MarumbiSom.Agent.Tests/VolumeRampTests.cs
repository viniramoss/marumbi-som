using MarumbiSom.Agent.Player;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Agent.Tests;

public class VolumeRampTests
{
    [Theory]
    [InlineData(70, 21, 800)]
    [InlineData(21, 70, 1500)]
    public async Task Rampa_e_monotonica_termina_no_alvo_e_tem_passos_de_no_maximo_40_ms(double from, double to, int durationMs)
    {
        var time = new FakeTimeProvider();
        var samples = new List<(TimeSpan At, double Volume)>();
        var start = time.GetTimestamp();
        using var sampled = new SemaphoreSlim(0);

        var ramp = Task.Run(() => VolumeRamp.RunAsync(
            (v, _) =>
            {
                samples.Add((time.GetElapsedTime(start), v));
                sampled.Release();
                return Task.CompletedTask;
            },
            from,
            to,
            TimeSpan.FromMilliseconds(durationMs),
            time,
            CancellationToken.None));

        // Avança o relógio falso um passo por amostra, dando tempo para a rampa registrar o próximo Task.Delay.
        while (!ramp.IsCompleted)
        {
            if (await sampled.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                await Task.Delay(2);
                time.Advance(VolumeRamp.StepInterval);
            }
        }

        await ramp;
        Assert.Equal(from, samples[0].Volume);
        Assert.Equal(to, samples[^1].Volume);
        var steps = samples.Zip(samples.Skip(1)).ToList();
        Assert.All(steps, s => Assert.True(s.Second.At - s.First.At <= TimeSpan.FromMilliseconds(40)));
        Assert.All(steps, s => Assert.True(to < from ? s.Second.Volume <= s.First.Volume : s.Second.Volume >= s.First.Volume));
        // Sem degraus: nenhum passo muda mais que 5% da excursão total.
        Assert.All(steps, s => Assert.True(Math.Abs(s.Second.Volume - s.First.Volume) <= Math.Abs(to - from) * 0.05 + 1e-9));
    }

    [Theory]
    [InlineData(70, 30, 46.86)]
    [InlineData(100, 30, 66.94)]
    [InlineData(50, 0, 0)]
    [InlineData(50, 100, 50)]
    public void Nivel_de_ducking_considera_a_curva_cubica_do_mpv(double store, int duckLevel, double expected)
    {
        Assert.Equal(expected, SpotDucker.DuckedVolume(store, duckLevel), precision: 2);
    }

    [Theory]
    [InlineData(70, 30)]
    [InlineData(40, 30)]
    [InlineData(100, 50)]
    public void Ducking_reduz_a_amplitude_da_musica_para_a_fracao_configurada(double store, int duckLevel)
    {
        var ratio = MpvVolume.ToGain(SpotDucker.DuckedVolume(store, duckLevel)) / MpvVolume.ToGain(store);

        Assert.Equal(duckLevel / 100.0, ratio, precision: 6);
    }
}
