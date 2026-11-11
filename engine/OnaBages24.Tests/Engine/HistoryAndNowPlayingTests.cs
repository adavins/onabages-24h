using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Consulta de l'historial i títol per a BUTT (fase 1, pas 9).</summary>
public class HistoryAndNowPlayingTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly NowPlaying _nowPlaying;
    private readonly CommandRouter _router;

    public HistoryAndNowPlayingTests()
    {
        _db = Database.Open(_temp.File("historial.db"));
        _library = new Library(_db);
        _nowPlaying = new NowPlaying(null, _temp.File("NowPlaying.txt"), new NowPlayingSettings(), NullLogger.Instance);
        _router = new CommandRouter(new EngineState(), () => { }, _library, nowPlaying: _nowPlaying);
    }

    public void Dispose()
    {
        _nowPlaying.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private void Played(string artist, string title, DateTimeOffset when, QueueOrigin origin = QueueOrigin.AutoDj)
    {
        long id = _library.AddTrack(new Track { Path = $@"O:\musica\{artist} - {title}.mp3", Artist = artist, Title = title });
        _library.AddHistory(_library.GetTrack(id)!, when, origin);
    }

    /// <summary>Espera que el fil d'escriptura hagi escrit el fitxer amb aquest text (com a molt 3 s).</summary>
    private static void WaitForFile(string path, string expected)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        string? text = null;
        while (DateTime.UtcNow < until)
        {
            try { text = File.ReadAllText(path); } catch (IOException) { }
            if (text == expected) return;
            Thread.Sleep(20);
        }
        Assert.Equal(expected, text);
    }

    private static QueueItem Item(string artist, string title, TrackType type = TrackType.Music) =>
        new() { Path = @"O:\x.mp3", Artist = artist, Title = title, Type = type };

    // --- Historial ---

    [Fact]
    public void History_list_retorna_les_mes_recents_primer_amb_limit()
    {
        var t0 = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        Played("Macy Gray", "Still", t0);
        Played("The Kooks", "Bad Habit", t0.AddMinutes(4), QueueOrigin.Manual);
        Played("Sopa de Cabra", "L'Empordà", t0.AddMinutes(8));

        var response = Send("""{ "v": 1, "id": 1, "cmd": "history.list", "limit": 2 }""");

        Assert.True(response["ok"]!.GetValue<bool>());
        var entries = response["result"]!["entries"]!.AsArray();
        Assert.Equal(["L'Empordà", "Bad Habit"], entries.Select(e => e!["title"]!.GetValue<string>()));
        Assert.True(response["result"]!["more"]!.GetValue<bool>());
        Assert.Equal("manual", entries[1]!["origin"]!.GetValue<string>());
        Assert.Equal("2026-11-10T09:04:00.0000000+00:00", entries[1]!["startedAt"]!.GetValue<string>());
        Assert.Null(entries[0]!["playedMs"]); // encara no s'ha tancat
    }

    [Fact]
    public void History_list_filtra_per_hores_i_per_text()
    {
        var t0 = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        Played("Macy Gray", "Still", t0);
        Played("The Kooks", "Bad Habit", t0.AddHours(1));
        Played("Macy Gray", "I Try", t0.AddHours(2));
        Played("Artista 100%", "Títol_amb_ratlla", t0.AddHours(3));

        var byText = Send("""{ "v": 1, "id": 1, "cmd": "history.list", "search": "macy" }""");
        Assert.Equal(["I Try", "Still"], byText["result"]!["entries"]!.AsArray().Select(e => e!["title"]!.GetValue<string>()));

        var byTime = Send("""{ "v": 1, "id": 2, "cmd": "history.list", "from": "2026-11-10T09:30:00Z", "to": "2026-11-10T11:00:00Z" }""");
        Assert.Equal(["Bad Habit"], byTime["result"]!["entries"]!.AsArray().Select(e => e!["title"]!.GetValue<string>()));
        Assert.False(byTime["result"]!["more"]!.GetValue<bool>());

        // Els caràcters especials de LIKE es busquen tal qual
        var special = Send("""{ "v": 1, "id": 3, "cmd": "history.list", "search": "100%" }""");
        Assert.Single(special["result"]!["entries"]!.AsArray());
        var underscore = Send("""{ "v": 1, "id": 4, "cmd": "history.list", "search": "l_a" }""");
        Assert.Single(underscore["result"]!["entries"]!.AsArray());
        Assert.Empty(Send("""{ "v": 1, "id": 5, "cmd": "history.list", "search": "St_ll" }""")["result"]!["entries"]!.AsArray());
    }

    [Fact]
    public void History_list_amb_dies_compta_el_dia_sencer_en_hora_local()
    {
        var day = new DateOnly(2026, 11, 10);
        DateTimeOffset Local(DateOnly d, int h, int m) => new(d.ToDateTime(new TimeOnly(h, m), DateTimeKind.Local));
        Played("A", "Abans", Local(day.AddDays(-1), 23, 59));
        Played("B", "Matí", Local(day, 0, 0));
        Played("C", "Nit", Local(day, 23, 59));
        Played("D", "Després", Local(day.AddDays(1), 0, 0));

        var response = Send("""{ "v": 1, "id": 1, "cmd": "history.list", "from": "2026-11-10", "to": "2026-11-10" }""");

        Assert.Equal(["Nit", "Matí"], response["result"]!["entries"]!.AsArray().Select(e => e!["title"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "history.list", "from": "ahir" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "history.list", "from": "2026-11-10T12:00:00" }""")] // sense zona: ambigu
    [InlineData("""{ "v": 1, "id": 1, "cmd": "history.list", "from": 5 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "history.list", "from": "2026-11-11", "to": "2026-11-10" }""")]
    public void History_list_rebutja_dates_no_valides(string json)
    {
        var response = Send(json);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("bad_params", response["error"]!["code"]!.GetValue<string>());
    }

    // --- Títol per a BUTT ---

    [Theory]
    [InlineData("$artist$ - $title$", "Macy Gray", "Still", "Macy Gray - Still")]
    [InlineData("$artist$ - $title$", "", "Matinal", "Matinal")]
    [InlineData("$artist$ - $title$", "Macy Gray", "", "Macy Gray")]
    [InlineData("Ona Bages · $TITLE$ ($Artist$)", "Macy Gray", "Still", "Ona Bages · Still (Macy Gray)")]
    [InlineData("$artist$ - $title$", "Macy\r\nGray", " Still ", "Macy  Gray - Still")]
    public void Format_omple_la_plantilla(string template, string artist, string title, string expected)
    {
        Assert.Equal(expected, NowPlaying.Format(template, artist, title));
    }

    [Fact]
    public void Escriu_el_titol_de_la_musica_i_deixa_el_d_abans_amb_les_falques()
    {
        string path = _temp.File("NowPlaying.txt");

        _nowPlaying.Show(Item("Macy Gray", "Still"));
        WaitForFile(path, "Macy Gray - Still");

        _nowPlaying.Show(Item("", "Cortina Ona Bages", TrackType.Sweeper));
        _nowPlaying.Show(Item("Anunciant", "Falca", TrackType.Commercial));
        Thread.Sleep(200);
        Assert.Equal("Macy Gray - Still", File.ReadAllText(path));
        Assert.Equal("Macy Gray - Still", _nowPlaying.Text);

        _nowPlaying.Show(Item("", "La Tarda", TrackType.Program));
        WaitForFile(path, "La Tarda");
        Assert.Null(_nowPlaying.Problem);
    }

    [Fact]
    public void El_fitxer_es_utf8_sense_bom()
    {
        string path = _temp.File("NowPlaying.txt");
        _nowPlaying.Show(Item("Sopa de Cabra", "L'Empordà"));
        WaitForFile(path, "Sopa de Cabra - L'Empordà");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("Sopa de Cabra - L'Empordà"), bytes);
    }

    [Fact]
    public void Si_no_es_pot_escriure_avisa_i_ho_torna_a_provar()
    {
        string folder = _temp.File("butt");
        string path = Path.Combine(folder, "NowPlaying.txt");
        Directory.CreateDirectory(folder);
        _nowPlaying.Configure(new NowPlayingSettings { Path = path });

        // Un fitxer obert sense permís per esborrar-lo ni escriure-hi: no es pot substituir
        using (var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            _nowPlaying.Show(Item("Macy Gray", "Still"));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (_nowPlaying.Problem is null && DateTime.UtcNow < until) Thread.Sleep(20);
            Assert.NotNull(_nowPlaying.Problem);
            Assert.Contains(path, _nowPlaying.Problem);
        }

        // En alliberar-lo, ho torna a provar sol (cada 5 s)
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (_nowPlaying.Problem is not null && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Null(_nowPlaying.Problem);
        Assert.Equal("Macy Gray - Still", File.ReadAllText(path));
    }

    [Fact]
    public void Nowplaying_set_canvia_la_plantilla_el_fitxer_i_els_tipus_i_ho_desa()
    {
        _nowPlaying.Show(Item("Macy Gray", "Still"));
        string other = _temp.File("NowPlaying2.txt").Replace("\\", "\\\\");

        var response = Send($$"""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "template": "$title$ / $artist$", "path": "{{other}}", "types": ["music", "sweeper"] }""");

        Assert.True(response["ok"]!.GetValue<bool>());
        var result = response["result"]!["nowPlaying"]!;
        Assert.Equal("Still / Macy Gray", result["text"]!.GetValue<string>());
        Assert.True(result["customPath"]!.GetValue<bool>());
        Assert.Equal(["music", "sweeper"], result["types"]!.AsArray().Select(t => t!.GetValue<string>()));
        WaitForFile(_temp.File("NowPlaying2.txt"), "Still / Macy Gray");

        // Es desa, i en tornar a arrencar es llegeix igual
        var saved = NowPlayingSettings.FromJson(_library.GetSetting(CommandRouter.NowPlayingSettingKey)!);
        Assert.Equal("$title$ / $artist$", saved.Template);
        Assert.Equal([TrackType.Music, TrackType.Sweeper], saved.Types);
        Assert.True(saved.Enabled);

        // path null: torna al fitxer per defecte
        var reset = Send("""{ "v": 1, "id": 2, "cmd": "nowplaying.set", "path": null }""");
        Assert.Equal(_temp.File("NowPlaying.txt"), reset["result"]!["nowPlaying"]!["path"]!.GetValue<string>());
        Assert.False(reset["result"]!["nowPlaying"]!["customPath"]!.GetValue<bool>());
    }

    [Fact]
    public void Desactivat_no_escriu_res()
    {
        Send("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "enabled": false }""");
        _nowPlaying.Show(Item("Macy Gray", "Still"));
        Thread.Sleep(200);

        Assert.False(File.Exists(_temp.File("NowPlaying.txt")));
        Assert.Equal(0, _nowPlaying.Writes);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "template": "Ona Bages" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "path": "NowPlaying.txt" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "path": "C:\\RadioDJ\\butt\\" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "types": ["music", "cançons"] }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "types": ["3"] }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "nowplaying.set", "enabled": "sí" }""")]
    public void Nowplaying_set_rebutja_valors_no_valids(string json)
    {
        var response = Send(json);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("bad_params", response["error"]!["code"]!.GetValue<string>());
        Assert.Null(_library.GetSetting(CommandRouter.NowPlayingSettingKey));
    }

    [Fact]
    public void Nowplaying_get_retorna_l_estat_per_defecte()
    {
        var result = Send("""{ "v": 1, "id": 1, "cmd": "nowplaying.get" }""")["result"]!["nowPlaying"]!;

        Assert.True(result["enabled"]!.GetValue<bool>());
        Assert.Equal("$artist$ - $title$", result["template"]!.GetValue<string>());
        Assert.Equal(["music", "program"], result["types"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Null(result["text"]);
        Assert.Null(result["problem"]);
    }

    // --- Notes (fase 3, pas 4a) ---

    [Fact]
    public void Les_notes_es_desen_es_conserven_i_avisen_les_altres_interficies()
    {
        var changed = new List<string>();
        _router.NotesChanged += changed.Add;
        Assert.Equal("", Send("""{ "v": 1, "id": 1, "cmd": "notes.get" }""")["result"]!["text"]!.GetValue<string>());

        var set = Send("""{ "v": 1, "id": 2, "cmd": "notes.set", "text": "Avui: falca de la fira a les 12.\nTrucar en Pere." }""");
        Assert.True(set["ok"]!.GetValue<bool>());
        Assert.Equal(["Avui: falca de la fira a les 12.\nTrucar en Pere."], changed);

        // Un router nou (com si el motor s'hagués reiniciat) les llegeix de la base de dades
        var again = new CommandRouter(new EngineState(), () => { }, _library);
        Assert.Equal("Avui: falca de la fira a les 12.\nTrucar en Pere.",
            again.Handle("""{ "v": 1, "id": 3, "cmd": "notes.get" }""")["result"]!["text"]!.GetValue<string>());

        // Es poden buidar
        Assert.True(Send("""{ "v": 1, "id": 4, "cmd": "notes.set", "text": "" }""")["ok"]!.GetValue<bool>());
        Assert.Equal("", Send("""{ "v": 1, "id": 5, "cmd": "notes.get" }""")["result"]!["text"]!.GetValue<string>());
    }

    // --- Atenuació per defecte (D-078) ---

    [Fact]
    public void L_atenuacio_per_defecte_es_canvia_i_es_conserva()
    {
        var get = Send("""{ "v": 1, "id": 1, "cmd": "ducking.get" }""")["result"]!;
        Assert.Equal(-10, get["auxiliary"]!.GetValue<double>());
        Assert.Equal(-8, get["sweepers"]!.GetValue<double>());

        var set = Send("""{ "v": 1, "id": 2, "cmd": "ducking.set", "auxiliary": -6 }""")["result"]!;
        Assert.Equal(-6, set["auxiliary"]!.GetValue<double>());
        Assert.Equal(-8, set["sweepers"]!.GetValue<double>()); // el que no s'envia es queda com està
        Assert.Equal((-6.0, -8.0), CommandRouter.ReadDucking(_library)); // el que llegeix el motor en arrencar

        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 3, "cmd": "ducking.set", "auxiliary": 3 }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 4, "cmd": "ducking.set", "sweepers": "fort" }""")["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Les_notes_sense_text_o_massa_llargues_responen_bad_params()
    {
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 1, "cmd": "notes.set" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 2, "cmd": "notes.set", "text": 5 }""")["error"]!["code"]!.GetValue<string>());
        var tooLong = new JsonObject { ["v"] = 1, ["id"] = 3, ["cmd"] = "notes.set", ["text"] = new string('a', CommandRouter.NotesMaxLength + 1) };
        Assert.Equal("bad_params", Send(tooLong.ToJsonString())["error"]!["code"]!.GetValue<string>());
    }
}
