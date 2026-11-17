using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Agenda de bosses (D-170): el canvi automàtic del dia 1 i les ordres <c>agendas.*</c>.</summary>
public sealed class BagAgendaRunnerTests : IDisposable
{
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly AutoDj _autoDj;
    private readonly BagAgendaRunner _runner;
    private readonly CommandRouter _router;
    private readonly long[] _adult = new long[9];
    private readonly long _bagA, _bagB;
    /// <summary>L'hora del motor (UTC). 30/11/2026 a les 23:59 de Madrid.</summary>
    private DateTimeOffset _now = new(2026, 11, 30, 22, 59, 0, TimeSpan.Zero);
    private int _agendasChanged;

    public BagAgendaRunnerTests()
    {
        _db = Database.Open(_temp.File("agendes.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = 1 });
        _runner = new BagAgendaRunner(new BagAgendas(_db), _autoDj, _rotations, () => _now, Madrid);
        _router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: _autoDj, rotations: _rotations, agendas: _runner);
        _router.AgendasChanged += () => _agendasChanged++;
        long music = _library.EnsureCategory("Music");
        for (int i = 0; i < 9; i++) _adult[i] = _library.EnsureSubcategory(music, $"Adult {i + 1}");
        _bagA = _rotations.CreateBag("Generalista A", _adult[4], "a mà", DateTimeOffset.UtcNow);
        _bagB = _rotations.CreateBag("Generalista B", _adult[5], "a mà", DateTimeOffset.UtcNow);
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

    private JsonObject SaveAdult(string start = "2026-12", bool active = true) => Send(new()
    {
        ["cmd"] = "agendas.save", ["name"] = "Adult", ["startMonth"] = start, ["active"] = active,
        ["bagIds"] = new JsonArray(_bagA, _bagB), ["subcategoryIds"] = new JsonArray(_adult.Select(id => (JsonNode)id).ToArray()),
    });

    private (long? A, long? B) Bags() => (_rotations.GetBag(_bagA)!.SubcategoryId, _rotations.GetBag(_bagB)!.SubcategoryId);

    [Fact]
    public void Canvia_les_bosses_a_les_00_00_del_dia_1_en_hora_local_i_un_sol_cop_al_mes()
    {
        SaveAdult();
        Assert.Equal((_adult[4], _adult[5]), Bags()); // el 30 de novembre a les 23:59 encara no

        _now = new DateTimeOffset(2026, 11, 30, 23, 0, 30, TimeSpan.Zero); // 1/12 a les 00:00:30 de Madrid
        Assert.Equal(2, _runner.ApplyDue());
        Assert.Equal((_adult[0], _adult[1]), Bags());
        Assert.Equal("agenda «Adult»", _rotations.GetBag(_bagA)!.ChangedBy);

        // Un canvi a mà a mig mes no es desfà fins al mes següent
        Send(new() { ["cmd"] = "bags.set", ["bagId"] = _bagA, ["subcategoryId"] = _adult[8] });
        _now = new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, _runner.ApplyDue());
        Assert.Equal(_adult[8], _rotations.GetBag(_bagA)!.SubcategoryId);

        // Gener: només canvia la bossa A (Adult 3); la B es queda amb Adult 2
        _now = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1, _runner.ApplyDue());
        Assert.Equal((_adult[2], _adult[1]), Bags());
    }

    [Fact]
    public void Si_el_motor_estava_aturat_el_dia_1_el_canvi_es_fa_en_arrencar()
    {
        SaveAdult();
        _now = new DateTimeOffset(2027, 2, 3, 9, 0, 0, TimeSpan.Zero); // febrer: tercer mes
        Assert.Equal(2, _runner.ApplyDue());
        Assert.Equal((_adult[2], _adult[3]), Bags());
    }

    [Fact]
    public void En_desar_una_agenda_activa_les_bosses_passen_de_seguida_al_mes_d_ara()
    {
        _now = new DateTimeOffset(2026, 12, 10, 10, 0, 0, TimeSpan.Zero);
        var saved = SaveAdult()["result"]!;
        Assert.Equal(2, saved["changedBags"]!.GetValue<int>());
        Assert.Equal((_adult[0], _adult[1]), Bags());
        Assert.Equal(1, _agendasChanged);
        Assert.Equal("2026-12", saved["agenda"]!["appliedMonth"]!.GetValue<string>());

        // Inactiva: no toca res
        Send(new() { ["cmd"] = "bags.set", ["bagId"] = _bagA, ["subcategoryId"] = _adult[8] });
        long id = saved["agenda"]!["id"]!.GetValue<long>();
        var off = Send(new()
        {
            ["cmd"] = "agendas.save", ["agendaId"] = id, ["name"] = "Adult", ["startMonth"] = "2026-12", ["active"] = false,
            ["bagIds"] = new JsonArray(_bagA, _bagB), ["subcategoryIds"] = new JsonArray(_adult.Select(x => (JsonNode)x).ToArray()),
        })["result"]!;
        Assert.Equal(0, off["changedBags"]!.GetValue<int>());
        Assert.Equal(_adult[8], _rotations.GetBag(_bagA)!.SubcategoryId);
    }

