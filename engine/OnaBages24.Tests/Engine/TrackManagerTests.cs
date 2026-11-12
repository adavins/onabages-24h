using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Gestor de pistes (fase 3, pas 7a): fitxa editable, canvi de fitxer, esborrar, categories i filtre per estat.</summary>
public sealed class TrackManagerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly CommandRouter _router;
    private readonly List<long?> _changed = [];
    private readonly long _music, _adult5;

    public TrackManagerTests()
    {
        _db = Database.Open(_temp.File("gestor.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
        _router.LibraryChanged += _changed.Add;
        _music = _library.EnsureCategory("Music");
        _adult5 = _library.EnsureSubcategory(_music, "Adult 5");
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    private static JsonNode Result(JsonObject response) =>
        response["result"] ?? throw new Xunit.Sdk.XunitException("Error: " + response["error"]?.ToJsonString());

    private static string Error(JsonObject response) => response["error"]!["code"]!.GetValue<string>();

    private long Track(string title, long? subcategory = null, FileState state = FileState.Ok, bool enabled = true) =>
        _library.AddTrack(new Track { Path = $@"C:\musica\{title}.mp3", Title = title, Artist = "Artista", SubcategoryId = subcategory, FileState = state, Enabled = enabled });

    private string Wav(string name, double seconds)
    {
        string path = _temp.File(name + ".wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        var samples = new float[(int)(seconds * 48000) * 2];
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    [Fact]
    public void La_fitxa_porta_tots_els_camps_les_etiquetes_i_la_subcategoria()
    {
        long id = Track("Cançó", _adult5);
        _library.SetTags(id, ["català", "80s"]);

        var track = Result(Send($$"""{ "v": 1, "id": 1, "cmd": "library.get", "trackId": {{id}} }"""))["track"]!;

        Assert.Equal("Cançó", track["title"]!.GetValue<string>());
        Assert.Equal("Adult 5", track["subcategory"]!["name"]!.GetValue<string>());
        Assert.Equal("Music", track["subcategory"]!["categoryName"]!.GetValue<string>());
        Assert.Equal(["80s", "català"], track["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(track["enabled"]!.GetValue<bool>());
        Assert.Empty(track["usedBy"]!.AsArray());
        Assert.Equal("bad_params", Error(Send("""{ "v": 1, "id": 2, "cmd": "library.get", "trackId": 999 }""")));
    }

    [Fact]
    public void Editar_canvia_nomes_els_camps_que_s_envien()
    {
        long id = Track("Abans", _adult5);
        long dance = _library.EnsureSubcategory(_music, "Dance90");

        var track = Result(Send($$"""
            { "v": 1, "id": 1, "cmd": "library.update", "trackId": {{id}}, "title": "  Després ", "type": "promo", "subcategoryId": {{dance}},
              "tags": ["nadal", " ", "Nadal"], "validFrom": "2026-12-01", "validUntil": "2027-01-06", "enabled": false, "notes": "Només per Nadal" }
            """))["track"]!;

        Assert.Equal("Després", track["title"]!.GetValue<string>());
        Assert.Equal("Artista", track["artist"]!.GetValue<string>()); // no s'ha enviat: es queda igual
        Assert.Equal("promo", track["type"]!.GetValue<string>());
        Assert.Equal(dance, track["subcategoryId"]!.GetValue<long>());
        Assert.Equal(["nadal"], track["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("2026-12-01", track["validFrom"]!.GetValue<string>());
        Assert.False(track["enabled"]!.GetValue<bool>());
        Assert.Equal("Només per Nadal", track["notes"]!.GetValue<string>());
        Assert.Equal([id], _changed);

        // Treure la subcategoria, les dates i les notes
        track = Result(Send($$"""{ "v": 1, "id": 2, "cmd": "library.update", "trackId": {{id}}, "subcategoryId": null, "validFrom": null, "validUntil": null, "notes": "" }"""))["track"]!;
        Assert.Null(track["subcategory"]);
        Assert.Null(track["validFrom"]);
        Assert.Null(track["notes"]);
    }

    [Theory]
    [InlineData("\"type\": \"cançó\"")]
    [InlineData("\"subcategoryId\": 999")]
    [InlineData("\"validFrom\": \"1/12/2026\"")]
    [InlineData("\"validFrom\": \"2026-12-10\", \"validUntil\": \"2026-12-01\"")]
    [InlineData("\"enabled\": \"no\"")]
    [InlineData("\"tags\": \"nadal\"")]
    public void Editar_amb_valors_dolents_no_canvia_res(string fields)
    {
        long id = Track("Igual", _adult5);
        Assert.Equal("bad_params", Error(Send($$"""{ "v": 1, "id": 1, "cmd": "library.update", "trackId": {{id}}, {{fields}} }""")));
        Assert.Equal(_adult5, _library.GetTrack(id)!.SubcategoryId);
        Assert.Empty(_changed);
    }

    [Fact]
    public void Una_pista_que_falta_es_torna_a_apuntar_a_un_altre_fitxer()
    {
        long id = Track("Perduda", _adult5, FileState.Missing);
        _library.UpdateTrack(_library.GetTrack(id)! with { StartMs = 100, MixMs = 2000, EndMs = 2500, CueSource = CueSource.Analyzer });
        string path = Wav("trobada", 3);

        var track = Result(Send(new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "library.relink", ["trackId"] = id, ["path"] = path }.ToJsonString()))["track"]!;

        Assert.Equal(path, track["path"]!.GetValue<string>());
        Assert.Equal("ok", track["fileState"]!.GetValue<string>());
        Assert.InRange(track["durationMs"]!.GetValue<long>(), 2990, 3010);
        Assert.Null(track["cueSource"]); // l'analitzador els tornarà a calcular (mentrestant, els d'abans)
        Assert.Equal(2000, track["mixMs"]!.GetValue<long>());

        // Un fitxer que no existeix, o que ja és d'una altra pista
        Assert.Equal("bad_params", Error(Send(new JsonObject { ["v"] = 1, ["id"] = 2, ["cmd"] = "library.relink", ["trackId"] = id, ["path"] = path + "x.wav" }.ToJsonString())));
        long other = Track("Altra");
        Assert.Equal("database_error", Error(Send(new JsonObject { ["v"] = 1, ["id"] = 3, ["cmd"] = "library.relink", ["trackId"] = other, ["path"] = path }.ToJsonString())));
    }

    [Fact]
    public void Els_punts_manuals_es_conserven_en_canviar_el_fitxer()
    {
        long id = Track("Manual", _adult5, FileState.Missing);
        _library.UpdateTrack(_library.GetTrack(id)! with { MixMs = 1500, CueSource = CueSource.Manual });
        Result(Send(new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "library.relink", ["trackId"] = id, ["path"] = Wav("manual", 2) }.ToJsonString()));
        Assert.Equal(CueSource.Manual, _library.GetTrack(id)!.CueSource);
    }

    [Fact]
    public void No_es_pot_esborrar_una_pista_que_fa_servir_un_esdeveniment()
    {
        long used = Track("Senyal");
        long free = Track("Lliure");
        _library.SetTags(free, ["x"]);
        long entry = _library.AddHistory(_library.GetTrack(free)!, DateTimeOffset.UtcNow, QueueOrigin.Manual);
        _events.Save(new ScheduledEvent { Name = "Senyal de les 12", Mode = EventMode.Overlay, StartTime = new TimeOnly(12, 0),
            Actions = [new EventAction { Type = EventActionType.Auxiliary, Params = $$"""{"songId":{{used}},"player":1}""" }] });

        var detail = Result(Send($$"""{ "v": 1, "id": 1, "cmd": "library.get", "trackId": {{used}} }"""))["track"]!;
        Assert.Equal("Senyal de les 12", detail["usedBy"]![0]!["name"]!.GetValue<string>());

        var refused = Send($$"""{ "v": 1, "id": 2, "cmd": "library.delete", "trackId": {{used}} }""");
        Assert.Equal("database_error", Error(refused));
        Assert.Contains("Senyal de les 12", refused["error"]!["message"]!.GetValue<string>());
        Assert.NotNull(_library.GetTrack(used));

        // La lliure s'esborra; l'historial la conserva sense l'enllaç
        Result(Send($$"""{ "v": 1, "id": 3, "cmd": "library.delete", "trackId": {{free}} }"""));
        Assert.Null(_library.GetTrack(free));
        var history = _library.SearchHistory(null, null, null, 10).Single(h => h.Id == entry);
        Assert.Null(history.TrackId);
        Assert.Equal("Lliure", history.Title);
        Assert.Equal([free], _changed);
    }

    [Fact]
    public void Es_creen_i_es_reanomenen_categories_i_subcategories()
    {
        long cat = Result(Send("""{ "v": 1, "id": 1, "cmd": "category.save", "name": " Falques " }"""))["id"]!.GetValue<long>();
        var sub = Result(Send($$"""{ "v": 1, "id": 2, "cmd": "subcategory.save", "categoryId": {{cat}}, "name": "Campanya" }"""));
        Assert.Equal("Falques", sub["categoryName"]!.GetValue<string>());
        long subId = sub["id"]!.GetValue<long>();

        Result(Send($$"""{ "v": 1, "id": 3, "cmd": "subcategory.save", "subcategoryId": {{subId}}, "name": "Campanya Nadal" }"""));
        Assert.Equal("Campanya Nadal", _library.GetSubcategory(subId)!.Name);
        Result(Send($$"""{ "v": 1, "id": 4, "cmd": "category.save", "categoryId": {{cat}}, "name": "Publicitat" }"""));
        Assert.Equal("Publicitat", _library.GetSubcategory(subId)!.CategoryName);

        // Noms repetits o buits
        Assert.Equal("database_error", Error(Send("""{ "v": 1, "id": 5, "cmd": "category.save", "name": "music" }""")));
        Assert.Equal("database_error", Error(Send($$"""{ "v": 1, "id": 6, "cmd": "subcategory.save", "categoryId": {{_music}}, "name": "adult 5" }""")));
        Assert.Equal("bad_params", Error(Send("""{ "v": 1, "id": 7, "cmd": "category.save", "name": "  " }""")));
        Assert.Equal([null, null, null, null], _changed);
    }

    [Fact]
    public void Buidar_una_subcategoria_primer_diu_que_fara_i_despres_esborra_les_que_no_son_en_cap_esdeveniment()
    {
        long free = Track("Lliure", _adult5);
        long used = Track("En un esdeveniment", _adult5);
        long other = Track("D'una altra", _library.EnsureSubcategory(_music, "Adult 9"));
        _events.Save(new ScheduledEvent { Name = "Promo", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{used}}}""" }] });
        new Rotations(_db).Save("OnaBages", [new RotationStep { SubcategoryId = _adult5 }]);

        var preview = Result(Send($$"""{ "v": 1, "id": 1, "cmd": "subcategory.empty", "subcategoryId": {{_adult5}}, "dryRun": true }"""));
        Assert.Equal((2, 1), (preview["tracks"]!.GetValue<int>(), preview["usedByEvents"]!.GetValue<int>()));
        Assert.Equal(["OnaBages"], preview["rotations"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.NotNull(_library.GetTrack(free)); // la prova no toca res
        Assert.Empty(_changed);

        var done = Result(Send($$"""{ "v": 1, "id": 2, "cmd": "subcategory.empty", "subcategoryId": {{_adult5}} }"""));
        Assert.Equal((1, 1), (done["removed"]!.GetValue<int>(), done["keptForEvents"]!.GetValue<int>()));
        Assert.Null(_library.GetTrack(free));
        Assert.Equal(FileState.Ok, _library.GetTrack(used)!.FileState); // la de l'esdeveniment, com era
        Assert.Equal(_adult5, _library.GetTrack(used)!.SubcategoryId);
        Assert.NotNull(_library.GetTrack(other));
        Assert.Equal([null], _changed);
    }

    [Fact]
    public void La_cerca_filtra_per_estat_i_els_filtres_porten_els_comptes()
    {
        Track("Bona", _adult5);
        Track("Falta", _adult5, FileState.Missing);
        Track("Retirada", _adult5, FileState.Retired);
        Track("Desactivada", _adult5, enabled: false);
        Track("Sola");

        string Titles(string state) => string.Join(",", Result(Send($$"""{ "v": 1, "id": 1, "cmd": "library.search", "state": "{{state}}" }"""))["tracks"]!
            .AsArray().Select(t => t!["title"]!.GetValue<string>()));
        Assert.Equal("Falta", Titles("missing"));
        Assert.Equal("Retirada", Titles("retired"));
        Assert.Equal("Desactivada", Titles("disabled"));
        Assert.Equal("Sola", Titles("unclassified"));
        Assert.Equal(5, Titles("all").Split(',').Length);
        Assert.Equal("bad_params", Error(Send("""{ "v": 1, "id": 2, "cmd": "library.search", "state": "esborrades" }""")));

        var states = Result(Send("""{ "v": 1, "id": 3, "cmd": "library.filters" }"""))["states"]!;
        Assert.Equal(5, states["all"]!.GetValue<int>());
        Assert.Equal(1, states["missing"]!.GetValue<int>());
        Assert.Equal(1, states["unclassified"]!.GetValue<int>());
    }
}
