using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Player;

/// <summary>
/// Toca uma oferta durante a música com ducking (seção 10.3.3): música desce até o nível de ducking
/// em 800 ms, a oferta toca no volume da loja, e a música volta em 1.500 ms.
/// </summary>
public sealed class SpotDucker(TimeProvider time, ILogger<SpotDucker> logger)
{
    public static readonly TimeSpan DuckDown = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan Restore = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan MaxSpotDuration = TimeSpan.FromSeconds(150);

    /// <summary>Indica se um ducking está em andamento; o volume imposto à música é o da rampa.</summary>
    public bool IsDucking { get; private set; }

    /// <summary><c>duckLevel</c> é relativo ao volume da loja: loja 70 e ducking 30% → 21.</summary>
    public static double DuckedVolume(double storeVolume, int duckLevel) => storeVolume * Math.Clamp(duckLevel, 0, 100) / 100.0;

    public async Task<SpotResult> PlayDuringAsync(
        IMpvInstance music,
        IMpvInstance spot,
        string spotPath,
        double storeVolume,
        int duckLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(music);
        ArgumentNullException.ThrowIfNull(spot);
        var ducked = DuckedVolume(storeVolume, duckLevel);
        var finished = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(object? sender, MpvEventArgs e)
        {
            if (e.Name == "end-file")
            {
                finished.TrySetResult(e.Reason);
            }
        }

        void OnExited(object? sender, EventArgs e) => finished.TrySetResult("crash");

        IsDucking = true;
        spot.EventReceived += OnEvent;
        spot.Exited += OnExited;
        try
        {
            logger.LogInformation("Oferta '{Spot}': ducking {Store} → {Ducked}", Path.GetFileName(spotPath), storeVolume, ducked);
            await VolumeRamp.RunAsync(music.SetVolumeAsync, storeVolume, ducked, DuckDown, time, cancellationToken);

            await spot.SetVolumeAsync(storeVolume, cancellationToken);
            await spot.LoadFileAsync(spotPath, append: false, cancellationToken);

            string? reason;
            try
            {
                reason = await finished.Task.WaitAsync(MaxSpotDuration, time, cancellationToken);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Oferta não terminou em {Max}; interrompendo", MaxSpotDuration);
                await spot.CommandAsync(["stop"], cancellationToken);
                reason = "timeout";
            }

            return reason == "eof" ? SpotResult.Played : SpotResult.Interrupted;
        }
        finally
        {
            spot.EventReceived -= OnEvent;
            spot.Exited -= OnExited;
            try
            {
                await VolumeRamp.RunAsync(music.SetVolumeAsync, ducked, storeVolume, Restore, time, CancellationToken.None);
            }
            catch (MpvCommandException ex)
            {
                logger.LogWarning(ex, "Falha ao restaurar o volume da música após a oferta");
            }

            IsDucking = false;
        }
    }
}
