using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Security;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Registre unificat (D-167): incidències, canvis, sessions i esdeveniments en una sola llista.</summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly UserStore _users;
    private readonly SessionManager _sessions;
    private readonly CommandRouter _router;
    private readonly long _joan;
    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    public ActivityLogTests()
    {
        _db = Database.Open(_temp.File("ob24.db"));
        _users = new UserStore(_db);
        _sessions = new SessionManager(_users, () => T0, 1000);
        _router = new CommandRouter(new EngineState(), () => { }, new Library(_db), sessions: _sessions, users: _users, activity: new ActivityLog(_db));

        _joan = _sessions.SaveUser(new User { Name = "joan", Role = UserRole.Administrator }, "contrasenya").Id;
        var incidents = new IncidentStore(_db);
        incidents.Add(new Incident { Kind = "FITXER", Key = "a", Severity = IncidentSeverity.Warning, Text = "No s'ha pogut obrir a.mp3", StartedAt = T0.AddMinutes(1), EndedAt = T0.AddMinutes(1) });
        incidents.Add(new Incident { Kind = "SORTIDA", Key = "sortida", Severity = IncidentSeverity.Danger, Text = "Sortida d'emergència", StartedAt = T0.AddMinutes(3) });
        _users.AddChange(new ChangeEntry { At = T0.AddMinutes(2), UserId = _joan, UserName = "joan", Command = "scheduler.pause", Summary = "Ha pausat el planificador" });
        _users.AddChange(new ChangeEntry { At = T0, UserId = _joan, UserName = "joan", Command = "session.login", Summary = "Ha iniciat la sessió" });
        var events = new Events(_db);
        events.AddExecution(new ExecutionRecord { Name = "Butlletí 13 h", ScheduledFor = T0.AddMinutes(4), ExecutedAt = T0.AddMinutes(4), Result = ExecutionResult.Ok });
        events.AddExecution(new ExecutionRecord { Name = "Falques", ScheduledFor = T0.AddMinutes(5), Result = ExecutionResult.SkippedLate });
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private (string[] Lines, int Total) List(LogFilter filter)
    {
        var (entries, total) = new ActivityLog(_db).List(filter);
        return (entries.Select(e => $"{e.Source}: {e.Text}").ToArray(), total);
    }

    [Fact]
    public void Tot_junt_de_mes_nou_a_mes_antic()
    {
        var (lines, total) = List(new LogFilter());
        Assert.Equal(6, total);
        Assert.Equal(new[]
        {
            "Event: Falques",
            "Event: Butlletí 13 h",
            "Incident: Sortida d'emergència",
            "Change: Ha pausat el planificador",
            "Incident: No s'ha pogut obrir a.mp3",
            "Session: Ha iniciat la sessió",
        }, lines);
    }

    [Fact]
    public void Els_filtres()
    {
        Assert.Equal(["Incident: Sortida d'emergència"], List(new LogFilter { OpenOnly = true }).Lines);
        Assert.Equal(["Incident: Sortida d'emergència"], List(new LogFilter { MinSeverity = IncidentSeverity.Danger }).Lines);
        Assert.Equal(["Event: Falques", "Incident: Sortida d'emergència", "Incident: No s'ha pogut obrir a.mp3"],
            List(new LogFilter { MinSeverity = IncidentSeverity.Warning }).Lines); // omès = avís; els canvis i les sessions són informació
        Assert.Equal(["Change: Ha pausat el planificador", "Session: Ha iniciat la sessió"], List(new LogFilter { UserId = _joan }).Lines);
        Assert.Equal(["Incident: No s'ha pogut obrir a.mp3"], List(new LogFilter { IncidentKinds = ["FITXER"] }).Lines);
        Assert.Equal(["Change: Ha pausat el planificador"], List(new LogFilter { CommandPrefixes = ["scheduler."] }).Lines);
        Assert.Equal(["Event: Butlletí 13 h"], List(new LogFilter { Text = "butlletí" }).Lines.Where(l => l.Contains("Butlletí")).ToArray());
        Assert.Equal(["Event: Falques", "Event: Butlletí 13 h"], List(new LogFilter { Sources = [LogSource.Event] }).Lines);
        var (page, total) = List(new LogFilter { From = T0.AddMinutes(2), To = T0.AddMinutes(5), Limit = 2, Offset = 1 });
        Assert.Equal(new[] { "Incident: Sortida d'emergència", "Change: Ha pausat el planificador" }, page);
        Assert.Equal(3, total);
    }

    [Fact]
    public void Sense_ser_administrador_no_es_veuen_els_canvis_ni_les_sessions()
    {
        _sessions.SaveUser(new User { Name = "admin", Role = UserRole.Administrator }, "contrasenya");
        _sessions.SaveUser(new User { Name = "operadora", Role = UserRole.Operator }, "contrasenya");
        JsonObject Send(ClientContext client, string cmd, JsonObject? args = null)
        {
            var msg = new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = cmd };
            foreach (var (key, value) in args ?? []) msg[key] = value?.DeepClone();
            return _router.Handle(msg.ToJsonString(), client);
        }
        var client = new ClientContext();
        int Count(JsonObject? args = null) => Send(client, "log.list", args)["result"]!["entries"]!.AsArray()
            .Count(e => e!["source"]!.GetValue<string>() is "change" or "session");

        Assert.Equal(0, Count()); // mode de consulta: incidències i esdeveniments
        Assert.False(Send(client, "log.list")["result"]!["changesAllowed"]!.GetValue<bool>());
        Send(client, "session.login", new() { ["user"] = "operadora", ["password"] = "contrasenya" });
        Assert.Equal(0, Count(new() { ["sources"] = new JsonArray("change") }));
        Send(client, "session.login", new() { ["user"] = "admin", ["password"] = "contrasenya" });
        Assert.True(Count() >= 2);
    }
}