    [Fact]
    public void La_llista_diu_que_toca_aquest_mes_i_el_que_ve()
    {
        _now = new DateTimeOffset(2026, 12, 10, 10, 0, 0, TimeSpan.Zero);
        SaveAdult();
        var list = Send(new() { ["cmd"] = "agendas.list" })["result"]!;
        Assert.Equal("2026-12", list["month"]!.GetValue<string>());
        var agenda = list["agendas"]![0]!;
        Assert.Equal(["Generalista A", "Generalista B"], agenda["bags"]!.AsArray().Select(b => b!["name"]!.GetValue<string>()));
        Assert.Equal("Music / Adult 9", agenda["subcategories"]![8]!["name"]!.GetValue<string>());
        Assert.Equal(["Music / Adult 1", "Music / Adult 2"], agenda["current"]!.AsArray().Select(b => b!["subcategory"]!.GetValue<string>()));
        Assert.Equal(["Music / Adult 3", "Music / Adult 2"], agenda["next"]!.AsArray().Select(b => b!["subcategory"]!.GetValue<string>()));
    }

    [Fact]
    public void La_previsualitzacio_funciona_sense_desar()
    {
        _now = new DateTimeOffset(2027, 6, 10, 10, 0, 0, TimeSpan.Zero);
        var months = Send(new()
        {
            ["cmd"] = "agendas.preview", ["startMonth"] = "2026-12", ["months"] = 4,
            ["bagIds"] = new JsonArray(_bagA, _bagB), ["subcategoryIds"] = new JsonArray(_adult.Select(x => (JsonNode)x).ToArray()),
        })["result"]!["months"]!.AsArray();
        Assert.Equal(["2027-06", "2027-07", "2027-08", "2027-09"], months.Select(m => m!["month"]!.GetValue<string>()));
        // Mes 9 (agost): Adult 9 i Adult 1
        Assert.Equal(["Music / Adult 9", "Music / Adult 1"], months[2]!["bags"]!.AsArray().Select(b => b!["subcategory"]!.GetValue<string>()));
        Assert.Empty(Send(new() { ["cmd"] = "agendas.list" })["result"]!["agendas"]!.AsArray());
    }

    [Fact]
    public void Errors_dels_parametres()
    {
        string Error(JsonObject msg) => Send(msg)["error"]!["message"]!.GetValue<string>();
        Assert.Contains("startMonth", Error(new() { ["cmd"] = "agendas.save", ["name"] = "X", ["startMonth"] = "2026-13", ["active"] = true, ["bagIds"] = new JsonArray(_bagA), ["subcategoryIds"] = new JsonArray(_adult[0]) }));
        Assert.Contains("No existeix la bossa", Error(new() { ["cmd"] = "agendas.save", ["name"] = "X", ["startMonth"] = "2026-12", ["active"] = true, ["bagIds"] = new JsonArray(999L), ["subcategoryIds"] = new JsonArray(_adult[0]) }));
        Assert.Contains("No existeix la subcategoria", Error(new() { ["cmd"] = "agendas.save", ["name"] = "X", ["startMonth"] = "2026-12", ["active"] = true, ["bagIds"] = new JsonArray(_bagA), ["subcategoryIds"] = new JsonArray(999L) }));
        Assert.Contains("com a mínim 2 subcategories", Error(new() { ["cmd"] = "agendas.preview", ["startMonth"] = "2026-12", ["bagIds"] = new JsonArray(_bagA, _bagB), ["subcategoryIds"] = new JsonArray(_adult[0]) }));
        Assert.Contains("months", Error(new() { ["cmd"] = "agendas.preview", ["startMonth"] = "2026-12", ["months"] = 37, ["bagIds"] = new JsonArray(_bagA), ["subcategoryIds"] = new JsonArray(_adult[0]) }));
        Assert.Contains("No existeix l'agenda", Error(new() { ["cmd"] = "agendas.delete", ["agendaId"] = 5 }));
    }

    [Fact]
    public void No_es_poden_esborrar_les_bosses_ni_les_subcategories_d_una_agenda_i_l_agenda_si()
    {
        long id = SaveAdult()["result"]!["agenda"]!["id"]!.GetValue<long>();
        string Error(JsonObject msg) => Send(msg)["error"]!["message"]!.GetValue<string>();
        Assert.Contains("la canvia l'agenda «Adult»", Error(new() { ["cmd"] = "bags.delete", ["bagId"] = _bagB }));
        Assert.Contains("agendes de bosses «Adult»", string.Join(" ", _library.SubcategoryBlockers(_adult[8])));

        Assert.Equal(id, Send(new() { ["cmd"] = "agendas.delete", ["agendaId"] = id })["result"]!["deleted"]!.GetValue<long>());
        Assert.Equal(2, _agendasChanged);
        Assert.Empty(_library.SubcategoryBlockers(_adult[8]));
        Assert.True(Send(new() { ["cmd"] = "bags.delete", ["bagId"] = _bagB })["ok"]!.GetValue<bool>());
    }
}
