using OnaBages24.Data;

namespace OnaBages24.Tests.Data;

/// <summary>Esdeveniments del planificador a la base de dades (fase 2, pas 1).</summary>
public sealed class EventsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Events _events;

    public EventsTests()
    {
        _db = Database.Open(_temp.File("esdeveniments.db"));
        _events = new Events(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private static ScheduledEvent Magazin() => new()
    {
        Name = "El Magazín de la Comarca",
        Mode = EventMode.Cut,
        StartTime = new TimeOnly(10, 0, 0),
        Recurrence = EventRecurrence.EveryNWeeks,
        DaysMask = Weekdays.Bit(DayOfWeek.Friday),
        IntervalWeeks = 2,
        AnchorDate = new DateOnly(2026, 11, 6),
        FadeMs = 2000,
        MaxDurationSeconds = 3600,
        ClearQueue = true,
        ExcludedDates = [new DateOnly(2027, 1, 1)],
        Actions =
        [
            new EventAction { Type = EventActionType.Track, Params = """{"songId":10}""" },
            new EventAction { Type = EventActionType.Track, Params = """{"songId":20}""", DaysMask = Weekdays.Bit(DayOfWeek.Friday) },
            new EventAction { Type = EventActionType.Rotation, Params = """{"rotacioId":1}""" },
        ],
    };

    [Fact]
    public void Desa_i_torna_a_llegir_un_esdeveniment_sencer()
    {
        long id = _events.Save(Magazin());

        var ev = _events.Get(id)!;
        Assert.Equal("El Magazín de la Comarca", ev.Name);
        Assert.Equal(EventMode.Cut, ev.Mode);
        Assert.Equal(new TimeOnly(10, 0, 0), ev.StartTime);
        Assert.Equal(EventRecurrence.EveryNWeeks, ev.Recurrence);
        Assert.Equal(Weekdays.Bit(DayOfWeek.Friday), ev.DaysMask);
        Assert.Equal(2, ev.IntervalWeeks);
        Assert.Equal(new DateOnly(2026, 11, 6), ev.AnchorDate);
        Assert.Equal(2000, ev.FadeMs);
        Assert.Equal(3600, ev.MaxDurationSeconds);
        Assert.True(ev.ClearQueue);
        Assert.True(ev.Enabled);
        Assert.Equal(60, ev.ToleranceSeconds);
        Assert.Equal([new DateOnly(2027, 1, 1)], ev.ExcludedDates);
        Assert.Empty(ev.IncludedDates);
        Assert.True(DateTimeOffset.UtcNow - ev.Modified < TimeSpan.FromMinutes(1));

        // Les accions, en el mateix ordre
        Assert.Equal([EventActionType.Track, EventActionType.Track, EventActionType.Rotation], ev.Actions.Select(a => a.Type));
        Assert.Equal("""{"songId":20}""", ev.Actions[1].Params);
        Assert.Null(ev.Actions[0].DaysMask);
        Assert.Equal(Weekdays.Bit(DayOfWeek.Friday), ev.Actions[1].DaysMask);
    }

    [Fact]
    public void Els_noms_de_la_base_de_dades_son_els_de_l_especificacio()
    {
        long id = _events.Save(Magazin());

        using var command = _db.Command("SELECT mode, recurrencia, repeticio, hora_inici FROM esdeveniment WHERE id = $id;");
        command.Parameters.AddWithValue("$id", id);
        using var r = command.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("TALLA", r.GetString(0));
        Assert.Equal("CADA_N_SETMANES", r.GetString(1));
        Assert.Equal("CAP", r.GetString(2));
        Assert.Equal("10:00:00", r.GetString(3));
    }

    [Fact]
    public void En_tornar_a_desar_se_substitueixen_les_accions_i_les_dates()
    {
        long id = _events.Save(Magazin());
        var ev = _events.Get(id)!;

        _events.Save(ev with
        {
            Name = "Magazín (estiu)",
            Actions = [new EventAction { Type = EventActionType.AutoDj, Params = """{"actiu":true}""" }],
            ExcludedDates = [],
        });

        var saved = _events.Get(id)!;
        Assert.Equal("Magazín (estiu)", saved.Name);
        Assert.Single(saved.Actions);
        Assert.Equal(EventActionType.AutoDj, saved.Actions[0].Type);
        Assert.Empty(saved.ExcludedDates);
        Assert.Single(_events.List());
    }

    [Fact]
    public void Ordinals_i_dies_del_mes_amb_l_ultim()
    {
        long a = _events.Save(new ScheduledEvent
        {
            Name = "Ona Clàssica",
            Mode = EventMode.Cut,
            StartTime = new TimeOnly(9, 0),
            Recurrence = EventRecurrence.MonthlyOrdinal,
            DaysMask = Weekdays.Bit(DayOfWeek.Saturday),
            Ordinals = [1, 3, -1],
        });
        long b = _events.Save(new ScheduledEvent
        {
            Name = "Resum del mes",
            Mode = EventMode.Queue,
            StartTime = new TimeOnly(12, 30),
            Recurrence = EventRecurrence.DaysOfMonth,
            DaysMask = 0,
            MonthDays = [1, 15, -1],
        });

        Assert.Equal([1, 3, -1], _events.Get(a)!.Ordinals);
        Assert.Equal([1, 15, -1], _events.Get(b)!.MonthDays);
        Assert.Equal("1,15,last", Events.NumberListText([1, 15, -1]));
    }

    [Fact]
    public void Repeticio_cada_hora_amb_les_hores_triades()
    {
        int sevenToEleven = Enumerable.Range(7, 17).Sum(h => 1 << h); // 7–23 h
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Senyal horari",
            Mode = EventMode.Overlay,
            StartTime = new TimeOnly(0, 0),
            Repeat = EventRepeat.EveryHour,
            HoursMask = sevenToEleven,
            DuckingDb = -6.5,
        });

        var ev = _events.Get(id)!;
        Assert.Equal(EventRepeat.EveryHour, ev.Repeat);
        Assert.Equal(sevenToEleven, ev.HoursMask);
        Assert.Equal(-6.5, ev.DuckingDb);
    }

    [Fact]
    public void Esborrar_un_esdeveniment_n_esborra_les_accions_i_conserva_el_registre()
    {
        long id = _events.Save(Magazin());
        var when = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        _events.AddExecution(new ExecutionRecord { EventId = id, Name = "El Magazín", ScheduledFor = when, ExecutedAt = when, Result = ExecutionResult.Ok });

        Assert.True(_events.Delete(id));
        Assert.False(_events.Delete(id));
        Assert.Null(_events.Get(id));

        using (var command = _db.Command("SELECT COUNT(*) FROM accio;"))
            Assert.Equal(0L, (long)command.ExecuteScalar()!);
        var log = _events.Executions(when.AddDays(-1), when.AddDays(1));
        Assert.Single(log);
        Assert.Equal("El Magazín", log[0].Name);
    }

    [Fact]
    public void Desar_amb_un_id_que_no_existeix_dona_error()
    {
        var error = Assert.Throws<ArgumentException>(() => _events.Save(Magazin() with { Id = 999 }));
        Assert.Contains("999", error.Message);
        Assert.Empty(_events.List());
    }

    public static TheoryData<ScheduledEvent> Invalid => new()
    {
        Magazin() with { Name = "  " },
        Magazin() with { IntervalWeeks = null },
        Magazin() with { AnchorDate = null },
        Magazin() with { Recurrence = EventRecurrence.MonthlyOrdinal, Ordinals = [] },
        Magazin() with { Recurrence = EventRecurrence.MonthlyOrdinal, Ordinals = [6] },
        Magazin() with { Recurrence = EventRecurrence.DaysOfMonth, MonthDays = [32] },
        Magazin() with { Recurrence = EventRecurrence.Dates },
        Magazin() with { Recurrence = EventRecurrence.Weekly, DaysMask = 0 },
        Magazin() with { Repeat = EventRepeat.EveryNMinutes, IntervalMinutes = 0 },
        Magazin() with { Repeat = EventRepeat.EveryHour, HoursMask = 0 },
        Magazin() with { StartDate = new DateOnly(2027, 1, 1), EndDate = new DateOnly(2026, 1, 1) },
        Magazin() with { StartTime = new TimeOnly(10, 0, 0, 500) },
        Magazin() with { Actions = [new EventAction { Type = EventActionType.Track, Params = "no és json" }] },
        Magazin() with { Actions = [new EventAction { Type = EventActionType.Track, Params = "[1,2]" }] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void No_desa_un_esdeveniment_incoherent(ScheduledEvent ev)
    {
        Assert.Throws<ArgumentException>(() => _events.Save(ev));
        Assert.Empty(_events.List());
    }

    [Fact]
    public void Categories_amb_color()
    {
        long id = _events.SaveCategory("Programes", "#5a2d14");
        Assert.Equal(id, _events.SaveCategory("programes", "#000000")); // mateix nom (sense distingir majúscules): es canvia el color
        long evId = _events.Save(Magazin() with { CategoryId = id });

        Assert.Equal([new EventCategory(id, "Programes", "#000000")], _events.Categories());

        Assert.True(_events.DeleteCategory(id));
        Assert.Null(_events.Get(evId)!.CategoryId); // l'esdeveniment queda sense categoria
    }

    [Fact]
    public void Registre_d_execucio_per_dates_i_per_esdeveniment()
    {
        var day = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        _events.AddExecution(new ExecutionRecord { EventId = 1, Name = "A", ScheduledFor = day.AddHours(9), ExecutedAt = day.AddHours(9).AddMilliseconds(12), Result = ExecutionResult.Ok });
        _events.AddExecution(new ExecutionRecord { EventId = 2, Name = "B", ScheduledFor = day.AddHours(2.5), Result = ExecutionResult.SkippedClockChange, Detail = "02:30 no existeix" });
        _events.AddExecution(new ExecutionRecord { EventId = 1, Name = "A", ScheduledFor = day.AddDays(1).AddHours(9), Result = ExecutionResult.SkippedLate });

        var today = _events.Executions(day, day.AddDays(1));
        Assert.Equal(["B", "A"], today.Select(e => e.Name)); // per hora prevista
        Assert.Equal(ExecutionResult.SkippedClockChange, today[0].Result);
        Assert.Null(today[0].ExecutedAt);
        Assert.Equal("02:30 no existeix", today[0].Detail);
        Assert.Equal(day.AddHours(9).AddMilliseconds(12), today[1].ExecutedAt);

        Assert.Equal(2, _events.Executions(day, day.AddDays(2), eventId: 1).Count);
        Assert.True(_events.HasExecution(1, day.AddHours(9)));
        Assert.False(_events.HasExecution(1, day.AddHours(10)));
    }

    [Fact]
    public void Marca_de_dies_de_la_setmana()
    {
        Assert.Equal(1, Weekdays.Bit(DayOfWeek.Monday));
        Assert.Equal(64, Weekdays.Bit(DayOfWeek.Sunday));
        Assert.True(Weekdays.Contains(Weekdays.MondayToFriday, DayOfWeek.Friday));
        Assert.False(Weekdays.Contains(Weekdays.MondayToFriday, DayOfWeek.Saturday));
    }
}
