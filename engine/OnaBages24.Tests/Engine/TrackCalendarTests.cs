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

        var rows = TrackCalendar.Build(_events.List(), _spot);

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

        ev = TrackCalendar.Set(ev, _spot, 0, true, TypeOf);  // dilluns
        Assert.Equal(1, Assert.Single(ev.Actions).DaysMask);

        for (int day = 1; day < 5; day++) ev = TrackCalendar.Set(ev, _spot, day, true, TypeOf);
        Assert.Null(Assert.Single(ev.Actions).DaysMask); // tots els dies de l'esdeveniment: sense dies propis

        ev = TrackCalendar.Set(ev, _spot, 2, false, TypeOf); // treu el dimecres
        Assert.Equal(0b1_1011, Assert.Single(ev.Actions).DaysMask);

        foreach (int day in new[] { 0, 1, 3, 4 }) ev = TrackCalendar.Set(ev, _spot, day, false, TypeOf);
        Assert.Empty(ev.Actions); // cap dia: l'acció se'n va

        Assert.Throws<ArgumentException>(() => TrackCalendar.Set(ev, _spot, 6, true, TypeOf)); // diumenge no sona
    }

    [Fact]
    public void Una_pista_nova_va_abans_del_primer_programa()
    {
        long promo = _library.AddTrack(new Track { Path = @"C:\p\Promo.mp3", Type = TrackType.Promo });
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.All, actions: [Track(promo), Track(_program)]);

        ev = TrackCalendar.Set(ev, _spot, 0, true, id => _library.GetTrack(id)?.Type);

        Assert.Equal([promo, _spot, _program], ev.Actions.Select(a => TrackCalendar.TrackOf(a)!.Value));
    }

    [Fact]
    public void Els_recomptes_compten_dia_a_dia_dins_de_les_dates_de_la_pista()
    {
        Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday, actions: [Track(_spot, days: 0b101)]); // dl i dc
        Save("Desactivat", EventMode.Queue, 12, 0, Weekdays.All, enabled: false, actions: [Track(_spot)]);
        var from = new DateOnly(2026, 11, 16); // dilluns
        var to = new DateOnly(2026, 11, 29);   // diumenge, dues setmanes després

        var counts = TrackCalendar.Count(_events.List(), _library.GetTrack(_spot)!, from, to);
        Assert.Equal(4, counts.Total);
        Assert.Equal([1, 0, 1, 0, 0, 0, 0], counts.PerWeekday);
        Assert.Equal([2, 2], counts.Weeks.Select(w => w.Count));
        Assert.Equal(7, counts.MarkedInDisabled); // els dies marcats en un esdeveniment desactivat no compten (però es diuen)

        // Si la pista s'acaba el divendres de la primera setmana, només compten fins aquí
        _library.UpdateTrack(_library.GetTrack(_spot)! with { ValidUntil = new DateOnly(2026, 11, 20) });
        Assert.Equal(2, TrackCalendar.Count(_events.List(), _library.GetTrack(_spot)!, from, to).Total);
    }

    [Fact]
    public void Una_franja_dins_de_la_durada_maxima_d_un_TALLA_esta_ocupada()
    {
        Save("Magazín", EventMode.Cut, 13, 0, Weekdays.All, maxSeconds: 3600);
        Save("Mitja hora", EventMode.Queue, 13, 30, Weekdays.All);

        var row = TrackCalendar.Build(_events.List(), _spot).Single(r => r.Time == new TimeOnly(13, 30));

        Assert.Equal("Magazín", row.Days[0]!.BusyBy);
    }

    [Fact]
    public void Ordres_calendar()
    {
        var ev = Save("Magazín", EventMode.Cut, 9, 0, Weekdays.MondayToFriday);
        var router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
        JsonObject Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }
        string Error(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        var set = Send(new() { ["cmd"] = "calendar.set", ["trackId"] = _spot, ["eventId"] = ev.Id, ["day"] = 2, ["marked"] = true })["result"]!;
        Assert.True(set["rows"]![0]!["days"]![2]!["marked"]!.GetValue<bool>());
        Assert.False(set["rows"]![0]!["days"]![1]!["marked"]!.GetValue<bool>());
        Assert.Equal(1 << 2, Assert.Single(_events.Get(ev.Id)!.Actions).DaysMask); // desat

        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "calendar.set", ["trackId"] = _spot, ["eventId"] = ev.Id, ["day"] = 6, ["marked"] = true })));

        var block = Send(new() { ["cmd"] = "calendar.addBlock", ["time"] = "08:05" })["result"]!;
        var created = _events.Get(block["eventId"]!.GetValue<long>())!;
        Assert.True(created.CommercialBlock);
        Assert.Equal(EventMode.Queue, created.Mode);
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "calendar.addBlock", ["time"] = "08:05" }))); // ja n'hi ha un
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "calendar.addBlock", ["time"] = "08:07" }))); // no és múltiple de 5

        var got = Send(new() { ["cmd"] = "calendar.get", ["trackId"] = _spot, ["from"] = "2026-11-16", ["to"] = "2026-11-22" })["result"]!;
        Assert.Equal(["08:05:00", "09:00:00"], got["rows"]!.AsArray().Select(r => r!["time"]!.GetValue<string>()));
        Assert.Equal(1, got["counts"]!["total"]!.GetValue<int>()); // el dimecres
    }
}
