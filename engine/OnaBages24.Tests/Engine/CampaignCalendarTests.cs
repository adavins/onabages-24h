using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Calendari d'una campanya (fase 4, pas 3d; D-126): el calendari de les pistes amb l'acció CAMPANYA.</summary>
public sealed class CampaignCalendarTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly CommandRouter _router;
    private readonly long _spot, _program, _campaign;
    private readonly DateOnly _start = DateOnly.FromDateTime(DateTime.Today);
    private int _next;

    public CampaignCalendarTests()
    {
        _db = Database.Open(_temp.File("calendari-campanya.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns);
        _spot = AddTrack("Falca A", TrackType.Commercial);
        _program = AddTrack("Programa", TrackType.Program);
        _campaign = _campaigns.Save(new Campaign
        {
            Name = "Ferreteria", StartDate = _start, EndDate = _start.AddDays(27), State = CampaignState.Active,
            Spots = [new CampaignSpot { TrackId = _spot }],
        }, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private long AddTrack(string title, TrackType type) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), Type = type, Artist = "Prova", Title = title,
        DurationMs = 20_000, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
    });

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private JsonObject Set(string time, int day, bool marked) =>
        Send(new() { ["cmd"] = "calendar.set", ["campaignId"] = _campaign, ["time"] = time, ["day"] = day, ["marked"] = marked });

    /// <summary>Un programa de dilluns a divendres a les 9, amb una pista de tipus Programa.</summary>
    private ScheduledEvent Program9()
    {
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Programa del matí", Mode = EventMode.Cut, StartTime = new TimeOnly(9, 0), DaysMask = Weekdays.MondayToFriday,
            Enabled = true,
            Actions = [new EventAction { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = _program }.ToJsonString() }],
        });
        return _events.Get(id)!;
    }

    [Fact]
    public void Marcar_posa_l_accio_CAMPANYA_aquell_dia_abans_del_programa_i_els_buits_van_a_un_bloc()
    {
        var ev = Program9();

        // Una casella amb esdeveniment: la campanya hi entra només el dimecres, abans del programa
        var result = Set("09:00", 2, true)["result"]!;
        Assert.Equal("Ferreteria", result["campaign"]!["name"]!.GetValue<string>());
        Assert.Null(result["track"]);
        Assert.True(result["rows"]![0]!["days"]![2]!["marked"]!.GetValue<bool>());
        Assert.False(result["rows"]![0]!["days"]![1]!["marked"]!.GetValue<bool>());
        var actions = _events.Get(ev.Id)!.Actions;
        Assert.Equal([EventActionType.Campaign, EventActionType.Track], actions.Select(a => a.Type));
        Assert.Equal(_campaign, CampaignPlanner.CampaignIdOf(actions[0]));
        Assert.Equal(1 << 2, actions[0].DaysMask);

        // Una casella buida: un bloc de falques d'aquella hora, amb la campanya, només aquell dia
        Set("17:20", 5, true);
        var block = Assert.Single(_events.List(), e => e.CommercialBlock);
        Assert.Equal((EventMode.Queue, 1 << 5, new TimeOnly(17, 20)), (block.Mode, block.DaysMask, block.StartTime));
        Assert.True(CampaignPlanner.IsCampaignAction(Assert.Single(block.Actions), _campaign));

        // Desmarcar-lo deixa el bloc buit, que s'esborra
        Set("17:20", 5, false);
        Assert.DoesNotContain(_events.List(), e => e.CommercialBlock);

        // Desmarcar el programa en treu l'acció i deixa el programa com era
        Set("09:00", 2, false);
        Assert.Equal([EventActionType.Track], _events.Get(ev.Id)!.Actions.Select(a => a.Type));
    }

    [Fact]
    public void Els_recomptes_son_dins_de_les_dates_de_la_campanya_i_un_per_emissio()
    {
        Set("10:50", 0, true); // dilluns
        Set("12:05", 1, true); // dimarts
        var block = _events.List().Single(e => e.StartTime == new TimeOnly(10, 50));
        // La mateixa campanya dues vegades al mateix bloc (p. ex. des del Planificador): només compta (i sona) un cop
        _events.Save(block with { Actions = [.. block.Actions, block.Actions[0]] });

        var counts = Send(new() { ["cmd"] = "calendar.get", ["campaignId"] = _campaign })["result"]!["counts"]!;
        Assert.Equal(_start.ToString("yyyy-MM-dd"), counts["from"]!.GetValue<string>());
        Assert.Equal(_start.AddDays(27).ToString("yyyy-MM-dd"), counts["to"]!.GetValue<string>());
        Assert.True(counts["periodFromTrack"]!.GetValue<bool>());
        // 28 dies: 4 dilluns i 4 dimarts
        Assert.Equal(8, counts["total"]!.GetValue<int>());
        Assert.Equal([1, 1, 0, 0, 0, 0, 0], counts["perWeekday"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public void Una_pista_i_una_campanya_comparteixen_el_bloc_i_es_poden_moure()
    {
        Send(new() { ["cmd"] = "calendar.set", ["trackId"] = _spot, ["time"] = "10:50", ["day"] = 0, ["marked"] = true });
        Set("10:50", 0, true);
        var block = Assert.Single(_events.List(), e => e.CommercialBlock); // un sol bloc per hora (D-105)
        Assert.Equal([EventActionType.Track, EventActionType.Campaign], block.Actions.Select(a => a.Type));

        // El contingut marca la campanya; moure-la amunt la posa davant de la pista
        var content = Send(new() { ["cmd"] = "calendar.content", ["eventId"] = block.Id, ["day"] = 0, ["campaignId"] = _campaign })["result"]!;
        Assert.Equal([false, true], content["items"]!.AsArray().Select(i => i!["isTrack"]!.GetValue<bool>()));
        var moved = Send(new() { ["cmd"] = "calendar.move", ["eventId"] = block.Id, ["day"] = 0, ["campaignId"] = _campaign, ["delta"] = -1 })["result"]!;
        Assert.Equal([true, false], moved["items"]!.AsArray().Select(i => i!["isTrack"]!.GetValue<bool>()));
        Assert.Equal([EventActionType.Campaign, EventActionType.Track], _events.Get(block.Id)!.Actions.Select(a => a.Type));

        // El calendari de la pista no veu la campanya com a seva
        var trackRows = Send(new() { ["cmd"] = "calendar.get", ["trackId"] = _spot })["result"]!["rows"]!;
        Assert.True(trackRows[0]!["days"]![0]!["marked"]!.GetValue<bool>());
        Set("10:50", 0, false);
        Assert.Equal([EventActionType.Track], _events.Get(block.Id)!.Actions.Select(a => a.Type)); // el bloc continua amb la pista
    }

    [Fact]
    public void Una_campanya_que_no_existeix_es_rebutja()
    {
        var error = Send(new() { ["cmd"] = "calendar.get", ["campaignId"] = 999 })["error"]!;
        Assert.Equal("bad_params", error["code"]!.GetValue<string>());
        Assert.Contains("No existeix la campanya 999", error["message"]!.GetValue<string>());
    }
}
