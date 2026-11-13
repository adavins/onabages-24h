using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Calendari d'una pista (fase 4, pas 2; D-104): la graella setmanal, marcar caselles i els recomptes de passis.</summary>
public sealed class TrackCalendarTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly long _spot, _program;

    /// <summary>«Avui» per a les proves: dijous 12 de novembre del 2026.</summary>
    private static readonly DateOnly Today = new(2026, 11, 12);

    public TrackCalendarTests()
    {
        _db = Database.Open(_temp.File("calendari.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _spot = _library.AddTrack(new Track { Path = @"C:\f\Ferreteria.mp3", Title = "Ferreteria", Type = TrackType.Commercial, DurationMs = 20_000 });
        _program = _library.AddTrack(new Track { Path = @"C:\p\Magazin.mp3", Title = "Magazín", Type = TrackType.Program, DurationMs = 3_600_000 });
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private static EventAction Track(long id, int? days = null) => new() { Type = EventActionType.Track, Params = $$"""{"songId":{{id}}}""", DaysMask = days };

    private ScheduledEvent Save(string name, EventMode mode, int hour, int minute, int days, bool enabled = true, int? maxSeconds = null,
        IReadOnlyList<EventAction>? actions = null, bool template = false)
    {
        long id = _events.Save(new ScheduledEvent
        {
            Name = name, Mode = mode, StartTime = new TimeOnly(hour, minute), DaysMask = days, Enabled = enabled,
            MaxDurationSeconds = maxSeconds, Actions = actions ?? [], IsTemplate = template,
        });
        return _events.Get(id)!;
    }

    [Fact]
    public void La_graella_te_una_fila_per_hora_amb_l_esdeveniment_de_cada_dia()
    {
        Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday);
        Save("Dissabte", EventMode.Cut, 9, 0, 0b010_0000);
        Save("Mitja hora", EventMode.Queue, 8, 30, Weekdays.All);
        Save("Senyal", EventMode.Overlay, 10, 0, Weekdays.All);              // no porta pistes
        Save("Plantilla", EventMode.Queue, 11, 0, Weekdays.All, template: true); // tampoc

        var rows = TrackCalendar.Build(_events.List(), _spot, Today);

        Assert.Equal([new TimeOnly(8, 30), new TimeOnly(9, 0)], rows.Select(r => r.Time));
        var nine = rows[1].Days;
        Assert.Equal("Magazín", nine[0]!.Event.Name);
        Assert.Equal("Dissabte", nine[5]!.Event.Name);
        Assert.Null(nine[6]); // diumenge, a les 9, no hi ha res
        Assert.All(rows.SelectMany(r => r.Days).OfType<CalendarCell>(), c => Assert.False(c.Marked));
    }

    [Fact]
    public void Marcar_i_desmarcar_dies_canvia_els_dies_propis_de_l_accio()
    {
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday);
        TrackType? TypeOf(long id) => _library.GetTrack(id)?.Type;

        ev = TrackCalendar.Set(ev, _spot, 0, true, TypeOf, Today);  // dilluns
        Assert.Equal(1, Assert.Single(ev.Actions).DaysMask);

        for (int day = 1; day < 5; day++) ev = TrackCalendar.Set(ev, _spot, day, true, TypeOf, Today);
        Assert.Null(Assert.Single(ev.Actions).DaysMask); // tots els dies de l'esdeveniment: sense dies propis

        ev = TrackCalendar.Set(ev, _spot, 2, false, TypeOf, Today); // treu el dimecres
        Assert.Equal(0b1_1011, Assert.Single(ev.Actions).DaysMask);

        foreach (int day in new[] { 0, 1, 3, 4 }) ev = TrackCalendar.Set(ev, _spot, day, false, TypeOf, Today);
        Assert.Empty(ev.Actions); // cap dia: l'acció se'n va

        Assert.Throws<ArgumentException>(() => TrackCalendar.Set(ev, _spot, 6, true, TypeOf, Today)); // diumenge no sona
    }

    [Fact]
    public void Una_pista_nova_va_abans_del_primer_programa()
    {
        long promo = _library.AddTrack(new Track { Path = @"C:\p\Promo.mp3", Type = TrackType.Promo });
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.All, actions: [Track(promo), Track(_program)]);

        ev = TrackCalendar.Set(ev, _spot, 0, true, id => _library.GetTrack(id)?.Type, Today);

        Assert.Equal([promo, _spot, _program], ev.Actions.Select(a => TrackCalendar.TrackOf(a)!.Value));
    }

    [Fact]
    public void Els_recomptes_compten_dia_a_dia_dins_de_les_dates_de_la_pista()
    {
        Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday, actions: [Track(_spot, days: 0b101)]); // dl i dc
        Save("Desactivat", EventMode.Queue, 12, 0, Weekdays.All, enabled: false, actions: [Track(_spot)]);
        var from = new DateOnly(2026, 11, 16); // dilluns
        var to = new DateOnly(2026, 11, 29);   // diumenge, dues setmanes després

        var counts = TrackCalendar.Count(_events.List(), _library.GetTrack(_spot)!, from, to, Today);
        Assert.Equal(4, counts.Total);
        Assert.Equal([1, 0, 1, 0, 0, 0, 0], counts.PerWeekday);
        Assert.Equal([2, 2], counts.Weeks.Select(w => w.Count));
        Assert.Equal(7, counts.MarkedInDisabled); // els dies marcats en un esdeveniment desactivat no compten (però es diuen)

        // Si la pista s'acaba el divendres de la primera setmana, només compten fins aquí
        _library.UpdateTrack(_library.GetTrack(_spot)! with { ValidUntil = new DateOnly(2026, 11, 20) });
        Assert.Equal(2, TrackCalendar.Count(_events.List(), _library.GetTrack(_spot)!, from, to, Today).Total);
    }

    [Fact]
    public void Una_data_solta_nomes_surt_el_seu_dia_i_les_passades_o_acabades_no_surten()
    {
        // Una data solta, dissabte 14/11: només la columna del dissabte
        _events.Save(new ScheduledEvent { Name = "Prova", Mode = EventMode.Cut, StartTime = new TimeOnly(21, 46), Recurrence = EventRecurrence.Dates,
            IncludedDates = [new DateOnly(2026, 11, 14)] });
        // Una data solta que ja ha passat, i un esdeveniment setmanal que es va acabar ahir: no surten
        _events.Save(new ScheduledEvent { Name = "Passada", Mode = EventMode.Cut, StartTime = new TimeOnly(20, 0), Recurrence = EventRecurrence.Dates,
            IncludedDates = [new DateOnly(2026, 11, 10)] });
        Save("Acabat", EventMode.Queue, 19, 0, Weekdays.All);
        var ended = _events.List().Single(e => e.Name == "Acabat");
        _events.Save(ended with { EndDate = Today.AddDays(-1) });

        var rows = TrackCalendar.Build(_events.List(), _spot, Today);

        var row = Assert.Single(rows);
        Assert.Equal(new TimeOnly(21, 46), row.Time);
        Assert.Equal([5], Enumerable.Range(0, 7).Where(d => row.Days[d] is not null)); // dissabte
        Assert.Equal([new DateOnly(2026, 11, 14)], TrackCalendar.UpcomingDates(row.Days[5]!.Event, Today));

        // Marcar-la aquell dia: l'acció no necessita dies propis (és l'únic dia que sona)
        var ev = TrackCalendar.Set(row.Days[5]!.Event, _spot, 5, true, _ => null, Today);
        Assert.Null(Assert.Single(ev.Actions).DaysMask);
    }

    [Fact]
    public void Una_franja_dins_de_la_durada_maxima_d_un_TALLA_esta_ocupada()
    {
        Save("Magazín", EventMode.Cut, 13, 0, Weekdays.All, maxSeconds: 3600);
        Save("Mitja hora", EventMode.Queue, 13, 30, Weekdays.All);

        var row = TrackCalendar.Build(_events.List(), _spot, Today).Single(r => r.Time == new TimeOnly(13, 30));

        Assert.Equal("Magazín", row.Days[0]!.BusyBy);
    }

    [Fact]
    public void Ordres_calendar()
    {
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday);
        var router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
        JsonObject Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }
        string Error(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        JsonObject Set(string time, int day, bool marked, long? track = null) =>
            Send(new() { ["cmd"] = "calendar.set", ["trackId"] = track ?? _spot, ["time"] = time, ["day"] = day, ["marked"] = marked });

        // Una casella amb esdeveniment: la pista hi entra aquell dia
        var set = Set("09:00", 2, true)["result"]!;
        Assert.True(set["rows"]![0]!["days"]![2]!["marked"]!.GetValue<bool>());
        Assert.False(set["rows"]![0]!["days"]![1]!["marked"]!.GetValue<bool>());
        Assert.Equal(1 << 2, Assert.Single(_events.Get(ev.Id)!.Actions).DaysMask); // desat

        // Diumenge a les 9 no hi ha cap esdeveniment: es crea un bloc de falques, només el diumenge
        Set("09:00", 6, true);
        var block = Assert.Single(_events.List(), e => e.CommercialBlock);
        Assert.Equal((EventMode.Queue, 1 << 6, new TimeOnly(9, 0)), (block.Mode, block.DaysMask, block.StartTime));

        Assert.Equal("bad_params", Error(Set("nou", 0, true)));
        Assert.Equal("bad_params", Error(Set("09:00", 7, true)));

        var got = Send(new() { ["cmd"] = "calendar.get", ["trackId"] = _spot, ["from"] = "2026-11-16", ["to"] = "2026-11-22" })["result"]!;
        Assert.Equal(["09:00:00"], got["rows"]!.AsArray().Select(r => r!["time"]!.GetValue<string>()));
        Assert.Equal(2, got["counts"]!["total"]!.GetValue<int>()); // el dimecres (Magazín) i el diumenge (bloc)

        // Treure-la del bloc: es queda buit i s'esborra
        Set("09:00", 6, false);
        Assert.DoesNotContain(_events.List(), e => e.CommercialBlock);
    }

    [Fact]
    public void Els_blocs_agrupen_per_hora_i_sonen_els_dies_de_les_seves_pistes()
    {
        long capsule = _library.AddTrack(new Track { Path = @"C:\c\Capsula.mp3", Title = "Càpsula", Type = TrackType.News });
        var router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
        void Set(long track, int day, bool marked) => router.Handle(new JsonObject
        {
            ["v"] = 1, ["id"] = 1, ["cmd"] = "calendar.set", ["trackId"] = track, ["time"] = "10:50", ["day"] = day, ["marked"] = marked,
        }.ToJsonString());

        for (int day = 0; day < 5; day++) Set(_spot, day, true); // la falca, de dilluns a divendres
        Set(capsule, 5, true);                                      // la càpsula, el dissabte

        var block = Assert.Single(_events.List(), e => e.CommercialBlock); // un sol bloc per a les dues
        Assert.Equal(0b011_1111, block.DaysMask);                          // de dilluns a dissabte; el diumenge no
        Assert.Equal([0b1_1111, 0b10_0000], block.Actions.Select(a => a.DaysMask!.Value));

        Set(capsule, 5, false); // sense la càpsula, el bloc torna a ser de dilluns a divendres
        Assert.Equal(Weekdays.MondayToFriday, _events.List().Single(e => e.CommercialBlock).DaysMask);

        // Un bloc buit d'abans (p. ex. buidat al Planificador) no surt a la graella i s'esborra al primer canvi
        _events.Save(TrackCalendar.NewBlock(new TimeOnly(7, 5)) with { DaysMask = Weekdays.All });
        Assert.DoesNotContain(TrackCalendar.Build(_events.List(), _spot, Today), r => r.Time == new TimeOnly(7, 5));
        Set(_spot, 0, false);
        Assert.DoesNotContain(_events.List(), e => e.StartTime == new TimeOnly(7, 5));
    }

    [Fact]
    public void Moure_la_pista_la_passa_abans_o_despres_de_la_veina_d_aquell_dia()
    {
        long a = _library.AddTrack(new Track { Path = @"C:\f\A.mp3", Title = "A", Type = TrackType.Commercial, DurationMs = 10_000 });
        long b = _library.AddTrack(new Track { Path = @"C:\f\B.mp3", Title = "B", Type = TrackType.Commercial, DurationMs = 10_000 });
        // Ordre: A (tots els dies), B (només dimarts), Ferreteria (tots), Magazín (tots)
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday,
            actions: [Track(a), Track(b, 0b10), Track(_spot), Track(_program)]);
        long[] Order(ScheduledEvent e) => e.Actions.Select(x => TrackCalendar.TrackOf(x)!.Value).ToArray();

        // Dilluns, B no hi és: pujar Ferreteria la passa just abans d'A
        Assert.Equal([_spot, a, b, _program], Order(TrackCalendar.Move(ev, _spot, 0, -1)));
        // Dimarts, B hi és: pujar Ferreteria la passa just abans de B
        Assert.Equal([a, _spot, b, _program], Order(TrackCalendar.Move(ev, _spot, 1, -1)));
        // Baixar-la la passa darrere del Magazín
        Assert.Equal([a, b, _program, _spot], Order(TrackCalendar.Move(ev, _spot, 0, 1)));
        // La primera no puja més
        Assert.Same(ev, TrackCalendar.Move(ev, a, 0, -1));
        Assert.Throws<ArgumentException>(() => TrackCalendar.Move(ev, b, 0, 1)); // dilluns, B no hi és

        // Per ordre: el contingut del dimarts en ordre, amb la pista marcada; moure-la es desa
        var router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
        JsonNode Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString())["result"]!; }
        var content = Send(new() { ["cmd"] = "calendar.content", ["eventId"] = ev.Id, ["day"] = 1, ["trackId"] = _spot });
        Assert.Equal(["A", "B", "Ferreteria", "Magazín"], content["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>()));
        Assert.Equal([false, false, true, false], content["items"]!.AsArray().Select(i => i!["isTrack"]!.GetValue<bool>()));
        Assert.Equal(20_000, content["items"]![2]!["durationMs"]!.GetValue<long>());
        Assert.Equal(3, Send(new() { ["cmd"] = "calendar.content", ["eventId"] = ev.Id, ["day"] = 0 })["items"]!.AsArray().Count); // dilluns, sense B

        var moved = Send(new() { ["cmd"] = "calendar.move", ["eventId"] = ev.Id, ["day"] = 1, ["trackId"] = _spot, ["delta"] = -1 });
        Assert.Equal(["A", "Ferreteria", "B", "Magazín"], moved["items"]!.AsArray().Select(i => i!["label"]!.GetValue<string>()));
        Assert.Equal([a, _spot, b, _program], Order(_events.Get(ev.Id)!));
    }
}
