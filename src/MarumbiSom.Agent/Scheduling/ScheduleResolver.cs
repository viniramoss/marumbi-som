using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Scheduling;

/// <summary>Uma ocorrência concreta de uma janela de agenda, com início e fim absolutos.</summary>
/// <param name="ContinuesAfterEnd">Outra janela começa exatamente no fim desta (troca de playlist sem silêncio).</param>
public sealed record ScheduleWindow(ScheduleEntry Entry, DateTimeOffset Start, DateTimeOffset End, bool ContinuesAfterEnd)
{
    public string PlaylistId => Entry.PlaylistId;
}

/// <summary>
/// Resolve a janela de agenda ativa (seção 10.2, regra 1). Dias: 0 = domingo … 6 = sábado, no fuso da loja.
/// O início é inclusivo e o fim exclusivo. Fim "24:00" (ou igual ao início) vai até a meia-noite seguinte;
/// fim menor que o início atravessa a meia-noite e pertence ao dia do início.
/// </summary>
public static class ScheduleResolver
{
    public static ScheduleWindow? ActiveAt(IReadOnlyList<ScheduleEntry> schedules, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(zone);
        var window = Find(schedules, zone, now);
        if (window is null)
        {
            return null;
        }

        var (entry, start, end) = window.Value;
        return new ScheduleWindow(entry, start, end, Find(schedules, zone, end) is not null);
    }

    /// <summary>Início da próxima janela depois de <paramref name="now"/>, procurando até 8 dias à frente.</summary>
    public static DateTimeOffset? NextStartAfter(IReadOnlyList<ScheduleEntry> schedules, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        DateTimeOffset? best = null;
        for (var day = 0; day <= 8; day++)
        {
            foreach (var (_, start, _) in Occurrences(schedules, zone, today.AddDays(day)))
            {
                if (start > now && (best is null || start < best))
                {
                    best = start;
                }
            }
        }

        return best;
    }

    /// <summary>Indica se alguma janela começou em (<paramref name="after"/>, <paramref name="now"/>]; usado para encerrar a pausa do administrador.</summary>
    public static bool AnyStartBetween(IReadOnlyList<ScheduleEntry> schedules, TimeZoneInfo zone, DateTimeOffset after, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(zone);
        if (now <= after)
        {
            return false;
        }

        var first = TimeZoneInfo.ConvertTime(after, zone).Date.AddDays(-1);
        var last = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (Occurrences(schedules, zone, date).Any(o => o.Start > after && o.Start <= now))
            {
                return true;
            }
        }

        return false;
    }

    public static TimeZoneInfo ResolveZone(string? timezone, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                logger.LogError(ex, "Fuso '{Timezone}' do manifesto não encontrado; usando o fuso do Windows", timezone);
            }
        }

        return TimeZoneInfo.Local;
    }

    private static (ScheduleEntry Entry, DateTimeOffset Start, DateTimeOffset End)? Find(
        IReadOnlyList<ScheduleEntry> schedules, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        (ScheduleEntry Entry, DateTimeOffset Start, DateTimeOffset End)? found = null;
        foreach (var date in new[] { today.AddDays(-1), today })
        {
            foreach (var occurrence in Occurrences(schedules, zone, date))
            {
                // Agendas não se sobrepõem (o Hub valida); se acontecer, vale a que começou por último.
                if (occurrence.Start <= now && now < occurrence.End && (found is null || occurrence.Start > found.Value.Start))
                {
                    found = occurrence;
                }
            }
        }

        return found;
    }

    private static IEnumerable<(ScheduleEntry Entry, DateTimeOffset Start, DateTimeOffset End)> Occurrences(
        IReadOnlyList<ScheduleEntry> schedules, TimeZoneInfo zone, DateTime date)
    {
        foreach (var entry in schedules)
        {
            if (!entry.Days.Contains((int)date.DayOfWeek))
            {
                continue;
            }

            var start = date + entry.Start.ToTimeSpan();
            var end = entry.End == TimeOnly.MaxValue || entry.End == entry.Start
                ? date.AddDays(1)
                : entry.End < entry.Start ? date.AddDays(1) + entry.End.ToTimeSpan() : date + entry.End.ToTimeSpan();
            yield return (entry, ToOffset(start, zone), ToOffset(end, zone));
        }
    }

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        // Horário inexistente (início do horário de verão) avança para o primeiro instante válido.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));
    }
}
