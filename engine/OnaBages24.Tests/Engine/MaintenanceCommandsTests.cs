using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Manteniment de la programació (D-194) pel protocol, amb una base de dades de debò: maintenance.scan.</summary>
public sealed class MaintenanceCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly CommandRouter _router;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private int _next;

    public MaintenanceCommandsTests()
    {
        _db = Database.Open(_temp.File("manteniment.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns);
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
}
