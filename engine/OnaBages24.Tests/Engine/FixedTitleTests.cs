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

/// <summary>Títol fix per a BUTT (D-184): a mà, amb l'acció TITOL del planificador, i que torna sol en acabar.</summary>
public sealed class FixedTitleTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly NowPlaying _nowPlaying;
    private readonly CommandRouter _router;
    private readonly string _path;

    public FixedTitleTests()
    {
        _db = Database.Open(_temp.File("titol.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _path = _temp.File("NowPlaying.txt");
        _nowPlaying = new NowPlaying(null, _path, new NowPlayingSettings(), NullLogger.Instance);
        var scheduler = new Scheduler(_events, new EventExecutor(_library, _queue, null, nowPlaying: _nowPlaying), NullLogger.Instance, zone: TimeZoneInfo.Utc);
        _router = new CommandRouter(new EngineState(), () => { }, _library, () => [], _queue, nowPlaying: _nowPlaying, events: _events, scheduler: scheduler);
    }

    public void Dispose()
    {
        _nowPlaying.Dispose();
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    /// <summary>Espera que el fil d'escriptura hagi escrit el fitxer amb aquest text (com a molt 3 s).</summary>
    private void WaitForFile(string expected)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        string? text = null;
        while (DateTime.UtcNow < until)
        {
            try { text = File.ReadAllText(_path); } catch (IOException) { }
            if (text == expected) return;
            Thread.Sleep(20);
        }
        Assert.Equal(expected, text);
    }

    private static QueueItem Song(string artist, string title) => new() { Path = @"O:\x.mp3", Artist = artist, Title = title, Type = TrackType.Music };

    [Fact]
    public void El_titol_fix_mana_sobre_les_cançons_i_en_acabar_torna_el_que_sona()
    {
        var changes = new List<FixedTitle?>();
        _nowPlaying.FixedChanged += t => { lock (changes) changes.Add(t); };
        _nowPlaying.Show(Song("Macy Gray", "Still"));
        WaitForFile("Macy Gray - Still");

        _nowPlaying.SetFixed("Esport i Punt", DateTimeOffset.Now.AddSeconds(1), "Esport i Punt");
        WaitForFile("Esport i Punt");

        // Mentre dura, les cançons no el canvien (però es recorda la que sona)
        _nowPlaying.Show(Song("Txarango", "Som riu"));
        Thread.Sleep(200);
        Assert.Equal("Esport i Punt", File.ReadAllText(_path));

        // En acabar, torna sol el títol del que sona ara
        WaitForFile("Txarango - Som riu");
        Assert.Null(_nowPlaying.Fixed);
        lock (changes)
        {
            Assert.Equal(2, changes.Count);
            Assert.Equal("Esport i Punt", changes[0]?.Text);
            Assert.Null(changes[1]);
        }
    }

    [Fact]
    public void El_titol_fix_tambe_mana_sobre_el_titol_d_un_stream()
    {
        _nowPlaying.SetFixed("Esport i Punt", DateTimeOffset.Now.AddHours(1), null);
        var stream = new QueueItem { Path = "http://127.0.0.1/directe", Title = "Directe", Type = TrackType.Stream, StreamTitleForButt = true };
        _nowPlaying.ShowStream(new QueueStreamStatus(stream, "live", "Cutting Crew - Died In Your Arms", TimeSpan.Zero, TimeSpan.FromMinutes(5),
            false, false, 0, null, null));

        WaitForFile("Esport i Punt");
        Assert.Equal("Esport i Punt", _nowPlaying.Text);

        // Treure'l a mà: torna el títol del stream
        Assert.True(_nowPlaying.ClearFixed());
        WaitForFile("Cutting Crew - Died In Your Arms");
        Assert.False(_nowPlaying.ClearFixed()); // ja no n'hi ha
    }

    [Fact]
    public void Una_entrada_amb_titol_per_a_BUTT_l_escriu_mentre_sona_i_el_titol_fix_hi_mana()
    {
        _nowPlaying.Show(Song("Macy Gray", "Still"));
        WaitForFile("Macy Gray - Still");

        // Sense títol, una entrada (tipus Stream) no canvia el títol
        var input = QueueItem.FromInput(null, "Línia 2", QueueOrigin.Event);
        _nowPlaying.Show(input);
        Thread.Sleep(200);
        Assert.Equal("Macy Gray - Still", File.ReadAllText(_path));

        // Amb títol, sí (en una línia), encara que el tipus no sigui dels que canvien el títol
        _nowPlaying.Show(input with { ButtTitle = "Esport i Punt\n(en directe)" });
        WaitForFile("Esport i Punt (en directe)");

        // Canviar la plantilla no el toca; quan torna una cançó, torna el títol de la cançó
        _nowPlaying.Configure(_nowPlaying.Settings with { Template = "$title$" });
        Assert.Equal("Esport i Punt (en directe)", _nowPlaying.Text);
        _nowPlaying.Show(Song("Txarango", "Som riu"));
        WaitForFile("Som riu");

        // Amb un títol fix, mana el fix
        _nowPlaying.SetFixed("Títol fix", DateTimeOffset.Now.AddHours(1), null);
        _nowPlaying.Show(input with { ButtTitle = "Esport i Punt" });
        WaitForFile("Títol fix");
    }

    [Fact]
    public void Si_el_motor_es_reinicia_a_mig_programa_es_recupera_i_si_ja_ha_passat_no()
    {
        var saved = new FixedTitle("Esport i Punt", DateTimeOffset.Now.AddHours(1), "Esport i Punt");
        var back = FixedTitle.FromJson(saved.ToJson());
        Assert.Equal(saved, back);
        Assert.Null(FixedTitle.FromJson("null")); // el que es desa quan s'acaba

        Assert.False(_nowPlaying.Restore(saved with { Until = DateTimeOffset.Now.AddMinutes(-1) }));
        Assert.Null(_nowPlaying.Fixed);
        Assert.True(_nowPlaying.Restore(back!));
        WaitForFile("Esport i Punt");
    }

    [Fact]
    public void Nowplaying_fixed_set_i_clear_el_posen_i_el_treuen_a_ma()
    {
        _nowPlaying.Show(Song("Macy Gray", "Still"));
        string until = DateTimeOffset.Now.AddHours(2).ToString("O");

        var set = Send($$"""{ "v": 1, "id": 1, "cmd": "nowplaying.fixed.set", "text": "  Esport i Punt\n", "until": "{{until}}" }""");

        Assert.True(set["ok"]!.GetValue<bool>(), set.ToJsonString());
        var fixedJson = set["result"]!["nowPlaying"]!["fixed"]!;
        Assert.Equal("Esport i Punt", fixedJson["text"]!.GetValue<string>());
        Assert.Null(fixedJson["eventName"]);
        Assert.Equal(DateTimeOffset.Parse(until), DateTimeOffset.Parse(fixedJson["until"]!.GetValue<string>()));
        WaitForFile("Esport i Punt");

        var get = Send("""{ "v": 1, "id": 2, "cmd": "nowplaying.get" }""");
        Assert.Equal("Esport i Punt", get["result"]!["nowPlaying"]!["fixed"]!["text"]!.GetValue<string>());

        var clear = Send("""{ "v": 1, "id": 3, "cmd": "nowplaying.fixed.clear" }""");
        Assert.Null(clear["result"]!["nowPlaying"]!["fixed"]);
        WaitForFile("Macy Gray - Still");
    }

    [Theory]
    [InlineData("", 60, "text")]
    [InlineData("Esport i Punt", -60, "ja ha passat")]
    [InlineData("Esport i Punt", 25 * 60, "24 hores")]
    public void Nowplaying_fixed_set_rebutja_valors_no_valids(string text, int minutes, string expected)
    {
        string until = DateTimeOffset.Now.AddMinutes(minutes).ToString("O");
        var response = Send($$"""{ "v": 1, "id": 1, "cmd": "nowplaying.fixed.set", "text": "{{text}}", "until": "{{until}}" }""");
        Assert.Equal("bad_params", response["error"]?["code"]?.GetValue<string>());
        Assert.Contains(expected, response["error"]!["message"]!.GetValue<string>());
        Assert.Null(_nowPlaying.Fixed);
    }

    [Fact]
    public void L_accio_TITOL_posa_el_titol_fix_des_de_l_hora_de_l_esdeveniment_durant_la_durada()
    {
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Esport i Punt", Mode = EventMode.System, StartTime = new TimeOnly(20, 0),
            Actions = [new EventAction { Type = EventActionType.Title, Params = """{"text":"Esport i Punt (en directe)","durada_seg":7200}""" }],
        });
        var ev = _events.Get(id)!;
        var executor = new EventExecutor(_library, _queue, null, nowPlaying: _nowPlaying);
        var at = DateTimeOffset.Now.AddSeconds(-2); // s'ha disparat fa un moment
        var occurrence = Recurrences.At(ev, DateOnly.FromDateTime(at.UtcDateTime), TimeOnly.FromDateTime(at.UtcDateTime), TimeZoneInfo.Utc);

        var outcome = executor.Execute(executor.Prepare(occurrence));

        Assert.Equal(ExecutionResult.Ok, outcome.Result);
        Assert.Contains("títol per a BUTT «Esport i Punt (en directe)»", outcome.Detail);
        var title = _nowPlaying.Fixed;
        Assert.NotNull(title);
        Assert.Equal("Esport i Punt", title.EventName);
        Assert.Equal(occurrence.At + TimeSpan.FromHours(2), title.Until);
        WaitForFile("Esport i Punt (en directe)");
    }

    [Theory]
    [InlineData("""{ "durada_seg": 3600 }""", "text")]
    [InlineData("""{ "text": "Esport i Punt" }""", "d'1 minut a 24 hores")]
    [InlineData("""{ "text": "Esport i Punt", "durada_seg": 30 }""", "d'1 minut a 24 hores")]
    [InlineData("""{ "text": "Esport i Punt", "durada_seg": 90000 }""", "d'1 minut a 24 hores")]
    public void Un_esdeveniment_amb_l_accio_TITOL_s_ha_de_desar_amb_text_i_durada(string paramsJson, string expected)
    {
        var response = Send($$"""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": {
                "name": "Esport i Punt", "mode": "SISTEMA", "startTime": "20:00:00",
                "actions": [ { "type": "TITOL", "params": {{paramsJson}} } ] } }
            """);
        Assert.Equal("bad_params", response["error"]?["code"]?.GetValue<string>());
        Assert.Contains(expected, response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void L_accio_TITOL_es_desa_i_es_descriu()
    {
        var response = Send("""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": {
                "name": "Esport i Punt", "mode": "SISTEMA", "startTime": "20:00:00",
                "actions": [ { "type": "TITOL", "params": { "text": "Esport i Punt", "durada_seg": 7200 } } ] } }
            """);
        Assert.True(response["ok"]!.GetValue<bool>(), response.ToJsonString());
        var action = response["result"]!["event"]!["actions"]![0]!;
        Assert.Equal("TITOL", action["type"]!.GetValue<string>());
        Assert.Equal("Títol per a BUTT «Esport i Punt»", action["label"]!.GetValue<string>());
        Assert.Equal("2 h 00 min", action["detail"]!.GetValue<string>());
    }
}
