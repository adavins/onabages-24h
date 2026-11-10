using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Tests.Engine;

/// <summary>Quan sona cada esdeveniment (fase 2, pas 2; docs/planificador.md, secció 12; D-057).</summary>
public class RecurrencesTests
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static DateTimeOffset Local(int y, int m, int d, int h = 0, int min = 0) =>
        new(new DateTime(y, m, d, h, min, 0), Madrid.GetUtcOffset(new DateTime(y, m, d, h, min, 0)));

    private static List<DateOnly> Days(ScheduledEvent ev, DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
            if (Recurrences.OccursOn(ev, d)) days.Add(d);
        return days;
    }

    private static ScheduledEvent Event(Func<ScheduledEvent, ScheduledEvent> change) =>
        change(new ScheduledEvent { Name = "Prova", Mode = EventMode.Cut, StartTime = new TimeOnly(10, 0) });

    // --- Dies ---

    [Fact]
    public void Setmanal_de_dilluns_a_divendres()
    {
        var ev = Event(e => e with { DaysMask = Weekdays.MondayToFriday });

        var days = Days(ev, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 22));

        Assert.Equal(5, days.Count);
        Assert.All(days, d => Assert.True(d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
    }

    [Fact]
    public void Cada_15_dies_com_a_la_maqueta_amb_l_excepcio_de_cap_d_any()
    {
        // «El Magazín de la Comarca»: divendres cada 2 setmanes des del 6/11/2026, excepte l'1/1/2027
        var ev = Event(e => e with
        {
            Recurrence = EventRecurrence.EveryNWeeks,
            DaysMask = Weekdays.Bit(DayOfWeek.Friday),
            IntervalWeeks = 2,
            AnchorDate = new DateOnly(2026, 11, 6),
            ExcludedDates = [new DateOnly(2027, 1, 1)],
        });

        var next = Recurrences.Next(ev, Local(2026, 11, 16), 5, Madrid);

        Assert.Equal(
            [new DateOnly(2026, 11, 20), new DateOnly(2026, 12, 4), new DateOnly(2026, 12, 18), new DateOnly(2027, 1, 15), new DateOnly(2027, 1, 29)],
            next.Select(o => o.Date));
    }

    [Fact]
    public void Cada_N_setmanes_tambe_compta_abans_de_l_ancora_i_amb_diversos_dies()
    {
        var ev = Event(e => e with
        {
            Recurrence = EventRecurrence.EveryNWeeks,
            DaysMask = Weekdays.Bit(DayOfWeek.Tuesday) | Weekdays.Bit(DayOfWeek.Friday),
            IntervalWeeks = 2,
            AnchorDate = new DateOnly(2026, 11, 6), // divendres
        });

        // La setmana de l'àncora (dt 3 i dv 6), no la següent, sí la de dues setmanes abans (dt 20/10 i dv 23/10)
        Assert.Equal(
            [new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 23), new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 6), new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 20)],
            Days(ev, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 22)));
    }

    [Fact]
    public void Primer_i_tercer_dissabte_de_mes()
    {
        var ev = Event(e => e with
        {
            Recurrence = EventRecurrence.MonthlyOrdinal,
            DaysMask = Weekdays.Bit(DayOfWeek.Saturday),
            Ordinals = [1, 3],
        });

        Assert.Equal(
            [new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 21), new DateOnly(2026, 12, 5), new DateOnly(2026, 12, 19)],
            Days(ev, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Ultim_diumenge_de_mes_tambe_quan_n_hi_ha_cinc()
    {
        var ev = Event(e => e with
        {
            Recurrence = EventRecurrence.MonthlyOrdinal,
            DaysMask = Weekdays.Bit(DayOfWeek.Sunday),
            Ordinals = [-1],
        });

        // Novembre 2026: l'últim és el 29; gener 2027 té cinc diumenges i l'últim és el 31
        Assert.Equal(
            [new DateOnly(2026, 11, 29), new DateOnly(2026, 12, 27), new DateOnly(2027, 1, 31)],
            Days(ev, new DateOnly(2026, 11, 1), new DateOnly(2027, 1, 31)));
    }

    [Fact]
    public void Dies_del_mes_amb_l_ultim_i_els_mesos_curts()
    {
        var ev = Event(e => e with { Recurrence = EventRecurrence.DaysOfMonth, DaysMask = 0, MonthDays = [1, 15, -1] });
        var day31 = Event(e => e with { Recurrence = EventRecurrence.DaysOfMonth, DaysMask = 0, MonthDays = [31] });

        Assert.Equal(
            [new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 15), new DateOnly(2027, 2, 28)],
            Days(ev, new DateOnly(2027, 2, 1), new DateOnly(2027, 2, 28)));
        // El dia 31 no existeix al novembre: aquell mes no sona (per a l'últim dia, «last»)
        Assert.Empty(Days(day31, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        Assert.Equal([new DateOnly(2026, 12, 31)], Days(day31, new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)));
    }

    [Fact]
    public void Dates_soltes()
    {
        var ev = Event(e => e with
        {
            Recurrence = EventRecurrence.Dates,
            DaysMask = 0,
            IncludedDates = [new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 5)],
        });

        Assert.Equal(
            [new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 5)],
            Days(ev, new DateOnly(2026, 12, 1), new DateOnly(2027, 1, 31)));
    }

    [Fact]
    public void Rang_de_dates_i_excepcions()
    {
        var ev = Event(e => e with
        {
            StartDate = new DateOnly(2026, 11, 18),
            EndDate = new DateOnly(2026, 11, 21),
            ExcludedDates = [new DateOnly(2026, 11, 19)],
        });

        Assert.Equal(
            [new DateOnly(2026, 11, 18), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 21)],
            Days(ev, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
    }

    // --- Hores dins del dia ---

    [Fact]
    public void Cada_hora_a_les_hores_triades_al_minut_de_l_hora_d_inici()
    {
        // «:30 Senyal mitja hora», de 7 a 9 h
        var ev = Event(e => e with
        {
            StartTime = new TimeOnly(0, 30),
            Repeat = EventRepeat.EveryHour,
            HoursMask = (1 << 7) | (1 << 8) | (1 << 9),
        });

        Assert.Equal([new TimeOnly(7, 30), new TimeOnly(8, 30), new TimeOnly(9, 30)], Recurrences.TimesOfDay(ev));
        Assert.Equal(24, Recurrences.TimesOfDay(ev with { HoursMask = null }).Count);
    }

    [Fact]
    public void Cada_N_minuts_fins_a_mitjanit()
    {
        var ev = Event(e => e with { StartTime = new TimeOnly(23, 0), Repeat = EventRepeat.EveryNMinutes, IntervalMinutes = 15 });

        Assert.Equal(
            [new TimeOnly(23, 0), new TimeOnly(23, 15), new TimeOnly(23, 30), new TimeOnly(23, 45)],
            Recurrences.TimesOfDay(ev));
    }

    [Fact]
    public void Entre_dues_hores_inclou_el_principi_i_no_el_final()
    {
        var ev = Event(e => e with { StartTime = new TimeOnly(0, 0), Repeat = EventRepeat.EveryHour });

        var list = Recurrences.Between(ev, Local(2026, 11, 20, 10), Local(2026, 11, 20, 13), Madrid).ToList();

        Assert.Equal([Local(2026, 11, 20, 10), Local(2026, 11, 20, 11), Local(2026, 11, 20, 12)], list.Select(o => o.At));
    }

    // --- Canvis d'hora (D-057) ---

    [Fact]
    public void L_hora_que_no_existeix_no_sona_i_queda_marcada()
    {
        // 28/3/2027: a les 02:00 es passa a les 03:00
        var ev = Event(e => e with { StartTime = new TimeOnly(2, 30) });

        var day = Recurrences.Between(ev, Local(2027, 3, 27), Local(2027, 3, 30), Madrid).ToList();

        Assert.Equal(3, day.Count);
        var skipped = Assert.Single(day, o => o.SkippedClockChange);
        Assert.Equal(new DateOnly(2027, 3, 28), skipped.Date);
        Assert.Equal(new TimeOnly(2, 30), skipped.LocalTime);
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), skipped.At); // el moment del salt (03:00 d'estiu)
        // Els altres dies sonen a les 02:30 de debò
        Assert.All(day.Where(o => !o.SkippedClockChange), o => Assert.Equal(new TimeOnly(2, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.At, Madrid).DateTime)));
    }

    [Fact]
    public void L_hora_repetida_sona_nomes_la_primera_vegada()
    {
        // 25/10/2026: a les 03:00 es torna a les 02:00 (de 02:00 a 03:00 passa dues vegades)
        var ev = Event(e => e with { StartTime = new TimeOnly(2, 30) });

        var day = Recurrences.Between(ev, Local(2026, 10, 25), Local(2026, 10, 26), Madrid).ToList();

        var only = Assert.Single(day);
        Assert.False(only.SkippedClockChange);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), only.At); // 02:30 d'estiu (+2)
    }

    [Fact]
    public void Cada_hora_els_dies_de_canvi_d_hora()
    {
        var ev = Event(e => e with { StartTime = new TimeOnly(0, 0), Repeat = EventRepeat.EveryHour });

        var spring = Recurrences.Between(ev, Local(2027, 3, 28), Local(2027, 3, 29), Madrid).ToList();
        var autumn = Recurrences.Between(ev, Local(2026, 10, 25), Local(2026, 10, 26), Madrid).ToList();

        // Primavera: 24 hores programades, la de les 02:00 omesa
        Assert.Equal(24, spring.Count);
        Assert.Equal([new TimeOnly(2, 0)], spring.Where(o => o.SkippedClockChange).Select(o => o.LocalTime));
        // Tardor: 24 emissions (la de les 02:00 només una vegada), totes en instants diferents
        Assert.Equal(24, autumn.Count);
        Assert.Equal(24, autumn.Select(o => o.At).Distinct().Count());
    }

    [Fact]
    public void La_mateixa_hora_local_abans_i_despres_del_canvi()
    {
        // Les 10:00 cada dia són sempre les 10:00 locals, encara que entremig canviï l'hora
        var ev = Event(e => e);

        var list = Recurrences.Between(ev, Local(2027, 3, 27), Local(2027, 3, 30), Madrid).ToList();

        Assert.Equal(3, list.Count);
        Assert.All(list, o => Assert.Equal(10, TimeZoneInfo.ConvertTime(o.At, Madrid).Hour));
        Assert.Equal(TimeSpan.FromHours(1), list[0].At.Offset);
        Assert.Equal(TimeSpan.FromHours(2), list[1].At.Offset);
    }

    // --- Accions ---

    [Fact]
    public void Les_accions_amb_dies_propis_nomes_aquells_dies()
    {
        var ev = Event(e => e with
        {
            DaysMask = Weekdays.MondayToFriday,
            Actions =
            [
                new EventAction { Type = EventActionType.Track, Params = """{"songId":1}""" },
                new EventAction { Type = EventActionType.Track, Params = """{"songId":2}""", DaysMask = Weekdays.Bit(DayOfWeek.Tuesday) },
                new EventAction { Type = EventActionType.Track, Params = """{"songId":3}""" },
            ],
        });

        Assert.Equal(2, Recurrences.ActionsFor(ev, new DateOnly(2026, 11, 16)).Count); // dilluns
        Assert.Equal(3, Recurrences.ActionsFor(ev, new DateOnly(2026, 11, 17)).Count); // dimarts
    }
}
