using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Campanyes de falques: dades i ordres (fase 4, pas 3a; D-011, D-029, D-122).</summary>
public sealed class CampaignsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly CommandRouter _router;
    private readonly long _a, _b, _c;
    private int _next;
    private int _changed;

    public CampaignsTests()
    {
        _db = Database.Open(_temp.File("campanyes.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns);
        _router.CampaignsChanged += () => _changed++;
        _a = Spot("Falca A");
        _b = Spot("Falca B");
        _c = Spot("Falca C (Black Friday)");
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
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

    /// <summary>L'exemple de docs/planificador.md (secció 10): A i B tot el mes, C del 24 al 28 amb pes 2.</summary>
    private JsonObject Ferreteria() => new()
    {
        ["cmd"] = "campaigns.save",
        ["name"] = "Ferreteria",
        ["advertiser"] = "Ferreteria Santpedor",
        ["startDate"] = "2026-11-01",
        ["endDate"] = "2026-11-30",
        ["selection"] = "PES",
        ["state"] = "ACTIVA",
        ["contractedTotal"] = 66,
        ["spots"] = new JsonArray(
            new JsonObject { ["trackId"] = _a },
            new JsonObject { ["trackId"] = _b, ["daysMask"] = 0b0011111 },
            new JsonObject { ["trackId"] = _c, ["startDate"] = "2026-11-24", ["endDate"] = "2026-11-28", ["weight"] = 2 }),
    };

    [Fact]
    public void Es_desa_i_es_llegeix_sencera_amb_les_falques_en_ordre()
    {
        var saved = Send(Ferreteria());
        Assert.True(saved["ok"]!.GetValue<bool>(), saved.ToJsonString());
        var c = saved["result"]!["campaign"]!;
        Assert.Equal("Ferreteria", c["name"]!.GetValue<string>());
        Assert.Equal("PES", c["selection"]!.GetValue<string>());
        Assert.Equal(66, c["contractedTotal"]!.GetValue<int>());
        Assert.Null(c["contractedPerDay"]);
        Assert.Equal(1, _changed);

        var spots = c["spots"]!.AsArray();
        Assert.Equal([_a, _b, _c], spots.Select(s => s!["trackId"]!.GetValue<long>()));
        Assert.Equal(127, spots[0]!["daysMask"]!.GetValue<int>());
        Assert.Null(spots[0]!["startDate"]);
        Assert.Equal(0b0011111, spots[1]!["daysMask"]!.GetValue<int>());
        Assert.Equal("2026-11-24", spots[2]!["startDate"]!.GetValue<string>());
        Assert.Equal(2, spots[2]!["weight"]!.GetValue<int>());
        Assert.Equal("Falca C (Black Friday)", spots[2]!["track"]!["title"]!.GetValue<string>());
        Assert.Equal(0, c["playCount"]!.GetValue<int>());
        Assert.Empty(c["events"]!.AsArray());

        long id = c["id"]!.GetValue<long>();
        var got = Send(new() { ["cmd"] = "campaigns.get", ["campaignId"] = id })["result"]!["campaign"]!;
        Assert.Equal(3, got["spots"]!.AsArray().Count);
        var list = Send(new() { ["cmd"] = "campaigns.list" })["result"]!["campaigns"]!.AsArray();
        Assert.Single(list);
        Assert.Null(list[0]!["spots"]![0]!["track"]); // la llista no porta la fitxa de cada pista
    }

    [Fact]
    public void Desar_amb_id_la_substitueix_i_pot_canviar_l_ordre_i_treure_falques()
    {
        long id = Send(Ferreteria())["result"]!["campaign"]!["id"]!.GetValue<long>();
        var edit = Ferreteria();
        edit["campaignId"] = id;
        edit["name"] = "Ferreteria (novembre)";
        edit["spots"] = new JsonArray(new JsonObject { ["trackId"] = _b }, new JsonObject { ["trackId"] = _a });
        var c = Send(edit)["result"]!["campaign"]!;

        Assert.Equal(id, c["id"]!.GetValue<long>());
        Assert.Equal("Ferreteria (novembre)", c["name"]!.GetValue<string>());
        Assert.Equal([_b, _a], c["spots"]!.AsArray().Select(s => s!["trackId"]!.GetValue<long>()));
        Assert.Single(_campaigns.List());
    }

    [Theory]
    [InlineData("endDate", "2026-10-31", "anterior a la d'inici")]
    [InlineData("name", "  ", "Falta el nom")]
    [InlineData("selection", "RANDOM", "ALTERNA, PES o SEQUENCIA")]
    [InlineData("state", "PAUSADA", "ESBORRANY, ACTIVA o ACABADA")]
    [InlineData("startDate", "1/11/2026", "AAAA-MM-DD")]
    public void Les_dades_incorrectes_es_rebutgen_amb_el_motiu(string field, string value, string reason)
    {
        var msg = Ferreteria();
        msg[field] = value;
        var r = Send(msg);
        Assert.False(r["ok"]!.GetValue<bool>());
        Assert.Equal("bad_params", r["error"]!["code"]!.GetValue<string>());
        Assert.Contains(reason, r["error"]!["message"]!.GetValue<string>());
        Assert.Empty(_campaigns.List());
        Assert.Equal(0, _changed);
    }

    [Fact]
    public void Les_falques_han_de_ser_dins_de_les_dates_de_la_campanya_i_valides()
    {
        string Error(JsonObject spot)
        {
            var msg = Ferreteria();
            msg["spots"] = new JsonArray(spot);
            return Send(msg)["error"]?["message"]?.GetValue<string>() ?? "";
        }
        Assert.Contains("dins de les de la campanya", Error(new JsonObject { ["trackId"] = _a, ["endDate"] = "2026-12-05" }));
        Assert.Contains("dins de les de la campanya", Error(new JsonObject { ["trackId"] = _a, ["startDate"] = "2026-10-20" }));
        Assert.Contains("anterior a la d'inici", Error(new JsonObject { ["trackId"] = _a, ["startDate"] = "2026-11-20", ["endDate"] = "2026-11-10" }));
        Assert.Contains("cap dia", Error(new JsonObject { ["trackId"] = _a, ["daysMask"] = 0 }));
        Assert.Contains("el pes", Error(new JsonObject { ["trackId"] = _a, ["weight"] = 0 }));
        Assert.Contains("No existeix la pista 999", Error(new JsonObject { ["trackId"] = 999 }));

        var twice = Ferreteria();
        twice["spots"] = new JsonArray(new JsonObject { ["trackId"] = _a }, new JsonObject { ["trackId"] = _a });
        Assert.Contains("repetida", Send(twice)["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Una_activa_amb_la_data_de_fi_passada_compta_com_a_acabada()
    {
        var msg = Ferreteria();
        msg["startDate"] = "2020-01-01";
        msg["endDate"] = "2020-01-31";
        msg["spots"] = new JsonArray();
        var c = Send(msg)["result"]!["campaign"]!;
        Assert.Equal("ACTIVA", c["state"]!.GetValue<string>());
        Assert.Equal("ACABADA", c["effectiveState"]!.GetValue<string>());

        var future = Ferreteria();
        future["startDate"] = "2099-01-01";
        future["endDate"] = "2099-01-31";
        future["spots"] = new JsonArray();
        Assert.Equal("ACTIVA", Send(future)["result"]!["campaign"]!["effectiveState"]!.GetValue<string>());
    }

    [Fact]
    public void Una_falca_d_una_campanya_no_es_pot_esborrar_ni_es_treu_en_buidar_la_subcategoria()
    {
        Send(Ferreteria());
        var error = Assert.Throws<DatabaseException>(() => _library.DeleteTrack(_a));
        Assert.Contains("campanya «Ferreteria»", error.Message);
        Assert.True(_library.IsTrackInUse(_a));

        long free = Spot("Falca lliure");
        Assert.False(_library.IsTrackInUse(free));
        _library.DeleteTrack(free);

        // Esborrar en bloc: les falques es deixen i es diu per què
        var bulk = Send(new() { ["cmd"] = "library.bulk", ["action"] = "delete", ["trackIds"] = new JsonArray(_a, _b) })["result"]!;
        Assert.Equal(0, bulk["deleted"]!.GetValue<int>());
        Assert.Contains("campanya Ferreteria", bulk["kept"]![0]!["events"]!.GetValue<string>());
    }

    [Fact]
    public void No_es_pot_esborrar_si_un_esdeveniment_la_fa_sonar_o_si_ja_ha_sonat()
    {
        long id = Send(Ferreteria())["result"]!["campaign"]!["id"]!.GetValue<long>();
        long ev = _events.Save(new ScheduledEvent { Name = "Falques 8:30", Mode = EventMode.Queue, StartTime = new TimeOnly(8, 30),
            Actions = [new EventAction { Type = EventActionType.Campaign, Params = $$"""{"campanyaId":{{id}}}""" }] });

        var r = Send(new() { ["cmd"] = "campaigns.delete", ["campaignId"] = id });
        Assert.Contains("«Falques 8:30»", r["error"]!["message"]!.GetValue<string>());
        Assert.Equal("Falques 8:30", Send(new() { ["cmd"] = "campaigns.get", ["campaignId"] = id })["result"]!["campaign"]!["events"]![0]!["name"]!.GetValue<string>());

        _events.Delete(ev);
        lock (_db.Sync)
        {
            using var insert = _db.Command(
                "INSERT INTO historial (pista_id, inici, origen, artista, titol, ruta, campanya_id) " +
                "VALUES ($pista, '2026-11-02T07:30:00.000Z', 'ESDEVENIMENT', 'Ferreteria', 'Falca A', 'f1.mp3', $campanya);");
            insert.Parameters.AddWithValue("$pista", _a);
            insert.Parameters.AddWithValue("$campanya", id);
            insert.ExecuteNonQuery();
        }
        r = Send(new() { ["cmd"] = "campaigns.delete", ["campaignId"] = id });
        Assert.Contains("ja ha sonat 1 vegada", r["error"]!["message"]!.GetValue<string>());
        Assert.Contains("acabada", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Una_campanya_que_no_s_ha_fet_servir_s_esborra_amb_les_falques()
    {
        long id = Send(Ferreteria())["result"]!["campaign"]!["id"]!.GetValue<long>();
        var r = Send(new() { ["cmd"] = "campaigns.delete", ["campaignId"] = id });
        Assert.True(r["ok"]!.GetValue<bool>());
        Assert.Empty(_campaigns.List());
        Assert.False(_library.IsTrackInUse(_a)); // ja es pot esborrar
        Assert.Contains("No existeix", Send(new() { ["cmd"] = "campaigns.get", ["campaignId"] = id })["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Un_esdeveniment_amb_l_accio_CAMPANYA_la_descriu_i_rebutja_una_que_no_existeix()
    {
        long id = Send(Ferreteria())["result"]!["campaign"]!["id"]!.GetValue<long>();
        JsonObject Save(long campaignId) => Send(new()
        {
            ["cmd"] = "scheduler.save",
            ["event"] = new JsonObject
            {
                ["name"] = "Falques 8:30", ["mode"] = "ENCUA", ["startTime"] = "08:30",
                ["actions"] = new JsonArray(new JsonObject { ["type"] = "CAMPANYA", ["params"] = new JsonObject { ["campanyaId"] = campaignId } }),
            },
        });

        var bad = Save(999);
        Assert.Equal("bad_params", bad["error"]!["code"]!.GetValue<string>());
        Assert.Contains("La campanya 999 no existeix", bad["error"]!["message"]!.GetValue<string>());

        var action = Save(id)["result"]!["event"]!["actions"]![0]!;
        Assert.Equal("Campanya «Ferreteria»", action["label"]!.GetValue<string>());
        Assert.Contains("3 falques", action["detail"]!.GetValue<string>());
        Assert.Contains("del 1/11/2026 al 30/11/2026", action["detail"]!.GetValue<string>());
        Assert.False(action["missing"]!.GetValue<bool>() && DateOnly.FromDateTime(DateTime.Now) <= new DateOnly(2026, 11, 30));
    }
}
