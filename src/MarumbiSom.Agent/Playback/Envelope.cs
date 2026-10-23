namespace MarumbiSom.Agent.Playback;

public enum EnvelopeCurve
{
    /// <summary>Linear no volume do mpv (raiz cúbica do ganho), igual à rampa da Etapa 1 e à percepção de volume.</summary>
    Loudness,

    /// <summary>Entrada da mixagem de potência constante: ganho segue sen(π·x/2).</summary>
    EqualPowerIn,

    /// <summary>Saída da mixagem de potência constante: ganho segue cos(π·x/2).</summary>
    EqualPowerOut,
}

/// <summary>
/// Ganho (amplitude, 0–1) que varia de <see cref="From"/> a <see cref="To"/> ao longo do tempo. O player
/// multiplica os envelopes (volume da loja × mixagem × ducking × fim do horário × pausa) e converte o
/// produto para o volume cúbico do mpv. Assim nenhuma mudança de volume acontece em degrau (regra 16).
/// </summary>
public readonly record struct Envelope(double From, double To, DateTimeOffset Start, TimeSpan Duration, EnvelopeCurve Curve)
{
    public static Envelope Constant(double value) => new(value, value, DateTimeOffset.MinValue, TimeSpan.Zero, EnvelopeCurve.Loudness);

    public static Envelope Ramp(double from, double to, DateTimeOffset start, TimeSpan duration, EnvelopeCurve curve = EnvelopeCurve.Loudness) =>
        new(from, to, start, duration, curve);

    public DateTimeOffset End => Duration <= TimeSpan.Zero ? Start : Start + Duration;

    public bool IsAnimating(DateTimeOffset now) => now < End && From != To;

    public bool IsFinished(DateTimeOffset now) => now >= End;

    public double ValueAt(DateTimeOffset now)
    {
        if (Duration <= TimeSpan.Zero || now >= End)
        {
            return To;
        }

        if (now <= Start)
        {
            return From;
        }

        var x = (now - Start) / Duration;
        return Curve switch
        {
            EnvelopeCurve.EqualPowerIn => From + ((To - From) * Math.Sin(Math.PI / 2 * x)),
            EnvelopeCurve.EqualPowerOut => To + ((From - To) * Math.Cos(Math.PI / 2 * x)),
            _ => Math.Pow(Math.Cbrt(From) + ((Math.Cbrt(To) - Math.Cbrt(From)) * x), 3),
        };
    }

    /// <summary>Nova rampa que parte do valor atual, sem salto.</summary>
    public Envelope RampTo(double to, DateTimeOffset now, TimeSpan duration, EnvelopeCurve curve = EnvelopeCurve.Loudness) =>
        Ramp(ValueAt(now), to, now, duration, curve);
}
