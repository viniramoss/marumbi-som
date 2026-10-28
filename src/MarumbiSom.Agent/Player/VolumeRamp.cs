namespace MarumbiSom.Agent.Player;

/// <summary>
/// Rampa linear de volume (regra 16 e seção 10.3.3): nunca muda em degrau. O valor é recalculado
/// a partir do tempo decorrido a cada 20 ms, garantindo passos de no máximo 40 ms mesmo com o
/// temporizador do Windows atrasando.
/// </summary>
public static class VolumeRamp
{
    public static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(20);

    public static async Task RunAsync(
        Func<double, CancellationToken, Task> setVolume,
        double from,
        double to,
        TimeSpan duration,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setVolume);
        ArgumentNullException.ThrowIfNull(time);
        var start = time.GetTimestamp();
        while (true)
        {
            var elapsed = time.GetElapsedTime(start);
            if (elapsed >= duration)
            {
                await setVolume(to, cancellationToken);
                return;
            }

            var fraction = elapsed / duration;
            await setVolume(from + ((to - from) * fraction), cancellationToken);
            await Task.Delay(StepInterval, time, cancellationToken);
        }
    }
}
