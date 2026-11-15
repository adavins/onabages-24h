using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Còpies de seguretat des de la interfície (fase 4, pas 7b; D-152): backups.list, create, inspect i restore.</summary>
public sealed class BackupCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly CommandRouter _router;
    private int _restored;

    public BackupCommandsTests()
    {
        _db = Database.Open(_temp.File("emissio.db"));
        _library = new Library(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library, backups: new Backups(_db, _temp.File("copies")));
        _router.DatabaseRestored += () => _restored++;
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private JsonNode Ok(JsonObject msg)
    {
        var reply = Send(msg);
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["result"]!;
    }

    [Fact]
    public void Es_fa_una_copia_es_mira_i_es_restaura_sencera_conservant_la_sortida_i_l_historial()
    {
        var track = _library.GetTrack(_library.AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3", Title = "A" }))!;
        _library.SetSetting(CommandRouter.OutputSettingKey, """{"id":"aquest-ordinador"}""");
        string copy = Ok(new() { ["cmd"] = "backups.create" })["path"]!.GetValue<string>();

        var list = Ok(new() { ["cmd"] = "backups.list" })["backups"]!.AsArray();
        Assert.Equal(("auto", copy), (list[0]!["kind"]!.GetValue<string>(), list[0]!["path"]!.GetValue<string>()));

        var info = Ok(new() { ["cmd"] = "backups.inspect", ["path"] = copy });
        Assert.Equal((1, (string?)null), (info["tracks"]!.GetValue<int>(), info["problem"]?.GetValue<string>()));

        // Després de la còpia: una pista més, una emissió i una altra sortida
        _library.AddTrack(new Track { Path = @"D:\EMISSIO\b.mp3" });
        _library.AddHistory(track, DateTimeOffset.UtcNow, QueueOrigin.Manual);
        _library.SetSetting(CommandRouter.OutputSettingKey, """{"id":"una-altra"}""");

        var result = Ok(new() { ["cmd"] = "backups.restore", ["path"] = copy });
        Assert.Equal(1, result["keptHistory"]!.GetValue<int>());
        Assert.True(File.Exists(result["safety"]!.GetValue<string>()));
        Assert.Equal(1, _restored);
        Assert.Equal(1, _library.CountTracks()); // la pista b no hi era a la còpia
        Assert.Single(_library.HistoryFor(track.Id, null, DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Equal("""{"id":"una-altra"}""", _library.GetSetting(CommandRouter.OutputSettingKey)); // la d'ara

        // La de l'estat d'abans surt a la llista, per poder desfer
        Assert.Contains(Ok(new() { ["cmd"] = "backups.list" })["backups"]!.AsArray(), b => b!["kind"]!.GetValue<string>() == "safety");
    }

    [Fact]
    public void Una_copia_que_no_es_pot_restaurar_no_toca_res()
    {
        string bad = _temp.File("no-es-db.db");
        File.WriteAllText(bad, "això no és SQLite");
        var reply = Send(new() { ["cmd"] = "backups.restore", ["path"] = bad });
        Assert.Equal("bad_params", reply["error"]!["code"]!.GetValue<string>());
        Assert.Equal(0, _restored);
    }
}
