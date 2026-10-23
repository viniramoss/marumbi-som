namespace MarumbiSom.Agent.Player;

/// <summary>
/// Conversão entre o volume do mpv (0–100) e o ganho linear de amplitude. O mpv aplica curva cúbica:
/// ganho = (volume / 100)³ (player/audio.c, <c>softvol</c>).
/// </summary>
public static class MpvVolume
{
    public static double ToGain(double volume) => Math.Pow(Math.Clamp(volume, 0, 100) / 100.0, 3);

    public static double FromGain(double gain) => 100.0 * Math.Cbrt(Math.Clamp(gain, 0, 1));
}
