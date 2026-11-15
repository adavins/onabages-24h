using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Calendaris desats i dates de la temporada (fase 4, pas 6a; D-145).</summary>
public sealed class SavedCalendarsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly SavedCalendars _saved;
    private readonly CommandRouter _router;
    private readonly long _a, _b, _campaign;
    private int _next;

    public SavedCalendarsTests()
    {
        _db = Database.Open(_temp.File("calendaris-desats.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _saved = new SavedCalendars(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns, savedCalendars: _saved);
        _a = AddTrack("Falca A");
        _b = AddTrack("Falca B");
        var today = DateOnly.FromDateTime(DateTime.Today);
        _campaign = _campaigns.Save(new Campaign
        {
            Name = "Ferreteria", StartDate = today, EndDate = today.AddDays(27), State = CampaignState.Active,
            Spots = [new CampaignSpot { TrackId = _b }],
        }, DateTimeOffset.UtcNow);
        // Un tall de dilluns a divendres a les 9
        _events.Save(new ScheduledEvent
        {
            Name = "Tall de les 9", Mode = EventMode.Cut, StartTime = new TimeOnly(9, 0), DaysMask = Weekdays.MondayToFriday, Enabled = true,
            Actions = [new EventAction { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = AddTrack("Programa", TrackType.Program) }.ToJsonString() }],
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private long AddTrack(string title, TrackType type = TrackType.Commercial) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), Type = type, Artist = "Prova", Title = title,
        DurationMs = 20_000, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
    });

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private void Mark(string key, long id, string time, int day) =>
        Assert.NotNull(Send(new() { ["cmd"] = "calendar.set", [key] = id, ["time"] = time, ["day"] = day, ["marked"] = true })["result"]);

    /// <summary>Les caselles marcades del calendari d'una pista o d'una campanya, com «09:00:00/0».</summary>
    private string[] Marked(CalendarSubject subject) => TrackCalendar.Build(_events.List(), subject, DateOnly.FromDateTime(DateTime.Today))
        .SelectMany(r => Enumerable.Range(0, 7).Where(d => r.Days[d]?.Marked == true).Select(d => $"{r.Time:HH:mm:ss}/{d}"))
        .Order().ToArray();

    [Fact]
    public void Es_desa_el_calendari_d_una_pista_i_s_aplica_a_una_campanya()
    {
        // La falca A: dilluns i dimarts a les 9 (el tall) i dissabte a les 12:05 (un bloc nou)
        Mark("trackId", _a, "09:00", 0);
        Mark("trackId", _a, "09:00", 1);
        Mark("trackId", _a, "12:05", 5);
        var saved = Send(new() { ["cmd"] = "savedCalendars.save", ["trackId"] = _a, ["name"] = " Matins " })["result"]!["calendar"]!;
        Assert.Equal(("Matins", 3), (saved["name"]!.GetValue<string>(), saved["cells"]!.GetValue<int>()));
        Assert.Equal([3, 32], saved["slots"]!.AsArray().Select(s => s!["daysMask"]!.GetValue<int>()));

        // A la campanya, que ja té el dimecres a les 9: «Afegeix» el manté
        Mark("campaignId", _campaign, "09:00", 2);
        long id = saved["id"]!.GetValue<long>();
        var result = Send(new() { ["cmd"] = "savedCalendars.apply", ["calendarId"] = id, ["campaignId"] = _campaign })["result"]!;
        var applied = result["applied"]!;
        Assert.Equal((3, 0, 0), (applied["marked"]!.GetValue<int>(), applied["already"]!.GetValue<int>(), applied["removed"]!.GetValue<int>()));
        Assert.Equal(["09:00:00/0", "09:00:00/1", "09:00:00/2", "12:05:00/5"], Marked(CalendarSubject.ForCampaign(_campaign)));
        Assert.Equal("Ferreteria", result["campaign"]!["name"]!.GetValue<string>()); // com calendar.get

        // Tornar-lo a aplicar no fa res de nou; «Substitueix» treu el dimecres
        var again = Send(new() { ["cmd"] = "savedCalendars.apply", ["calendarId"] = id, ["campaignId"] = _campaign, ["mode"] = "replace" })["result"]!["applied"]!;
        Assert.Equal((0, 3, 1), (again["marked"]!.GetValue<int>(), again["already"]!.GetValue<int>(), again["removed"]!.GetValue<int>()));
        Assert.Equal(["09:00:00/0", "09:00:00/1", "12:05:00/5"], Marked(CalendarSubject.ForCampaign(_campaign)));
        Assert.Equal(Marked(CalendarSubject.ForTrack(_a)), Marked(CalendarSubject.ForCampaign(_campaign)));
    }

    [Fact]
    public void Una_casella_que_no_es_pot_marcar_es_diu_i_la_resta_s_aplica()
    {
        // Un calendari amb un dilluns a les 9 i un dissabte a les 9: el dissabte el tall no hi és, però s'hi fa un bloc
        _saved.Save(new SavedCalendar { Name = "Prova", Slots = [new SavedSlot(new TimeOnly(9, 0), 1 | 32)] }, DateTimeOffset.UtcNow);
        long id = _saved.List().Single().Id;
        var applied = Send(new() { ["cmd"] = "savedCalendars.apply", ["calendarId"] = id, ["trackId"] = _a })["result"]!["applied"]!;
        Assert.Equal(2, applied["marked"]!.GetValue<int>());
        Assert.Empty(applied["failed"]!.AsArray());
        Assert.Equal(["09:00:00/0", "09:00:00/5"], Marked(CalendarSubject.ForTrack(_a)));
    }

    [Fact]
    public void Els_noms_no_es_poden_repetir_i_es_poden_esborrar()
    {
        Mark("trackId", _a, "09:00", 0);
        Assert.NotNull(Send(new() { ["cmd"] = "savedCalendars.save", ["trackId"] = _a, ["name"] = "Matins" })["result"]);
        var repeated = Send(new() { ["cmd"] = "savedCalendars.save", ["trackId"] = _a, ["name"] = "MATINS" })["error"]!;
        Assert.Contains("Ja hi ha un calendari", repeated["message"]!.GetValue<string>());
        Assert.Contains("cap casella marcada", Send(new() { ["cmd"] = "savedCalendars.save", ["trackId"] = _b, ["name"] = "Buit" })["error"]!["message"]!.GetValue<string>());

        // Amb l'id, se substitueix (també amb el mateix nom)
        long id = _saved.List().Single().Id;
        Mark("trackId", _a, "09:00", 4);
        var replaced = Send(new() { ["cmd"] = "savedCalendars.save", ["calendarId"] = id, ["trackId"] = _a, ["name"] = "Matins" })["result"]!["calendar"]!;
        Assert.Equal(2, replaced["cells"]!.GetValue<int>());

        Assert.Single(Send(new() { ["cmd"] = "savedCalendars.list" })["result"]!["calendars"]!.AsArray());
        Send(new() { ["cmd"] = "savedCalendars.delete", ["calendarId"] = id });
        Assert.Empty(Send(new() { ["cmd"] = "savedCalendars.list" })["result"]!["calendars"]!.AsArray());
        Assert.Equal(["09:00:00/0", "09:00:00/4"], Marked(CalendarSubject.ForTrack(_a))); // els esdeveniments no es toquen
    }

    [Fact]
    public void La_temporada_es_desa_amb_les_dues_dates_o_cap()
    {
        var empty = Send(new() { ["cmd"] = "season.get" })["result"]!["season"]!;
        Assert.Null(empty["startDate"]);
        Send(new() { ["cmd"] = "season.set", ["startDate"] = "2026-09-14", ["endDate"] = "2027-06-30" });
        var season = Send(new() { ["cmd"] = "season.get" })["result"]!["season"]!;
        Assert.Equal(("2026-09-14", "2027-06-30"), (season["startDate"]!.GetValue<string>(), season["endDate"]!.GetValue<string>()));
        Assert.Contains("o cap", Send(new() { ["cmd"] = "season.set", ["startDate"] = "2026-09-14" })["error"]!["message"]!.GetValue<string>());
        Assert.Contains("abans de començar", Send(new() { ["cmd"] = "season.set", ["startDate"] = "2027-09-14", ["endDate"] = "2027-06-30" })["error"]!["message"]!.GetValue<string>());
        Send(new() { ["cmd"] = "season.set", ["startDate"] = null, ["endDate"] = null });
        Assert.Null(Send(new() { ["cmd"] = "season.get" })["result"]!["season"]!["endDate"]);
    }
}
