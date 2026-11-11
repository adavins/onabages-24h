using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Ordres scheduler.* (fase 2, pas 3; docs/protocol.md, 4.7).</summary>
public sealed class SchedulerCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly Scheduler _scheduler;
    private readonly CommandRouter _router;
    private int _changed;

    public SchedulerCommandsTests()
    {
        _db = Database.Open(_temp.File("ordres.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _scheduler = new Scheduler(_events, new EventExecutor(_library, _queue, null), NullLogger.Instance, zone: TimeZoneInfo.Utc);
        _scheduler.EventsChanged += () => _changed++;
        _router = new CommandRouter(new EngineState(), () => { }, _library, () => [], _queue, events: _events, scheduler: _scheduler);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private static JsonNode Result(JsonObject response)
    {
        Assert.True(response["ok"]!.GetValue<bool>(), response.ToJsonString());
        return response["result"]!;
    }

    private static string? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<string>();

    private const string Magazin = """
        { "v": 1, "id": 1, "cmd": "scheduler.save", "event": {
            "name": "El Magazín", "mode": "TALLA", "startTime": "10:00:00",
            "recurrence": "CADA_N_SETMANES", "daysMask": 16, "intervalWeeks": 2, "anchorDate": "2026-11-06",
            "clearQueue": true, "maxDurationSeconds": 3600, "excludedDates": ["2027-01-01"],
            "actions": [
              { "type": "PISTA", "params": { "songId": 10 } },
              { "type": "ROTACIO", "params": { "rotacioId": 1 }, "daysMask": 16 }
            ] } }
        """;

    [Fact]
    public void Desa_un_esdeveniment_nou_i_el_torna_amb_l_id()
    {
        var ev = Result(Send(Magazin))["event"]!;

        Assert.True(ev["id"]!.GetValue<long>() > 0);
        Assert.Equal("El Magazín", ev["name"]!.GetValue<string>());
        Assert.Equal("TALLA", ev["mode"]!.GetValue<string>());
        Assert.Equal("10:00:00", ev["startTime"]!.GetValue<string>());
        Assert.Equal("CADA_N_SETMANES", ev["recurrence"]!.GetValue<string>());
        Assert.Equal("2026-11-06", ev["anchorDate"]!.GetValue<string>());
        Assert.Equal(60, ev["toleranceSeconds"]!.GetValue<int>()); // per defecte
        Assert.True(ev["enabled"]!.GetValue<bool>());
        Assert.Equal(10, ev["actions"]![0]!["params"]!["songId"]!.GetValue<int>());
        Assert.Equal(16, ev["actions"]![1]!["daysMask"]!.GetValue<int>());
        Assert.Equal("2027-01-01", ev["excludedDates"]![0]!.GetValue<string>());
        Assert.Equal(1, _changed); // el planificador ho sap
    }

    [Fact]
    public void Llista_obte_modifica_i_esborra()
    {
        long id = Result(Send(Magazin))["event"]!["id"]!.GetValue<long>();

        Assert.Single(Result(Send("""{ "v": 1, "id": 2, "cmd": "scheduler.list" }"""))["events"]!.AsArray());

        var changed = Result(Send($$"""
            { "v": 1, "id": 3, "cmd": "scheduler.save", "event": { "id": {{id}}, "name": "Magazín d'estiu", "mode": "ENCUA", "startTime": "10:30" } }
            """))["event"]!;
        Assert.Equal("Magazín d'estiu", changed["name"]!.GetValue<string>());
        Assert.Equal("10:30:00", changed["startTime"]!.GetValue<string>());
        Assert.Equal("SETMANAL", changed["recurrence"]!.GetValue<string>()); // el que no s'envia torna al valor per defecte
        Assert.Empty(changed["actions"]!.AsArray());

        var got = Result(Send($$"""{ "v": 1, "id": 4, "cmd": "scheduler.get", "eventId": {{id}} }"""))["event"]!;
        Assert.Equal("Magazín d'estiu", got["name"]!.GetValue<string>());

        Assert.Equal(id, Result(Send($$"""{ "v": 1, "id": 5, "cmd": "scheduler.delete", "eventId": {{id}} }"""))["deleted"]!.GetValue<long>());
        Assert.Empty(Result(Send("""{ "v": 1, "id": 6, "cmd": "scheduler.list" }"""))["events"]!.AsArray());
        Assert.Equal("bad_params", ErrorCode(Send($$"""{ "v": 1, "id": 7, "cmd": "scheduler.get", "eventId": {{id}} }""")));
        Assert.Equal(3, _changed);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save" }""", "event")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "startTime": "10:00" } }""", "mode")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA" } }""", "startTime")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "PUNXA", "startTime": "10:00" } }""", "PUNXA")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA", "startTime": "25:00" } }""", "hora")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA", "startTime": "10:00", "daysMask": "dl" } }""", "daysMask")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA", "startTime": "10:00", "recurrence": "CADA_N_SETMANES" } }""", "setmanes")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA", "startTime": "10:00", "actions": [{ "type": "SALTA" }] } }""", "SALTA")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "X", "mode": "TALLA", "startTime": "10:00", "actions": [{ "type": "PISTA", "params": 5 }] } }""", "objecte")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "id": 999, "name": "X", "mode": "TALLA", "startTime": "10:00" } }""", "999")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.delete", "eventId": 999 }""", "999")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "scheduler.get" }""", "eventId")]
    public void Dades_dolentes_donen_bad_params_amb_el_motiu(string json, string expected)
    {
        var response = Send(json);

        Assert.Equal("bad_params", ErrorCode(response));
        Assert.Contains(expected, response["error"]!["message"]!.GetValue<string>());
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void Ocurrencies_entre_dues_dates_i_d_un_esdeveniment()
    {
        long id = Result(Send(Magazin))["event"]!["id"]!.GetValue<long>();
        Send("""{ "v": 1, "id": 2, "cmd": "scheduler.save", "event": { "name": "Senyal", "mode": "SUPERPOSA", "startTime": "00:00", "repeat": "CADA_HORA", "hoursMask": 128 } }""");
        Send("""{ "v": 1, "id": 3, "cmd": "scheduler.save", "event": { "name": "Apagat", "mode": "ENCUA", "startTime": "08:00", "enabled": false } }""");

        var all = Result(Send("""{ "v": 1, "id": 4, "cmd": "scheduler.occurrences", "from": "2026-11-20T00:00:00Z", "to": "2026-11-21T00:00:00Z" }"""));
        Assert.Equal(["Senyal", "El Magazín"], all["occurrences"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()));
        Assert.False(all["more"]!.GetValue<bool>());
        var magazin = all["occurrences"]![1]!;
        Assert.Equal("2026-11-20", magazin["date"]!.GetValue<string>());
        Assert.Equal("10:00:00", magazin["time"]!.GetValue<string>());
        Assert.Equal("TALLA", magazin["mode"]!.GetValue<string>());

        // D'un sol esdeveniment: els 3 pròxims divendres que toquen (l'1/1 no)
        var one = Result(Send($$"""{ "v": 1, "id": 5, "cmd": "scheduler.occurrences", "eventId": {{id}}, "from": "2026-12-01T00:00:00Z", "to": "2027-01-31T00:00:00Z" }"""));
        Assert.Equal(["2026-12-04", "2026-12-18", "2027-01-15", "2027-01-29"], one["occurrences"]!.AsArray().Select(o => o!["date"]!.GetValue<string>()));
    }

    [Fact]
    public void Registre_d_execucio()
    {
        var when = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        _events.AddExecution(new ExecutionRecord { EventId = 3, Name = "Mitja hora", ScheduledFor = when, ExecutedAt = when, Result = ExecutionResult.Ok, Detail = "2 pistes a la cua" });

        var log = Result(Send("""{ "v": 1, "id": 1, "cmd": "scheduler.log", "from": "2026-11-20T00:00:00Z", "to": "2026-11-21T00:00:00Z" }"""));

        var entry = Assert.Single(log["entries"]!.AsArray())!;
        Assert.Equal("Mitja hora", entry["name"]!.GetValue<string>());
        Assert.Equal("OK", entry["result"]!.GetValue<string>());
        Assert.Equal("2 pistes a la cua", entry["detail"]!.GetValue<string>());
        Assert.Equal(when, DateTimeOffset.Parse(entry["scheduledFor"]!.GetValue<string>()));
    }

    [Fact]
    public void Sense_planificador_ho_diu()
    {
        var router = new CommandRouter(new EngineState(), () => { }, _library, () => []);

        Assert.Equal("bad_params", ErrorCode(router.Handle("""{ "v": 1, "id": 1, "cmd": "scheduler.list" }""")));
    }

    [Fact]
    public void Les_accions_porten_com_es_mostren_a_la_interficie()
    {
        long track = _library.AddTrack(new Track { Path = _temp.File("no-hi-es.mp3"), Artist = "Ona Bages", Title = "El Magazín",
            Type = TrackType.Program, DurationMs = 3_540_000, FileState = FileState.Missing });
        string json = $$"""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "Magazín", "mode": "TALLA", "startTime": "10:00:00",
              "actions": [ { "type": "PISTA", "params": { "songId": {{track}} } }, { "type": "PISTA", "params": { "songId": 999 } },
                           { "type": "AUXILIAR", "params": { "songId": {{track}}, "player": 2, "duckingDb": -12 } } ] } }
            """;
        var actions = Result(Send(json))["event"]!["actions"]!.AsArray();

        Assert.Equal("Ona Bages - El Magazín", actions[0]!["label"]!.GetValue<string>());
        Assert.Equal("Programa · 59:00 · el fitxer falta", actions[0]!["detail"]!.GetValue<string>());
        Assert.True(actions[0]!["missing"]!.GetValue<bool>());
        Assert.Equal(3_540_000, actions[0]!["durationMs"]!.GetValue<long>());
        Assert.Equal("Pista 999 (no existeix)", actions[1]!["label"]!.GetValue<string>());
        Assert.Contains("reproductor auxiliar 2 · música a -12 dB", actions[2]!["detail"]!.GetValue<string>());
    }

    [Fact]
    public void Les_emissions_dels_desactivats_nomes_surten_si_es_demanen()
    {
        _events.Save(new ScheduledEvent { Name = "Desactivat", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0), Enabled = false,
            MaxDurationSeconds = 600 });
        const string Ask = """{ "v": 1, "id": 1, "cmd": "scheduler.occurrences", "from": "2026-11-16", "to": "2026-11-22"INCLUDE }""";

        Assert.Empty(Result(Send(Ask.Replace("INCLUDE", "")))["occurrences"]!.AsArray());
        var all = Result(Send(Ask.Replace("INCLUDE", ", \"includeDisabled\": true")))["occurrences"]!.AsArray();
        Assert.Equal(7, all.Count);
        Assert.False(all[0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("CAP", all[0]!["repeat"]!.GetValue<string>());
        Assert.Equal(600, all[0]!["maxDurationSeconds"]!.GetValue<int>());
    }

    // --- Editor (fase 2, pas 8b) ---

    [Fact]
    public void La_previsualitzacio_calcula_les_properes_emissions_sense_desar_res()
    {
        var result = Result(Send("""
            { "v": 1, "id": 1, "cmd": "scheduler.preview", "limit": 3, "event": {
                "name": "Encara no existeix", "mode": "ENCUA", "startTime": "10:30:00", "daysMask": 1 } }
            """));

        var occurrences = result["occurrences"]!.AsArray();
        Assert.Equal(3, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal("10:30:00", o!["time"]!.GetValue<string>()));
        Assert.All(occurrences, o => Assert.Equal(DayOfWeek.Monday, DateOnly.Parse(o!["date"]!.GetValue<string>()).DayOfWeek));
        Assert.Empty(_events.List());   // no s'ha desat
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void La_previsualitzacio_d_un_esdeveniment_incoherent_diu_per_que()
    {
        var response = Send("""
            { "v": 1, "id": 1, "cmd": "scheduler.preview", "event": {
                "name": "Cada 2 setmanes sense dia de referència", "mode": "TALLA", "startTime": "10:00:00",
                "recurrence": "CADA_N_SETMANES", "intervalWeeks": 2 } }
            """);

        Assert.Equal("bad_params", ErrorCode(response));
        Assert.Contains("dia de referència", response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void La_cerca_de_pistes_troba_per_paraules_a_l_artista_el_titol_o_la_ruta()
    {
        _library.AddTrack(new Track { Path = @"D:\continuitat\magazin.mp3", Artist = "Ona Bages", Title = "El Magazín de la Comarca", Type = TrackType.Program });
        _library.AddTrack(new Track { Path = @"C:\RadioDJ\hores\SENYALS HORARIS.mp3", Artist = "Senyals horaris", Title = "SENYALS HORARIS", Type = TrackType.TimeSignal });
        _library.AddTrack(new Track { Path = @"O:\musica\100%_exits\canco.mp3", Artist = "Algú", Title = "Cançó" });

        string[] Search(string text) => Result(Send($$"""{ "v": 1, "id": 1, "cmd": "library.search", "text": "{{text}}" }"""))["tracks"]!
            .AsArray().Select(t => t!["title"]!.GetValue<string>()).ToArray();

        Assert.Equal(["El Magazín de la Comarca"], Search("magazin comarca"));   // dues paraules, totes dues hi han de ser
        Assert.Equal(["SENYALS HORARIS"], Search("senyals"));                    // sense distingir majúscules
        Assert.Equal(["El Magazín de la Comarca"], Search("continuitat"));       // també a la ruta
        Assert.Equal(["Cançó"], Search("100%"));                                 // «%» és un caràcter normal
        Assert.Empty(Search("no-hi-es"));
        Assert.Equal(3, Search("").Length);                                      // sense text, totes

        var limited = Result(Send("""{ "v": 1, "id": 1, "cmd": "library.search", "text": "", "limit": 2 }"""));
        Assert.Equal(2, limited["tracks"]!.AsArray().Count);
        Assert.True(limited["more"]!.GetValue<bool>());
    }
}
