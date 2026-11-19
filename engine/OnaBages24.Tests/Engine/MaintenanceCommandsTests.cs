using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Manteniment de la programació (D-194) pel protocol, amb una base de dades de debò: maintenance.scan i maintenance.apply.</summary>
public sealed class MaintenanceCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly Backups _backups;
    private readonly UserStore _users;
    private readonly CommandRouter _router;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private int _next;

    public MaintenanceCommandsTests()
    {
        _db = Database.Open(_temp.File("manteniment.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _backups = new Backups(_db, _temp.File("copies"));
        _users = new UserStore(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns, backups: _backups, users: _users);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private long AddTrack(string title, DateOnly? until = null, long? subcategoryId = null) =>
        _library.AddTrack(new Track
        {
            Path = _temp.File($"p{++_next}.mp3"), Type = TrackType.Commercial, Title = title, DurationMs = 20_000,
            ValidUntil = until, SubcategoryId = subcategoryId,
        });

    private long AddEvent(string name, long trackId, DateOnly? endDate = null, int hour = 9) => _events.Save(new ScheduledEvent
    {
        Name = name, Mode = EventMode.Cut, StartTime = new TimeOnly(hour, 0), EndDate = endDate,
        Actions = [new EventAction { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = trackId }.ToJsonString() }],
    });

    private JsonNode Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        var reply = _router.Handle(msg.ToJsonString());
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["result"]!;
    }

    /// <summary>Les claus de les propostes de l'anàlisi.</summary>
    private string[] Keys() => Send(new() { ["cmd"] = "maintenance.scan" })["findings"]!.AsArray().Select(f => f!["key"]!.GetValue<string>()).ToArray();

    private JsonNode Apply(params string[] keys) =>
        Send(new() { ["cmd"] = "maintenance.apply", ["keys"] = new JsonArray(keys.Select(k => (JsonNode)k).ToArray()) });

    /// <summary>Les propostes, com «ESDEVENIMENT_ACABAT ESBORRA_ESDEVENIMENT Nom».</summary>
    private string[] Findings() => Send(new() { ["cmd"] = "maintenance.scan" })["findings"]!.AsArray()
        .Select(f => $"{f!["case"]} {f["action"]} {f["name"]}").ToArray();

    [Fact]
    public void ScanFindsWhatHasPiledUp()
    {
        long track = AddTrack("Magazín");
        AddEvent("Magazín", track);
        AddEvent("Programa d'estiu", track, endDate: _today.AddDays(-30), hour: 10);
        AddTrack("Falca de l'any passat", until: _today.AddDays(-100));
        long music = _library.EnsureCategory("Música");
        _library.EnsureSubcategory(music, "Nadal 2019");
        long pop = _library.EnsureSubcategory(music, "Pop");
        AddTrack("Cançó", subcategoryId: pop);

        Assert.Equal([
            "ESDEVENIMENT_ACABAT ESBORRA_ESDEVENIMENT Programa d'estiu",
            "PISTA_CADUCADA ESBORRA_PISTA Falca de l'any passat",
            "SUBCATEGORIA_BUIDA ESBORRA_SUBCATEGORIA Música / Nadal 2019",
        ], Findings());
    }

    [Fact]
    public void ScanChangesNothing()
    {
        long track = AddTrack("Magazín");
        AddEvent("Programa d'estiu", track, endDate: _today.AddDays(-30));
        Send(new() { ["cmd"] = "maintenance.scan" });
        Assert.Single(_events.List());
        Assert.NotNull(_library.GetTrack(track));
    }

    [Fact]
    public void ApplyDoesOnlyWhatIsMarkedAfterABackup()
    {
        long track = AddTrack("Magazín");
        long ended = AddEvent("Programa d'estiu", track, endDate: _today.AddDays(-30));
        long other = AddEvent("Programa de primavera", track, endDate: _today.AddDays(-90), hour: 11);
        long oldTrack = AddTrack("Falca de l'any passat", until: _today.AddDays(-100));

        var result = Apply($"event:{ended}", $"track:{oldTrack}");

        Assert.Null(_events.Get(ended));
        Assert.NotNull(_events.Get(other)); // no estava marcat
        Assert.Null(_library.GetTrack(oldTrack));
        Assert.Equal(2, result["done"]!.AsArray().Count);
        Assert.Empty(result["skipped"]!.AsArray());
        Assert.Null(result["error"]);
        // La còpia d'abans, que no entra a la rotació i surt com a «maintenance»
        var backup = Assert.Single(_backups.ListMaintenance());
        Assert.Equal(backup.Path, result["backup"]!.GetValue<string>());
        Assert.Contains(Send(new() { ["cmd"] = "backups.list" })["backups"]!.AsArray(), b => b!["kind"]!.GetValue<string>() == "maintenance");
        // I a la còpia hi és tot, per poder desfer-ho
        Assert.Equal(2, Backups.Inspect(backup.Path).Events);
    }

    [Fact]
    public void AProposalThatIsNoLongerThereIsSkipped()
    {
        long track = AddTrack("Magazín");
        long ended = AddEvent("Programa d'estiu", track, endDate: _today.AddDays(-30));
        string key = Assert.Single(Keys());
        // Mentrestant, algú l'allarga: ja no s'ha d'esborrar
        _events.Save(_events.Get(ended)! with { EndDate = _today.AddDays(30) });

        var result = Apply(key);

        Assert.NotNull(_events.Get(ended));
        Assert.Empty(result["done"]!.AsArray());
        Assert.Equal(key, Assert.Single(result["skipped"]!.AsArray())!["key"]!.GetValue<string>());
    }

    [Fact]
    public void APlayedExpiredTrackIsDisabledAndKeepsItsHistory()
    {
        long track = AddTrack("Falca de l'estiu", until: _today.AddDays(-40));
        _library.AddHistory(_library.GetTrack(track)!, DateTimeOffset.Now.AddDays(-50), QueueOrigin.Event);

        Apply($"track:{track}");

        var after = _library.GetTrack(track);
        Assert.NotNull(after);
        Assert.False(after.Enabled);
        Assert.Equal(1, _library.PlaysSince(track, DateTimeOffset.Now.AddYears(-2)));
    }

    [Fact]
    public void PastExceptionsAndBrokenActionsAreRemovedAndTheRestIsKept()
    {
        long track = AddTrack("Magazín");
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Magazín", Mode = EventMode.Cut, StartTime = new TimeOnly(9, 0),
            ExcludedDates = [_today.AddDays(-10), _today.AddDays(10)],
            Actions = [new EventAction { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = track }.ToJsonString() }],
        });
        long system = _events.Save(new ScheduledEvent
        {
            Name = "Canvi de rotació", Mode = EventMode.System, StartTime = new TimeOnly(8, 0),
            Actions = [
                new EventAction { Type = EventActionType.Rotation, Params = """{"rotacioId":77}""" },
                new EventAction { Type = EventActionType.AutoDj, Params = """{"actiu":true}""" },
            ],
        });

        Apply($"exceptions:{id}", $"actions:{system}");

        Assert.Equal([_today.AddDays(10)], _events.Get(id)!.ExcludedDates);
        var actions = _events.Get(system)!.Actions;
        Assert.Equal(EventActionType.AutoDj, Assert.Single(actions).Type);
        Assert.Empty(Keys());
    }

    [Fact]
    public void EachAppliedChangeIsLoggedWithItsReason()
    {
        long track = AddTrack("Magazín");
        long ended = AddEvent("Programa d'estiu", track, endDate: _today.AddDays(-30));

        Apply($"event:{ended}");

        var entry = Assert.Single(_users.ListChanges(new ChangeFilter()).Entries);
        Assert.Equal("maintenance.apply", entry.Command);
        Assert.Equal("Manteniment: ha esborrat l'esdeveniment «Programa d'estiu»", entry.Summary);
        Assert.Contains("Es va acabar", entry.Detail);
    }

    [Fact]
    public void ApplyWithoutKeysIsAnError()
    {
        var reply = _router.Handle(new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "maintenance.apply", ["keys"] = new JsonArray() }.ToJsonString());
        Assert.Equal("bad_params", reply["error"]!["code"]!.GetValue<string>());
        Assert.Empty(_backups.ListMaintenance()); // ni tan sols la còpia
    }
}
