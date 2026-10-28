using MarumbiSom.Agent.Scheduling;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Tests.Scheduling;

public class ScheduleResolverTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    // 23/10/2026 é sexta-feira (5).
    private static DateTimeOffset At(int day, string time) =>
        new(new DateTime(2026, 10, day) + TimeSpan.Parse(time, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(-3));

    private static ScheduleEntry Entry(string start, string end, string playlist, params int[] days) =>
        new(days.Length == 0 ? [0, 1, 2, 3, 4, 5, 6] : days, TimeOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            end == "24:00" ? TimeOnly.MaxValue : TimeOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture), playlist);

    [Fact]
    public void Inicio_inclusivo_e_fim_exclusivo()
    {
        ScheduleEntry[] schedules = [Entry("08:00", "12:00", "manha")];

        Assert.Null(ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "07:59:59")));
        Assert.Equal("manha", ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "08:00"))?.PlaylistId);
        Assert.Equal("manha", ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "11:59:59"))?.PlaylistId);
        Assert.Null(ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "12:00")));
    }

    [Fact]
    public void Respeita_os_dias_da_semana()
    {
        ScheduleEntry[] schedules = [Entry("08:00", "12:00", "semana", 1, 2, 3, 4, 5, 6)];

        Assert.NotNull(ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(24, "09:00"))); // sábado
        Assert.Null(ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(25, "09:00"))); // domingo
    }

    [Fact]
    public void Janelas_coladas_continuam_sem_fade()
    {
        ScheduleEntry[] schedules = [Entry("08:00", "12:00", "manha"), Entry("12:00", "20:00", "tarde")];

        var manha = ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "11:00"))!;
        var tarde = ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "13:00"))!;

        Assert.True(manha.ContinuesAfterEnd);
        Assert.Equal(At(23, "12:00"), manha.End);
        Assert.False(tarde.ContinuesAfterEnd);
        Assert.Equal(At(23, "20:00"), tarde.End);
    }

    [Fact]
    public void Janela_que_atravessa_a_meia_noite_pertence_ao_dia_do_inicio()
    {
        ScheduleEntry[] schedules = [Entry("22:00", "02:00", "noite", 5)];

        var window = ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(24, "01:00"));

        Assert.Equal("noite", window?.PlaylistId);
        Assert.Equal(At(24, "02:00"), window!.End);
        Assert.Null(ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(25, "01:00")));
    }

    [Fact]
    public void Fim_24h_vai_ate_a_meia_noite_e_emenda_no_dia_seguinte()
    {
        ScheduleEntry[] schedules = [Entry("00:00", "24:00", "sempre")];

        var window = ScheduleResolver.ActiveAt(schedules, SaoPaulo, At(23, "23:59"))!;

        Assert.Equal(At(24, "00:00"), window.End);
        Assert.True(window.ContinuesAfterEnd);
    }

    [Fact]
    public void Proximo_inicio_e_inicio_entre_dois_instantes()
    {
        ScheduleEntry[] schedules = [Entry("08:00", "12:00", "manha", 1, 2, 3, 4, 5), Entry("14:00", "18:00", "tarde", 1, 2, 3, 4, 5)];

        Assert.Equal(At(23, "14:00"), ScheduleResolver.NextStartAfter(schedules, SaoPaulo, At(23, "12:30")));
        Assert.Equal(At(26, "08:00"), ScheduleResolver.NextStartAfter(schedules, SaoPaulo, At(23, "19:00"))); // segunda
        Assert.True(ScheduleResolver.AnyStartBetween(schedules, SaoPaulo, At(23, "12:30"), At(23, "14:00")));
        Assert.False(ScheduleResolver.AnyStartBetween(schedules, SaoPaulo, At(23, "14:00"), At(23, "17:00")));
    }

    [Fact]
    public void Fuso_desconhecido_usa_o_do_windows()
    {
        var zone = ScheduleResolver.ResolveZone("Marte/Olympus", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(TimeZoneInfo.Local, zone);
    }
}
