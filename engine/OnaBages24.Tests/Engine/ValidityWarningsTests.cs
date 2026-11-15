using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Avisos de vigència (fase 4, pas 6b; D-146): validity.list i validity.removeFromEvents.</summary>
public sealed class ValidityWarningsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly CommandRouter _router;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);
    private int _next;

    public ValidityWarningsTests()
    {
        _db = Database.Open(_temp.File("vigencia.db"));
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

    private long AddTrack(string title, DateOnly? from = null, DateOnly? until = null, bool enabled = true, FileState file = FileState.Ok) =>
        _library.AddTrack(new Track
        {
            Path = _temp.File($"p{++_next}.mp3"), Type = TrackType.Commercial, Artist = "Prova", Title = title,
            DurationMs = 20_000, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
            ValidFrom = from, ValidUntil = until, Enabled = enabled, FileState = file,
        });

    private long AddCampaign(string name, DateOnly start, DateOnly end, CampaignState state, params CampaignSpot[] spots) =>
        _campaigns.Save(new Campaign { Name = name, StartDate = start, EndDate = end, State = state, Spots = spots }, DateTimeOffset.UtcNow);

    private static EventAction Track(long id) => new() { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = id }.ToJsonString() };
    private static EventAction CampaignAction(long id) => new() { Type = EventActionType.Campaign, Params = new JsonObject { ["campanyaId"] = id }.ToJsonString() };

    private long AddEvent(string name, bool block, bool enabled, params EventAction[] actions) => _events.Save(new ScheduledEvent
    {
        Name = name, Mode = EventMode.Queue, StartTime = new TimeOnly(9, 0), CommercialBlock = block, Enabled = enabled, Actions = actions,
    });

    private JsonNode Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        var reply = _router.Handle(msg.ToJsonString());
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["result"]!;
    }

    /// <summary>Els avisos, com «CADUCADA track Nom».</summary>
    private string[] Warnings() => Send(new() { ["cmd"] = "validity.list" })["warnings"]!.AsArray()
        .Select(w => $"{w!["kind"]} {w["subject"]} {w["name"]}").ToArray();

    [Fact]
    public void Avisa_de_les_pistes_dels_esdeveniments_actius()
    {
        long expired = AddTrack("Caducada", until: _today.AddDays(-1));
        long soon = AddTrack("Comença demà", from: _today.AddDays(1));
        long later = AddTrack("Comença d'aquí a un mes", from: _today.AddDays(30));
        long missing = AddTrack("Sense fitxer", file: FileState.Missing);
        long off = AddTrack("Desactivada", enabled: false);
        long disabledEventOnly = AddTrack("Només en un esdeveniment desactivat", until: _today.AddDays(-5));
        AddTrack("En cap esdeveniment", until: _today.AddDays(-5));
        AddEvent("Tall de les 9", block: false, enabled: true, Track(expired), Track(soon), Track(later), Track(missing), Track(off));
        AddEvent("Bloc de les 10", block: true, enabled: true, Track(expired));
        AddEvent("Desactivat", block: false, enabled: false, Track(disabledEventOnly));

        Assert.Equal(["CADUCADA track Caducada", "NO_SONARA track Desactivada", "NO_SONARA track Sense fitxer", "COMENCA_AVIAT track Comença demà"], Warnings());
        var first = Send(new() { ["cmd"] = "validity.list" })["warnings"]![0]!;
        Assert.True(first["canRemove"]!.GetValue<bool>());
        Assert.Equal(2, first["events"]!.AsArray().Count);
        Assert.Contains("2 esdeveniments", first["text"]!.GetValue<string>());
    }

    [Fact]
    public void Avisa_de_les_campanyes_i_de_les_seves_falques()
    {
        long ok = AddTrack("Falca bona");
        long off = AddTrack("Falca desactivada", enabled: false);
        long shortTrack = AddTrack("Falca curta", until: _today.AddDays(5));
        long finished = AddCampaign("Acabada", _today.AddDays(-20), _today.AddDays(-1), CampaignState.Active, new CampaignSpot { TrackId = ok });
        long draftSoon = AddCampaign("Esborrany aviat", _today.AddDays(3), _today.AddDays(30), CampaignState.Draft, new CampaignSpot { TrackId = ok });
        long draftLater = AddCampaign("Esborrany més endavant", _today.AddDays(20), _today.AddDays(30), CampaignState.Draft, new CampaignSpot { TrackId = ok });
        long tomorrow = AddCampaign("Comença demà", _today.AddDays(1), _today.AddDays(30), CampaignState.Active, new CampaignSpot { TrackId = ok });
        long problems = AddCampaign("Amb problemes", _today, _today.AddDays(30), CampaignState.Active,
            new CampaignSpot { TrackId = ok }, new CampaignSpot { TrackId = off }, new CampaignSpot { TrackId = shortTrack });
        AddEvent("Bloc", block: true, enabled: true,
            CampaignAction(finished), CampaignAction(draftSoon), CampaignAction(draftLater), CampaignAction(tomorrow), CampaignAction(problems));

        Assert.Equal([
            "CADUCADA campaign Acabada",
            "NO_SONARA campaign Amb problemes",
            "NO_SONARA campaign Esborrany aviat",
            "DATES_INCORRECTES campaign Amb problemes",
            "COMENCA_AVIAT campaign Comença demà",
        ], Warnings());
        var texts = Send(new() { ["cmd"] = "validity.list" })["warnings"]!.AsArray().Select(w => w!["text"]!.GetValue<string>()).ToArray();
        Assert.Contains("«Falca desactivada»: està desactivada", texts[1]);
        Assert.Contains("«Falca curta»: les dates de la pista", texts[3]);
    }

    [Fact]
    public void Treu_una_pista_caducada_dels_esdeveniments()
    {
        long expired = AddTrack("Caducada", until: _today.AddDays(-1));
        long other = AddTrack("Una altra");
        long cut = AddEvent("Tall amb més coses", block: false, enabled: true, Track(expired), Track(other));
        long block = AddEvent("Bloc només amb aquesta", block: true, enabled: true, Track(expired));
        long alone = AddEvent("Tall només amb aquesta", block: false, enabled: true, Track(expired));
        long disabled = AddEvent("Desactivat", block: false, enabled: false, Track(expired), Track(other));

        var result = Send(new() { ["cmd"] = "validity.removeFromEvents", ["trackId"] = expired });
        Assert.Equal((1, 1), (result["changed"]!.GetValue<int>(), result["deleted"]!.GetValue<int>()));
        Assert.Equal("Tall només amb aquesta", result["kept"]![0]!["name"]!.GetValue<string>());

        Assert.Equal([other], _events.Get(cut)!.Actions.Select(a => JsonNode.Parse(a.Params)!["songId"]!.GetValue<long>()));
        Assert.Null(_events.Get(block));
        Assert.Single(_events.Get(alone)!.Actions);
        Assert.Equal(2, _events.Get(disabled)!.Actions.Count); // els desactivats no es toquen
        Assert.Equal(["CADUCADA track Caducada"], Warnings()); // queda el que no s'ha pogut treure
    }

    [Fact]
    public void Treu_una_campanya_acabada_dels_esdeveniments()
    {
        long spot = AddTrack("Falca");
        long finished = AddCampaign("Acabada", _today.AddDays(-20), _today.AddDays(-1), CampaignState.Active, new CampaignSpot { TrackId = spot });
        long block = AddEvent("Bloc", block: true, enabled: true, CampaignAction(finished), Track(spot));

        var result = Send(new() { ["cmd"] = "validity.removeFromEvents", ["campaignId"] = finished });
        Assert.Equal(1, result["changed"]!.GetValue<int>());
        Assert.Equal(EventActionType.Track, Assert.Single(_events.Get(block)!.Actions).Type);
        Assert.Empty(Warnings());
    }
}
