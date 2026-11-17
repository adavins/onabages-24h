using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Paquet de campanya (fase 4, pas 7a; D-150): s'exporta d'una emissora a una carpeta compartida, al costat de les falques,
/// i s'importa a una altra, que té la seva pròpia base de dades.
/// </summary>
public sealed class CampaignPackageTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly string _drive;
    private readonly Station _a, _b;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Una emissora: la seva base de dades i el seu motor.</summary>
    private sealed class Station(Database db)
    {
        public Database Db { get; } = db;
        public Library Library { get; } = new(db);
        public Events Events { get; } = new(db);
        public Campaigns Campaigns { get; } = new(db);
        public CommandRouter Router => _router ??= new CommandRouter(new EngineState(), () => { }, Library, events: Events, campaigns: Campaigns);
        private CommandRouter? _router;

        public JsonObject Send(JsonObject msg)
        {
            msg["v"] = 1;
            msg["id"] = 1;
            return Router.Handle(msg.ToJsonString());
        }

        public JsonNode Ok(JsonObject msg)
        {
            var reply = Send(msg);
            Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
            return reply["result"]!;
        }
    }

    public CampaignPackageTests()
    {
        _drive = Path.Combine(_temp.Path, "Drive", "Campanyes");
        Directory.CreateDirectory(_drive);
        _a = new Station(Database.Open(_temp.File("emissora-a.db")));
        _b = new Station(Database.Open(_temp.File("emissora-b.db")));
    }

    public void Dispose()
    {
        _a.Db.Dispose();
        _b.Db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Un fitxer de falca (amb contingut, perquè es pugui llegir) i la seva pista a l'emissora A.</summary>
    private long Spot(string folder, string file, string title)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, file);
        File.WriteAllBytes(path, new byte[100_000]);
        return _a.Library.AddTrack(new Track
        {
            Path = path, Type = TrackType.Commercial, Artist = "Ferreteria", Title = title,
            DurationMs = 20_000, StartMs = 100, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
        });
    }

    /// <summary>A l'emissora A: una campanya de dues falques, programada dilluns i dimarts a les 9 (un tall que hi ha).</summary>
    private long CampaignInA(params long[] spots)
    {
        long id = _a.Campaigns.Save(new Campaign
        {
            Name = "Ferreteria Puig", Advertiser = "Ferreteria Puig", StartDate = _today, EndDate = _today.AddDays(30),
            State = CampaignState.Active, Selection = CampaignSelection.Alternate, ContractedTotal = 40,
            Spots = spots.Select(t => new CampaignSpot { TrackId = t }).ToList(),
        }, DateTimeOffset.UtcNow);
        _a.Events.Save(new ScheduledEvent { Name = "Tall de les 9", Mode = EventMode.Cut, StartTime = new TimeOnly(9, 0), Enabled = true,
            Actions = [new EventAction { Type = EventActionType.Track, Params = new JsonObject { ["songId"] = Spot(_temp.Path, "programa.mp3", "Programa") }.ToJsonString() }] });
        foreach (int day in new[] { 0, 1 })
            _a.Ok(new() { ["cmd"] = "calendar.set", ["campaignId"] = id, ["time"] = "09:00", ["day"] = day, ["marked"] = true });
        return id;
    }

    [Fact]
    public void S_exporta_i_s_importa_en_una_altra_emissora()
    {
        long campaign = CampaignInA(Spot(_drive, "puig-1.mp3", "Puig, versió 1"), Spot(_drive, "puig-2.mp3", "Puig, versió 2"));
        string package = Path.Combine(_drive, "ferreteria-puig.json");

        var exported = _a.Ok(new() { ["cmd"] = "campaigns.exportPackage", ["campaignId"] = campaign, ["path"] = package });
        Assert.Equal((2, 2), (exported["spots"]!.GetValue<int>(), exported["cells"]!.GetValue<int>()));
        Assert.Empty(exported["notInFolder"]!.AsArray());
        Assert.True(File.Exists(package));

        // A l'altra emissora, primer de prova: no canvia res
        var preview = _b.Ok(new() { ["cmd"] = "campaigns.importPackage", ["path"] = package, ["dryRun"] = true });
        Assert.Equal("Ferreteria Puig", preview["campaign"]!["name"]!.GetValue<string>());
        Assert.Equal(["new", "new"], preview["spots"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()));
        Assert.Equal(2, preview["calendar"]!["cells"]!.GetValue<int>());
        Assert.Empty(_b.Campaigns.List());

        // De debò: les pistes apunten als fitxers de la carpeta, la campanya en esborrany i el calendari marcat
        var imported = _b.Ok(new() { ["cmd"] = "campaigns.importPackage", ["path"] = package });
        var created = _b.Campaigns.Get(imported["campaignId"]!.GetValue<long>())!;
        Assert.Equal((CampaignState.Draft, 40, 2), (created.State, created.ContractedTotal, created.Spots.Count));
        var track = _b.Library.GetTrack(created.Spots[0].TrackId)!;
        Assert.Equal((Path.Combine(_drive, "puig-1.mp3"), "Puig, versió 1", 19_500L), (track.Path, track.Title, track.MixMs));
        Assert.Equal(2, imported["applied"]!["marked"]!.GetValue<int>());
        // A l'emissora B no hi ha cap tall a les 9: s'ha creat el bloc de falques d'aquella hora (D-105)
        var block = Assert.Single(_b.Events.List());
        Assert.True(block.CommercialBlock);
        Assert.Equal(new TimeOnly(9, 0), block.StartTime);
        Assert.Equal(["09:00:00/0", "09:00:00/1"], TrackCalendar.Build(_b.Events.List(), CalendarSubject.ForCampaign(created.Id), _today)
            .SelectMany(r => Enumerable.Range(0, 7).Where(d => r.Days[d]?.Marked == true).Select(d => $"{r.Time:HH:mm:ss}/{d}")));

        // Una segona importació: les pistes ja hi són i es reaprofiten; la campanya, sempre nova
        var again = _b.Ok(new() { ["cmd"] = "campaigns.importPackage", ["path"] = package, ["dryRun"] = true });
        Assert.Equal(["existing", "existing"], again["spots"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()));
    }

    [Fact]
    public void La_mateixa_falca_en_dos_periodes_s_exporta_dues_vegades_i_s_importa_com_una_sola_pista()
    {
        long puig = Spot(_drive, "puig-1.mp3", "Puig");
        long other = Spot(_drive, "puig-2.mp3", "Puig, l'altra");
        long campaign = _a.Campaigns.Save(new Campaign
        {
            Name = "Ferreteria Puig", StartDate = _today, EndDate = _today.AddDays(30), State = CampaignState.Active,
            Spots = [new CampaignSpot { TrackId = puig, EndDate = _today.AddDays(9) }, new CampaignSpot { TrackId = other },
                new CampaignSpot { TrackId = puig, StartDate = _today.AddDays(20) }],
        }, DateTimeOffset.UtcNow);
        string package = Path.Combine(_drive, "ferreteria-puig.json");

        var exported = _a.Ok(new() { ["cmd"] = "campaigns.exportPackage", ["campaignId"] = campaign, ["path"] = package });
        Assert.Equal(3, exported["spots"]!.GetValue<int>());
        Assert.Empty(exported["notInFolder"]!.AsArray());

        var imported = _b.Ok(new() { ["cmd"] = "campaigns.importPackage", ["path"] = package });
        var created = _b.Campaigns.Get(imported["campaignId"]!.GetValue<long>())!;
        Assert.Equal(3, created.Spots.Count);
        Assert.Equal(created.Spots[0].TrackId, created.Spots[2].TrackId);
        Assert.Equal((_today.AddDays(9), _today.AddDays(20)), (created.Spots[0].EndDate, created.Spots[2].StartDate));
        Assert.Equal(2, _b.Library.CountTracks()); // la falca repetida és una sola pista
    }

    [Fact]
    public void Una_falca_que_no_es_a_la_carpeta_es_diu_en_exportar_i_no_entra_en_importar()
    {
        long campaign = CampaignInA(Spot(_drive, "puig-1.mp3", "Al Drive"), Spot(Path.Combine(_temp.Path, "local"), "puig-local.mp3", "Només a l'ordinador"));
        string package = Path.Combine(_drive, "ferreteria-puig.json");

        var exported = _a.Ok(new() { ["cmd"] = "campaigns.exportPackage", ["campaignId"] = campaign, ["path"] = package });
        Assert.Equal(["puig-local.mp3"], exported["notInFolder"]!.AsArray().Select(f => f!.GetValue<string>()));

        var imported = _b.Ok(new() { ["cmd"] = "campaigns.importPackage", ["path"] = package });
        Assert.Equal(["new", "missing"], imported["spots"]!.AsArray().Select(s => s!["status"]!.GetValue<string>()));
        Assert.Single(_b.Campaigns.Get(imported["campaignId"]!.GetValue<long>())!.Spots);
    }

    [Fact]
    public void Dues_falques_amb_el_mateix_nom_de_fitxer_no_es_poden_exportar()
    {
        long campaign = CampaignInA(Spot(_drive, "falca.mp3", "A"), Spot(Path.Combine(_temp.Path, "altra"), "falca.mp3", "B"));
        var reply = _a.Send(new() { ["cmd"] = "campaigns.exportPackage", ["campaignId"] = campaign, ["path"] = Path.Combine(_drive, "p.json") });
        Assert.Equal("bad_params", reply["error"]!["code"]!.GetValue<string>());
        Assert.Contains("falca.mp3", reply["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Un_fitxer_que_no_es_un_paquet_es_rebutja()
    {
        string other = Path.Combine(_drive, "una-altra-cosa.json");
        File.WriteAllText(other, """{ "hola": 1 }""");
        var reply = _b.Send(new() { ["cmd"] = "campaigns.importPackage", ["path"] = other, ["dryRun"] = true });
        Assert.Equal("bad_params", reply["error"]!["code"]!.GetValue<string>());
        Assert.Empty(_b.Campaigns.List());
    }
}
