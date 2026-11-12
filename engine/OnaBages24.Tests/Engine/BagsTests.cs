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

/// <summary>Bosses amb nom (fase 3, pas 8c; D-014, D-096).</summary>
public sealed class BagsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Events _events;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly AutoDj _autoDj;
    private readonly CommandRouter _router;
    private readonly long _adult5, _adult6, _bag, _rotation;
    private int _next;

    public BagsTests()
    {
        _db = Database.Open(_temp.File("bosses.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _events = new Events(_db);
        _autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = 1 });
        _router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: _autoDj, rotations: _rotations, events: _events);
        long music = _library.EnsureCategory("Music");
        _adult5 = _library.EnsureSubcategory(music, "Adult 5");
        _adult6 = _library.EnsureSubcategory(music, "Adult 6");
        Track(_adult5);
        Track(_adult6);
        _bag = _rotations.CreateBag("Generalista A", _adult5, "a mà", DateTimeOffset.UtcNow);
        _rotation = _rotations.Create("OnaBages", [new RotationStep { BagId = _bag }]);
        _autoDj.Configure(true, _rotation, "a mà");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Track(long sub) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), SubcategoryId = sub, Artist = $"Artista {_next}", Title = $"Cançó {_next}",
        DurationMs = 200_000, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
    });

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private long? NextPick()
    {
        foreach (var item in _queue.Upcoming.ToList()) _queue.Remove(item.Id);
        _autoDj.Tick();
        return _queue.Upcoming.FirstOrDefault()?.TrackId is { } id ? _library.GetTrack(id)!.SubcategoryId : null;
    }

    [Fact]
    public void Un_pas_amb_bossa_tria_de_la_subcategoria_que_te_la_bossa_ara()
    {
        Assert.Equal(_adult5, NextPick());
        Assert.Equal("Bossa «Generalista A» (Music / Adult 5)", _autoDj.Rotation!.Steps[0].ToString());

        var set = Send(new() { ["cmd"] = "bags.set", ["bagId"] = _bag, ["subcategoryId"] = _adult6 })["result"]!["bag"]!;
        Assert.Equal("Music / Adult 6", set["subcategory"]!.GetValue<string>());
        Assert.Equal("a mà", set["changedBy"]!.GetValue<string>());

        Assert.Equal(_adult6, NextPick()); // la rotació no s'ha tocat
        Assert.Equal(_bag, _rotations.Get(_rotation)!.Steps[0].BagId);
    }

    [Fact]
    public void Una_bossa_sense_subcategoria_fa_saltar_el_pas_amb_avis()
    {
        Send(new() { ["cmd"] = "bags.set", ["bagId"] = _bag, ["subcategoryId"] = null });
        NextPick();
        Assert.Contains("Generalista A", _autoDj.Problem);
        Assert.Contains("sense subcategoria", _autoDj.Problem);
    }

    [Fact]
    public void L_accio_BOSSA_del_planificador_canvia_la_bossa()
    {
        var now = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var executor = new EventExecutor(_library, _queue, _autoDj);
        var scheduler = new Scheduler(_events, executor, NullLogger.Instance, clock: () => now, zone: TimeZoneInfo.Utc);
        _events.Save(new ScheduledEvent { Name = "Canvi de bosses", Mode = EventMode.System, StartTime = new TimeOnly(0, 0, 5),
            Actions = [new EventAction { Type = EventActionType.Bag, Params = $$"""{"bossaId":{{_bag}},"subcategoriaId":{{_adult6}}}""" }] });

        for (int s = 0; s < 40; s++)
        {
            now += TimeSpan.FromMilliseconds(250);
            scheduler.Tick();
        }

        var bag = _rotations.GetBag(_bag)!;
        Assert.Equal(_adult6, bag.SubcategoryId);
        Assert.Equal("esdeveniment «Canvi de bosses»", bag.ChangedBy);
    }

    [Fact]
    public void Es_creen_i_reanomenen_i_no_s_esborren_si_es_fan_servir()
    {
        var created = Send(new() { ["cmd"] = "bags.save", ["name"] = "Generalista B", ["subcategoryId"] = _adult6 })["result"]!["bag"]!;
        long b = created["id"]!.GetValue<long>();
        Send(new() { ["cmd"] = "bags.save", ["bagId"] = b, ["name"] = "Generalista C" });
        Assert.Equal("Generalista C", _rotations.GetBag(b)!.Name);

        // La A la fa servir la rotació: no es pot esborrar
        Assert.Contains("OnaBages", Send(new() { ["cmd"] = "bags.delete", ["bagId"] = _bag })["error"]!["message"]!.GetValue<string>());
        // La C, la canvia un esdeveniment
        _events.Save(new ScheduledEvent { Name = "Mensual", Mode = EventMode.System, StartTime = new TimeOnly(0, 0),
            Actions = [new EventAction { Type = EventActionType.Bag, Params = $$"""{"bossaId":{{b}},"subcategoriaId":{{_adult5}}}""" }] });
        Assert.Contains("Mensual", Send(new() { ["cmd"] = "bags.delete", ["bagId"] = b })["error"]!["message"]!.GetValue<string>());

        long free = _rotations.CreateBag("Lliure", null, null, DateTimeOffset.UtcNow);
        Assert.NotNull(Send(new() { ["cmd"] = "bags.delete", ["bagId"] = free })["result"]);
        Assert.Null(_rotations.GetBag(free));
        Assert.Equal("database_error", Send(new() { ["cmd"] = "bags.save", ["name"] = "generalista a" })["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void Els_passos_es_desen_amb_la_bossa_i_la_llista_la_porta()
    {
        var saved = Send(new() { ["cmd"] = "rotations.save", ["rotationId"] = _rotation, ["name"] = "OnaBages",
            ["steps"] = new JsonArray(new JsonObject { ["bagId"] = _bag }, new JsonObject { ["subcategoryId"] = _adult6 }) })["result"]!["rotation"]!;
        var steps = saved["steps"]!.AsArray();
        Assert.Equal(_bag, steps[0]!["bagId"]!.GetValue<long>());
        Assert.Equal("Generalista A", steps[0]!["bag"]!.GetValue<string>());
        Assert.Equal(_adult5, steps[0]!["subcategoryId"]!.GetValue<long>()); // la d'ara
        Assert.Null(steps[1]!["bagId"]);

        var bags = Send(new() { ["cmd"] = "bags.list" })["result"]!["bags"]!.AsArray();
        Assert.Equal(1, bags[0]!["steps"]!.GetValue<int>());
    }
}
