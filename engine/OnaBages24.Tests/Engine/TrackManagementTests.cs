using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Pestanya Pistes (D-111): cerca amb tots els filtres, seleccionar-les totes, canvis en bloc, categories i etiquetes.</summary>
public sealed class TrackManagementTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Events _events;
    private readonly CommandRouter _router;
    private readonly long _falques, _musica;
    private int _next;

    public TrackManagementTests()
    {
        _db = Database.Open(_temp.File("pistes.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _events = new Events(_db);
        _falques = _library.EnsureSubcategory(_library.EnsureCategory("Falques"), "Comerços");
        _musica = _library.EnsureSubcategory(_library.EnsureCategory("Música"), "Adult");
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private long Add(string title, long sub, TrackType type = TrackType.Commercial, DateOnly? from = null, DateOnly? until = null, long durationMs = 20_000) =>
        _library.AddTrack(new Track
        {
            Path = _temp.File($"p{++_next}.mp3"), Title = title, Artist = "Ona", SubcategoryId = sub, Type = type,
            DurationMs = durationMs, ValidFrom = from, ValidUntil = until,
        });

    private JsonNode Result(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        var response = _router.Handle(msg.ToJsonString());
        Assert.True(response["result"] is not null, response.ToJsonString());
        return response["result"]!;
    }

    private string Error(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        var response = _router.Handle(msg.ToJsonString());
        Assert.NotNull(response["error"]);
        return response["error"]!["message"]!.GetValue<string>();
    }

    private static string[] Titles(JsonNode result) => result["tracks"]!.AsArray().Select(t => t!["title"]!.GetValue<string>()).ToArray();

    [Fact]
    public void La_cerca_filtra_per_vigencia_esdeveniments_i_reproduccions_i_dona_el_total()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        long vencuda = Add("Vençuda", _falques, until: today.AddDays(-1));
        Add("Futura", _falques, from: today.AddDays(10), until: today.AddDays(40));
        long vigent = Add("Vigent", _falques, until: today.AddDays(5));
        Add("Sense dates", _musica, TrackType.Music, durationMs: 200_000);
        _events.Save(new ScheduledEvent { Name = "Bloc", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{vigent}}}""" }] });
        _library.AddHistory(_library.GetTrack(vencuda)!, DateTimeOffset.UtcNow.AddDays(-3), QueueOrigin.Event);

        var all = Result(new() { ["cmd"] = "library.search", ["limit"] = 2 });
        Assert.Equal(4, all["total"]!.GetValue<int>()); // el total, encara que la pàgina en tingui 2
        Assert.True(all["more"]!.GetValue<bool>());

        Assert.Equal(["Vençuda"], Titles(Result(new() { ["cmd"] = "library.search", ["validity"] = "expired" })));
        Assert.Equal(["Futura"], Titles(Result(new() { ["cmd"] = "library.search", ["validity"] = "notYet" })));
        Assert.Equal(["Sense dates", "Vigent"], Titles(Result(new() { ["cmd"] = "library.search", ["validity"] = "valid" })));
        // Amb dates que toquen l'interval (la de sense dates, no)
        var overlaps = Result(new() { ["cmd"] = "library.search", ["validity"] = "overlaps",
            ["validFrom"] = today.AddDays(20).ToString("yyyy-MM-dd"), ["validTo"] = today.AddDays(30).ToString("yyyy-MM-dd") });
        Assert.Equal(["Futura"], Titles(overlaps));

        Assert.Equal(["Vigent"], Titles(Result(new() { ["cmd"] = "library.search", ["inEvents"] = true })));
        Assert.Equal(3, Result(new() { ["cmd"] = "library.search", ["inEvents"] = false })["total"]!.GetValue<int>());

        Assert.Equal(["Vençuda"], Titles(Result(new() { ["cmd"] = "library.search",
            ["playedFrom"] = today.AddDays(-7).ToString("yyyy-MM-dd"), ["playedTo"] = today.ToString("yyyy-MM-dd") })));
        Assert.Equal(3, Result(new() { ["cmd"] = "library.search", ["neverPlayed"] = true })["total"]!.GetValue<int>());

        // Ordenar per durada, de més llarga a més curta; i per reproduccions
        Assert.Equal("Sense dates", Titles(Result(new() { ["cmd"] = "library.search", ["sort"] = "duration", ["desc"] = true }))[0]);
        Assert.Equal("Vençuda", Titles(Result(new() { ["cmd"] = "library.search", ["sort"] = "plays", ["desc"] = true }))[0]);
        Assert.Contains("validity", Error(new() { ["cmd"] = "library.search", ["validity"] = "caducada" }));
    }

    [Fact]
    public void Seleccionar_totes_les_d_un_filtre_i_canviar_les_en_bloc()
    {
        long a = Add("A", _falques), b = Add("B", _falques), c = Add("C", _musica, TrackType.Music);

        var ids = Result(new() { ["cmd"] = "library.ids", ["subcategoryId"] = _falques });
        Assert.Equal([a, b], ids["ids"]!.AsArray().Select(n => n!.GetValue<long>()));
        Assert.Equal(2, ids["total"]!.GetValue<int>());

        // «Migrar»: les falques passen a una altra subcategoria
        long altres = _library.EnsureSubcategory(_library.EnsureCategory("Falques"), "Institucionals");
        var moved = Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = new JsonArray(a, b), ["action"] = "subcategory", ["subcategoryId"] = altres });
        Assert.Equal(2, moved["changed"]!.GetValue<int>());
        Assert.All(new[] { a, b }, id => Assert.Equal(altres, _library.GetTrack(id)!.SubcategoryId));

        Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = new JsonArray(a, c), ["action"] = "disable" });
        Assert.False(_library.GetTrack(a)!.Enabled);
        Assert.True(_library.GetTrack(b)!.Enabled);

        Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = new JsonArray(a, b), ["action"] = "addTag", ["tag"] = "Nadal" });
        Assert.Equal(["Nadal"], _library.GetTags(b));
        long tag = _library.Tags().Single().Id;
        Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = new JsonArray(a), ["action"] = "removeTag", ["tagId"] = tag });
        Assert.Empty(_library.GetTags(a));
        Assert.Contains("«pinta» no existeix", Error(new() { ["cmd"] = "library.bulk", ["trackIds"] = new JsonArray(a), ["action"] = "pinta" }));
    }

    [Fact]
    public void Esborrar_en_bloc_respecta_les_que_fan_servir_els_esdeveniments_i_avisa_de_les_que_han_sonat()
    {
        long usada = Add("Usada", _falques), sonada = Add("Sonada", _falques), lliure = Add("Lliure", _falques);
        _events.Save(new ScheduledEvent { Name = "Bloc 10:50", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 50),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{usada}}}""" }] });
        _library.AddHistory(_library.GetTrack(sonada)!, DateTimeOffset.UtcNow.AddMonths(-6), QueueOrigin.Event);
        var all = new JsonArray(usada, sonada, lliure);

        var preview = Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = all.DeepClone(), ["action"] = "delete", ["dryRun"] = true });
        Assert.Equal(1, preview["playedRecently"]!.GetValue<int>());
        Assert.Equal("Bloc 10:50", Assert.Single(preview["kept"]!.AsArray())!["events"]!.GetValue<string>());
        Assert.Equal(0, preview["deleted"]!.GetValue<int>());
        Assert.NotNull(_library.GetTrack(lliure)); // la prova no esborra res

        var done = Result(new() { ["cmd"] = "library.bulk", ["trackIds"] = all.DeepClone(), ["action"] = "delete" });
        Assert.Equal(2, done["deleted"]!.GetValue<int>());
        Assert.NotNull(_library.GetTrack(usada));
        Assert.Null(_library.GetTrack(lliure));
    }

    [Fact]
    public void Nomes_es_poden_esborrar_categories_i_subcategories_que_ningu_fa_servir()
    {
        long categoria = _library.EnsureCategory("Promos");
        long buida = _library.EnsureSubcategory(categoria, "Antigues");
        long ambPista = _library.EnsureSubcategory(categoria, "Noves");
        Add("Promo", ambPista, TrackType.Promo);
        long perRotacio = _library.EnsureSubcategory(categoria, "De rotació");
        _rotations.Save("Matins", [new RotationStep { SubcategoryId = perRotacio }]);

        Assert.Contains("té 1 pista", Error(new() { ["cmd"] = "subcategory.delete", ["subcategoryId"] = ambPista }));
        Assert.Contains("«Matins»", Error(new() { ["cmd"] = "subcategory.delete", ["subcategoryId"] = perRotacio }));
        Assert.Contains("subcategories", Error(new() { ["cmd"] = "category.delete", ["categoryId"] = categoria }));

        Result(new() { ["cmd"] = "subcategory.delete", ["subcategoryId"] = buida });
        Assert.Null(_library.GetSubcategory(buida));

        // Una subcategoria que fa servir un esdeveniment (acció SWEEPERS) tampoc
        long sweepers = _library.EnsureSubcategory(categoria, "Sweepers");
        _events.Save(new ScheduledEvent { Name = "Sweepers del matí", Mode = EventMode.System, StartTime = new TimeOnly(7, 0),
            Actions = [new EventAction { Type = EventActionType.Sweepers, Params = $$"""{"subcategoriaId":{{sweepers}}}""" }] });
        Assert.Contains("«Sweepers del matí»", Error(new() { ["cmd"] = "subcategory.delete", ["subcategoryId"] = sweepers }));

        // Una categoria sense subcategories sí
        long sola = _library.EnsureCategory("Buida");
        Result(new() { ["cmd"] = "category.delete", ["categoryId"] = sola });
    }

    [Fact]
    public void El_gestor_d_etiquetes_canvia_noms_fusiona_i_no_esborra_les_de_les_rotacions()
    {
        long a = Add("A", _musica, TrackType.Music), b = Add("B", _musica, TrackType.Music);
        _library.SetTags(a, ["català"]);
        _library.SetTags(b, ["catala", "80s", "CATALÀ"]); // «CATALÀ» és la mateixa que «català»
        long accent = _library.EnsureTag("català"), sense = _library.EnsureTag("catala"), vuitanta = _library.EnsureTag("80s");
        _rotations.Save("Matins", [new RotationStep { SubcategoryId = _musica, TagId = sense }]);

        var list = Result(new() { ["cmd"] = "tags.list" })["tags"]!.AsArray();
        Assert.Equal(["80s", "catala", "català"], list.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Equal("Matins", list[1]!["rotations"]![0]!.GetValue<string>());

        Assert.Contains("fusiona", Error(new() { ["cmd"] = "tags.rename", ["tagId"] = vuitanta, ["name"] = "CATALÀ" }));
        Assert.Contains("«Matins»", Error(new() { ["cmd"] = "tags.delete", ["tagId"] = sense }));

        // «catala» dins de «català»: la pista B i el pas de la rotació passen a «català»
        Result(new() { ["cmd"] = "tags.merge", ["fromId"] = sense, ["intoId"] = accent });
        Assert.Equal(["80s", "català"], _library.GetTags(b).Order());
        Assert.Equal(accent, _rotations.List().Single().Steps[0].TagId);

        Result(new() { ["cmd"] = "tags.rename", ["tagId"] = vuitanta, ["name"] = "anys 80" });
        Assert.Equal(["anys 80", "català"], _library.GetTags(b).Order());
        Result(new() { ["cmd"] = "tags.delete", ["tagId"] = vuitanta });
        Assert.Equal(["català"], _library.GetTags(b));
    }
}
