using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Dades de l'emissora, informe de programació i certificat d'emissió (fase 4, pas 4a; D-138).</summary>
public sealed class ReportsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly CommandRouter _router;
    private readonly long _a, _b;
    /// <summary>L'1 de novembre del 2026 a les 0:00, hora local (el «ara» de les proves).</summary>
    private DateTimeOffset _now = Local(new DateOnly(2026, 11, 1));
    private int _next;

    public ReportsTests()
    {
        _db = Database.Open(_temp.File("informes.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns) { CampaignClock = () => _now };
        _a = Spot("Falca A");
        _b = Spot("Falca B");
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private static DateTimeOffset Local(DateOnly day, int hour = 0)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private long Spot(string title) => _library.AddTrack(new Track
    {
        Path = _temp.File($"f{++_next}.mp3"), Type = TrackType.Commercial, Artist = "Ferreteria", Title = title,
        DurationMs = 20_000, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
    });

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private Campaign Campaign(CampaignState state) => _campaigns.Get(_campaigns.Save(new Campaign
    {
        Name = "Ferreteria", Advertiser = "Ferreteria Santpedor", StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 30),
        State = state, Spots = [new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b }],
    }, _now))!;

    private void Event(EventActionType type, long id, int days = Weekdays.All) => _events.Save(new ScheduledEvent
    {
        Name = "Falques 10:00", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0), DaysMask = days, Enabled = true,
        Actions = [new EventAction
        {
            Type = type,
            Params = type == EventActionType.Campaign ? $$"""{"campanyaId":{{id}}}""" : $$"""{"songId":{{id}}}""",
        }],
    });

    [Fact]
    public void Les_dades_de_l_emissora_es_desen_i_el_logotip_ha_de_ser_una_imatge()
    {
        var empty = Send(new() { ["cmd"] = "station.get" })["result"]!["station"]!;
        Assert.Equal(("", "", null), (empty["name"]!.GetValue<string>(), empty["frequency"]!.GetValue<string>(), empty["logo"]?.GetValue<string>()));

        const string logo = "data:image/png;base64,iVBORw0KGgo=";
        Send(new() { ["cmd"] = "station.set", ["name"] = " Ona Bages ", ["frequency"] = "90.2 FM", ["logo"] = logo });
        Send(new() { ["cmd"] = "station.set", ["frequency"] = "90.2 MHz" }); // la resta no canvia
        var saved = Send(new() { ["cmd"] = "station.get" })["result"]!["station"]!;
        Assert.Equal(("Ona Bages", "90.2 MHz", logo), (saved["name"]!.GetValue<string>(), saved["frequency"]!.GetValue<string>(), saved["logo"]!.GetValue<string>()));

        var bad = Send(new() { ["cmd"] = "station.set", ["logo"] = "data:text/html;base64,PGI+" })["error"]!;
        Assert.Contains("PNG, JPEG o SVG", bad["message"]!.GetValue<string>());

        Send(new() { ["cmd"] = "station.set", ["logo"] = null }); // treure'l
        Assert.Null(Send(new() { ["cmd"] = "station.get" })["result"]!["station"]!["logo"]);
    }

    [Fact]
    public void L_informe_d_una_pista_compta_els_passis_previstos_dins_de_les_seves_dates()
    {
        _library.UpdateTrack(_library.GetTrack(_a)! with { ValidFrom = new DateOnly(2026, 11, 1), ValidUntil = new DateOnly(2026, 11, 30) });
        Event(EventActionType.Track, _a, Weekdays.MondayToFriday);

        // Sense dates, el període és el de la pista: els 21 dies feiners de novembre del 2026
        var report = Send(new() { ["cmd"] = "reports.programming", ["trackId"] = _a })["result"]!;
        Assert.Equal(("track", "2026-11-01", "2026-11-30", 21), (report["subject"]!["kind"]!.GetValue<string>(),
            report["from"]!.GetValue<string>(), report["to"]!.GetValue<string>(), report["total"]!.GetValue<int>()));
        var pass = report["passes"]![0]!;
        Assert.StartsWith("2026-11-02T10:00:00", pass["at"]!.GetValue<string>());
        Assert.Equal(("Falques 10:00", "Falca A"), (pass["eventName"]!.GetValue<string>(), pass["title"]!.GetValue<string>()));
        var row = Assert.Single(report["grid"]!.AsArray())!;
        Assert.Equal("10:00:00", row["time"]!.GetValue<string>());
        Assert.Equal([true, true, true, true, true, false, false], row["days"]!.AsArray().Select(d => d!.GetValue<bool>()));

        // Un període més curt
        var week = Send(new() { ["cmd"] = "reports.programming", ["trackId"] = _a, ["from"] = "2026-11-09", ["to"] = "2026-11-15" })["result"]!;
        Assert.Equal(5, week["total"]!.GetValue<int>());
    }

    [Fact]
    public void L_informe_d_una_campanya_en_esborrany_ja_reparteix_els_passis_entre_les_falques()
    {
        var c = Campaign(CampaignState.Draft); // encara no té pla desat
        Event(EventActionType.Campaign, c.Id);

        var report = Send(new() { ["cmd"] = "reports.programming", ["campaignId"] = c.Id })["result"]!;
        Assert.Equal(("campaign", "Ferreteria Santpedor", 30), (report["subject"]!["kind"]!.GetValue<string>(),
            report["subject"]!["advertiser"]!.GetValue<string>(), report["total"]!.GetValue<int>()));
        Assert.Equal([15, 15], report["bySpot"]!.AsArray().Select(s => s!["count"]!.GetValue<int>()));
        Assert.Equal(["Falca A", "Falca B"], report["passes"]!.AsArray().Take(2).Select(p => p!["title"]!.GetValue<string>()));
    }

    [Fact]
    public void L_informe_d_una_campanya_ja_començada_ajunta_el_pla_desat_i_el_que_queda()
    {
        var c = Campaign(CampaignState.Active);
        Event(EventActionType.Campaign, c.Id);
        var plans = new CampaignPlans(_campaigns, _events, _library, TimeZoneInfo.Local, () => _now);
        plans.RecalculateAll();

        // Deu dies i mig després: els 11 passis d'abans surten del pla desat; els 19 d'ara endavant, del pla d'ara
        _now = Local(new DateOnly(2026, 11, 11), 12);
        plans.RecalculateAll();
        var report = Send(new() { ["cmd"] = "reports.programming", ["campaignId"] = c.Id })["result"]!;
        Assert.Equal(30, report["total"]!.GetValue<int>());
        Assert.Equal(30, report["passes"]!.AsArray().Select(p => p!["at"]!.GetValue<string>()).Distinct().Count());
    }

    [Fact]
    public void El_certificat_surt_de_l_historial_i_nomes_dins_del_periode()
    {
        var c = Campaign(CampaignState.Active);
        var other = _campaigns.Save(new Campaign
        {
            Name = "Una altra", StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 30), Spots = [new CampaignSpot { TrackId = _a }],
        }, _now);
        var a = _library.GetTrack(_a)!;
        var b = _library.GetTrack(_b)!;
        _library.AddHistory(a, Local(new DateOnly(2026, 11, 2), 10), QueueOrigin.Event, c.Id);
        long second = _library.AddHistory(b, Local(new DateOnly(2026, 11, 3), 10), QueueOrigin.Event, c.Id);
        _library.FinishHistory(second, TimeSpan.FromSeconds(12), complete: false);
        _library.AddHistory(a, Local(new DateOnly(2026, 12, 1), 10), QueueOrigin.Event, c.Id); // fora del període
        _library.AddHistory(a, Local(new DateOnly(2026, 11, 4), 10), QueueOrigin.Event, other); // d'una altra campanya
        _library.AddHistory(a, Local(new DateOnly(2026, 11, 5), 10), QueueOrigin.Manual);       // a mà, sense campanya

        var certificate = Send(new() { ["cmd"] = "reports.certificate", ["campaignId"] = c.Id })["result"]!;
        Assert.Equal(2, certificate["total"]!.GetValue<int>());
        var emissions = certificate["emissions"]!.AsArray();
        Assert.Equal(2, emissions.Count); // el nombre de passis és el de la llista
        Assert.Equal(["Falca A", "Falca B"], emissions.Select(e => e!["title"]!.GetValue<string>()));
        Assert.False(emissions[1]!["complete"]!.GetValue<bool>());
        Assert.Equal([1, 1], certificate["bySpot"]!.AsArray().Select(s => s!["count"]!.GetValue<int>()));

        // El d'una pista compta totes les seves emissions del període, siguin de la campanya que siguin
        var track = Send(new() { ["cmd"] = "reports.certificate", ["trackId"] = _a, ["from"] = "2026-11-01", ["to"] = "2026-11-30" })["result"]!;
        Assert.Equal(3, track["total"]!.GetValue<int>());
    }

    [Fact]
    public void Un_periode_incorrecte_es_rebutja()
    {
        var c = Campaign(CampaignState.Active);
        string Error(JsonObject msg) => Send(msg)["error"]!["message"]!.GetValue<string>();
        Assert.Contains("abans de començar", Error(new() { ["cmd"] = "reports.certificate", ["campaignId"] = c.Id, ["from"] = "2026-11-10", ["to"] = "2026-11-01" }));
        Assert.Contains("400 dies", Error(new() { ["cmd"] = "reports.programming", ["campaignId"] = c.Id, ["from"] = "2026-01-01", ["to"] = "2027-12-31" }));
        Assert.Contains("data d'inici", Error(new() { ["cmd"] = "reports.programming", ["trackId"] = _b })); // pista sense dates
    }
}
