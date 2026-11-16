using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Incidències (fase 5, pas 1; D-156): desar-les, obrir-les i tancar-les, què ho és i incidents.list.</summary>
public sealed class IncidentsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly IncidentStore _store;
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    public IncidentsTests()
    {
        _db = Database.Open(_temp.File("emissio.db"));
        _store = new IncidentStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private IncidentTracker Tracker() => new(_store, NullLogger.Instance, () => _now);

    private IReadOnlyList<Incident> All() => _store.List(new IncidentFilter { Limit = 500 }).Incidents;

    [Fact]
    public void Una_incidencia_que_dura_s_obre_un_cop_s_actualitza_i_es_tanca()
    {
        var tracker = Tracker();
        var changes = new List<Incident>();
        tracker.Changed += changes.Add;

        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "S'emet per la sortida d'emergència");
        _now = _now.AddSeconds(2);
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "S'emet per la sortida d'emergència"); // igual: res
        Assert.Single(changes);
        Assert.Single(tracker.Open);
        Assert.True(All().Single().IsOpen);

        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "No hi ha cap sortida");
        Assert.Equal("No hi ha cap sortida", All().Single().Text);

        _now = _now.AddMinutes(5);
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, null);
        Assert.Empty(tracker.Open);
        var closed = All().Single();
        Assert.Equal((_now, false), (closed.EndedAt, closed.Recovered));
        Assert.Equal(3, changes.Count);
        Assert.False(changes[^1].IsOpen);

        // Si torna a passar, és una incidència nova
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Torna-hi");
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public void Les_d_un_tipus_alhora_tanquen_les_que_ja_no_hi_son()
    {
        var tracker = Tracker();
        tracker.SetAll(IncidentKind.Folder, IncidentSeverity.Warning, new Dictionary<string, string> { ["carpeta:1"] = "A", ["carpeta:2"] = "B" });
        tracker.Set(IncidentKind.AutoDj, "autodj", IncidentSeverity.Warning, "Sense pistes");
        Assert.Equal(3, tracker.Open.Count);

        tracker.SetAll(IncidentKind.Folder, IncidentSeverity.Warning, new Dictionary<string, string> { ["carpeta:2"] = "B" });
        Assert.Equal(["B", "Sense pistes"], tracker.Open.Select(i => i.Text).Order().ToArray());
        Assert.Equal("A", All().Single(i => !i.IsOpen).Text);
    }

    [Fact]
    public void Les_obertes_surten_primer_les_mes_greus()
    {
        var tracker = Tracker();
        tracker.Set(IncidentKind.AutoDj, "autodj", IncidentSeverity.Warning, "Avís");
        _now = _now.AddSeconds(1);
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Greu");
        Assert.Equal(["Greu", "Avís"], tracker.Open.Select(i => i.Text).ToArray());
    }

    [Fact]
    public void Una_d_un_moment_es_desa_ja_tancada_i_no_surt_a_les_obertes()
    {
        var tracker = Tracker();
        Incident? notified = null;
        tracker.Changed += i => notified = i;

        tracker.Report(IncidentKind.File, "fitxer:x.mp3", IncidentSeverity.Warning, "No s'ha pogut fer sonar", @"D:\x.mp3", recovered: true);

        Assert.Empty(tracker.Open);
        var saved = All().Single();
        Assert.Equal((false, true, _now, @"D:\x.mp3"), (saved.IsOpen, saved.Recovered, saved.StartedAt, saved.Detail));
        Assert.Equal(saved.Id, notified!.Id);
    }

    [Fact]
    public void En_arrencar_es_continuen_les_obertes_de_l_execucio_anterior_fins_que_es_resolen()
    {
        var started = _now;
        Tracker().Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Emergència");
        _now = _now.AddHours(1);

        var tracker = Tracker(); // el motor torna a arrencar
        Assert.Equal(started, tracker.Open.Single().StartedAt);
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Emergència"); // continua igual: no se n'obre cap altra
        Assert.Single(All());

        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, null);
        Assert.Equal((started, (DateTimeOffset?)_now), (All().Single().StartedAt, All().Single().EndedAt));
    }

    [Fact]
    public void La_llista_filtra_per_estat_tipus_i_dates_i_va_per_pagines()
    {
        var tracker = Tracker();
        for (int i = 0; i < 5; i++)
        {
            tracker.Report(IncidentKind.File, $"fitxer:{i}", IncidentSeverity.Warning, $"Fitxer {i}");
            _now = _now.AddDays(1);
        }
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Oberta");

        var (page, total) = _store.List(new IncidentFilter { Limit = 2, Offset = 1 });
        Assert.Equal(6, total);
        Assert.Equal(["Fitxer 4", "Fitxer 3"], page.Select(i => i.Text).ToArray());

        Assert.Equal("Oberta", _store.List(new IncidentFilter { Open = true }).Incidents.Single().Text);
        Assert.Equal(5, _store.List(new IncidentFilter { Open = false }).Total);
        Assert.Equal(5, _store.List(new IncidentFilter { Kinds = [IncidentKind.File] }).Total);
        var start = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(["Fitxer 2", "Fitxer 1"],
            _store.List(new IncidentFilter { From = start, To = start.AddDays(2) }).Incidents.Select(i => i.Text).ToArray());
    }

    [Fact]
    public void Es_conserven_un_any_les_resoltes_i_sempre_les_obertes()
    {
        var tracker = Tracker();
        tracker.Report(IncidentKind.File, "fitxer:vell", IncidentSeverity.Warning, "Vella");
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Oberta fa temps");
        _now = _now.AddYears(1).AddDays(1);
        tracker.Report(IncidentKind.File, "fitxer:nou", IncidentSeverity.Warning, "Nova");

        tracker.Purge();

        Assert.Equal(["Nova", "Oberta fa temps"], All().Select(i => i.Text).ToArray());
    }

    private static ExecutionRecord Record(ExecutionResult result, double? delayMs = null) => new()
    {
        EventId = 7,
        Name = "Hora en punt",
        ScheduledFor = new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero),
        Result = result,
        Detail = "motiu",
        StartDelayMs = delayMs,
    };

    [Fact]
    public void Del_registre_del_planificador_nomes_son_incidencies_els_omesos_tard_els_errors_i_els_que_comencen_tard()
    {
        Assert.Equal(IncidentKind.EventSkipped, IncidentRules.FromExecution(Record(ExecutionResult.SkippedLate))?.Kind);
        var error = IncidentRules.FromExecution(Record(ExecutionResult.Error));
        Assert.Equal((IncidentKind.EventError, IncidentSeverity.Danger), (error?.Kind, error?.Severity));
        Assert.Contains("«Hora en punt»", error?.Text);
        Assert.Contains("motiu", error?.Text);

        var late = IncidentRules.FromExecution(Record(ExecutionResult.Ok, 2500));
        Assert.Equal(IncidentKind.EventLate, late?.Kind);
        Assert.Null(IncidentRules.FromExecution(Record(ExecutionResult.Ok, 1999)));
        Assert.Null(IncidentRules.FromExecution(Record(ExecutionResult.Ok)));

        // Els omesos volguts no ho són
        Assert.Null(IncidentRules.FromExecution(Record(ExecutionResult.SkippedConflict)));
        Assert.Null(IncidentRules.FromExecution(Record(ExecutionResult.SkippedClockChange)));
        Assert.Null(IncidentRules.FromExecution(Record(ExecutionResult.NotPlayed)));
    }

    [Fact]
    public void Les_carpetes_vigilades_amb_problemes_o_fitxers_que_falten()
    {
        var texts = IncidentRules.Folders([
            new FolderWatchStatus(1, "Novetats", @"D:\N", false, null, 10, 10, 0, null),
            new FolderWatchStatus(2, "Falques", @"D:\F", false, null, 3, 4, 1, null),
            new FolderWatchStatus(3, "Programes", @"D:\P", false, null, 0, 0, 0, "No es troba la carpeta"),
        ]);
        Assert.Equal(new Dictionary<string, string>
        {
            ["carpeta:2"] = "Carpeta vigilada de «Falques»: 1 pista té el fitxer que falta",
            ["carpeta:3"] = "Carpeta vigilada de «Programes»: No es troba la carpeta",
        }, texts);
    }

    private JsonNode List(CommandRouter router, JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        msg["cmd"] = "incidents.list";
        var reply = router.Handle(msg.ToJsonString());
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["result"]!;
    }

    [Fact]
    public void Incidents_list_dona_les_obertes_o_les_filtrades()
    {
        var tracker = Tracker();
        tracker.Report(IncidentKind.File, "fitxer:a", IncidentSeverity.Warning, "Saltada", recovered: true);
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Emergència");
        var router = new CommandRouter(new EngineState(), () => { }, incidentStore: _store, incidents: tracker);

        var open = List(router, new() { ["open"] = true });
        Assert.Equal(1, open["total"]!.GetValue<int>());
        var incident = open["incidents"]![0]!;
        Assert.Equal(("SORTIDA", "danger", "Emergència", true), (incident["kind"]!.GetValue<string>(),
            incident["severity"]!.GetValue<string>(), incident["text"]!.GetValue<string>(), incident["open"]!.GetValue<bool>()));

        var files = List(router, new() { ["kinds"] = new JsonArray("FITXER") });
        Assert.Equal((1, true, false), (files["total"]!.GetValue<int>(), files["incidents"]![0]!["recovered"]!.GetValue<bool>(),
            files["incidents"]![0]!["open"]!.GetValue<bool>()));

        Assert.Equal(2, List(router, new() { ["from"] = "2026-11-16", ["to"] = "2026-11-16" })["total"]!.GetValue<int>());
        Assert.Equal(0, List(router, new() { ["from"] = "2026-11-17" })["total"]!.GetValue<int>());
    }

    [Fact]
    public void Restaurar_una_copia_no_toca_les_incidencies()
    {
        var backups = new Backups(_db, _temp.File("copies"));
        var tracker = Tracker();
        tracker.Report(IncidentKind.File, "fitxer:a", IncidentSeverity.Warning, "Abans de la còpia");
        string copy = backups.Create(new DateTime(2026, 11, 16, 3, 0, 0));
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "Després de la còpia");

        backups.Restore(copy, new RestoreOptions { KeepHistory = false }, new DateTime(2026, 11, 16, 12, 0, 0));

        Assert.Equal(["Després de la còpia", "Abans de la còpia"], All().Select(i => i.Text).ToArray());
        // L'oberta es pot continuar tancant amb el mateix id
        tracker.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, null);
        Assert.All(All(), i => Assert.False(i.IsOpen));
    }
}
