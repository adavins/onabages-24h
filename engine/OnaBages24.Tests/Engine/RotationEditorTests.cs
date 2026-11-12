using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Editor de rotacions (fase 3, pas 8a; D-094).</summary>
public sealed class RotationEditorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly AutoDj _autoDj;
    private readonly CommandRouter _router;
    private readonly List<string> _events = [];
    private readonly long _music, _adult5, _adult9, _catala;

    public RotationEditorTests()
    {
        _db = Database.Open(_temp.File("rotacions.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _autoDj = new AutoDj(_library, _rotations, _queue);
        _router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: _autoDj, rotations: _rotations, events: new Events(_db));
        _router.RotationsChanged += () => _events.Add("changed");
        _music = _library.EnsureCategory("Music");
        _adult5 = _library.EnsureSubcategory(_music, "Adult 5");
        _adult9 = _library.EnsureSubcategory(_music, "Adult 9");
        _catala = _library.EnsureTag("català");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private static JsonNode Result(JsonObject r) => r["result"] ?? throw new Xunit.Sdk.XunitException(r["error"]!.ToJsonString());
    private static string Error(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

    private JsonArray Steps(params JsonObject[] steps) => new(steps);

    [Fact]
    public void Es_crea_una_rotacio_amb_els_passos_en_ordre()
    {
        var saved = Result(Send(new()
        {
            ["cmd"] = "rotations.save", ["name"] = "Proves",
            ["steps"] = Steps(
                new JsonObject { ["subcategoryId"] = _adult5 },
                new JsonObject { ["categoryId"] = _music, ["tagId"] = _catala, ["selection"] = "random", ["repeatRule"] = false },
                new JsonObject { ["subcategoryId"] = _adult9, ["selection"] = "leastPlayed", ["sweeper"] = true }),
        }))["rotation"]!;

        var steps = saved["steps"]!.AsArray();
        Assert.Equal(3, steps.Count);
        Assert.Equal(_adult5, steps[0]!["subcategoryId"]!.GetValue<long>());
        Assert.Equal(_music, steps[1]!["categoryId"]!.GetValue<long>());
        Assert.Null(steps[1]!["subcategoryId"]);
        Assert.Equal("català", steps[1]!["tag"]!.GetValue<string>());
        Assert.False(steps[1]!["repeatRule"]!.GetValue<bool>());
        Assert.Equal("leastPlayed", steps[2]!["selection"]!.GetValue<string>());
        Assert.True(steps[2]!["sweeper"]!.GetValue<bool>());
        Assert.Equal(["changed"], _events);
    }

    [Fact]
    public void Editar_la_rotacio_activa_canvia_els_passos_de_l_AutoDJ()
    {
        long id = _rotations.Create("OnaBages", [new RotationStep { SubcategoryId = _adult5 }, new RotationStep { SubcategoryId = _adult9 }]);
        _autoDj.Configure(true, id, "a mà");

        Result(Send(new() { ["cmd"] = "rotations.save", ["rotationId"] = id, ["name"] = "OnaBages nova",
            ["steps"] = Steps(new JsonObject { ["subcategoryId"] = _adult9 }) }));

        Assert.Equal("OnaBages nova", _autoDj.Rotation!.Name);
        Assert.Single(_autoDj.Rotation.Steps);
        Assert.Equal(_adult9, _autoDj.Rotation.Steps[0].SubcategoryId);
    }

    [Fact]
    public void No_es_pot_esborrar_l_activa_ni_la_d_un_esdeveniment()
    {
        long active = _rotations.Create("Activa", [new RotationStep { SubcategoryId = _adult5 }]);
        long used = _rotations.Create("Dels dissabtes", [new RotationStep { SubcategoryId = _adult9 }]);
        long free = _rotations.Create("Lliure", [new RotationStep { SubcategoryId = _adult9 }]);
        _autoDj.Configure(true, active, "a mà");
        new Events(_db).Save(new ScheduledEvent { Name = "Dissabte", Mode = EventMode.System, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Rotation, Params = $$"""{"rotacioId":{{used}}}""" }] });

        Assert.Contains("activa", Send(new() { ["cmd"] = "rotations.delete", ["rotationId"] = active })["error"]!["message"]!.GetValue<string>());
        Assert.Contains("Dissabte", Send(new() { ["cmd"] = "rotations.delete", ["rotationId"] = used })["error"]!["message"]!.GetValue<string>());
        Result(Send(new() { ["cmd"] = "rotations.delete", ["rotationId"] = free }));
        Assert.Null(_rotations.Get(free));
        Assert.NotNull(_rotations.Get(used));
    }

    [Theory]
    [InlineData("""{ "cmd": "rotations.save", "name": "Buida", "steps": [] }""")]
    [InlineData("""{ "cmd": "rotations.save", "name": "", "steps": [ { "subcategoryId": 1 } ] }""")]
    [InlineData("""{ "cmd": "rotations.save", "name": "Sense font", "steps": [ { "selection": "random" } ] }""")]
    [InlineData("""{ "cmd": "rotations.save", "name": "Subcategoria", "steps": [ { "subcategoryId": 999 } ] }""")]
    [InlineData("""{ "cmd": "rotations.save", "name": "Selecció", "steps": [ { "subcategoryId": 1, "selection": "sovint" } ] }""")]
    public void Les_dades_dolentes_no_desen_res(string json)
    {
        var msg = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("bad_params", Error(Send(msg)));
        Assert.Empty(_rotations.List());
    }

    [Fact]
    public void Dos_noms_iguals_no_poden_ser()
    {
        _rotations.Create("OnaBages", [new RotationStep { SubcategoryId = _adult5 }]);
        var r = Send(new() { ["cmd"] = "rotations.save", ["name"] = "onabages", ["steps"] = Steps(new JsonObject { ["subcategoryId"] = _adult5 }) });
        Assert.Equal("database_error", Error(r));
    }
}
