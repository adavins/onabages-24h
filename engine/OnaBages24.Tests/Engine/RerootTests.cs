using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Canvi d'arrel de les rutes (fase 3, pas 7d; D-025, D-092).</summary>
public sealed class RerootTests : IDisposable
{
    private const string OldRoot = @"G:\.shortcut-targets-by-id\0B7l\OB - OM - CAPSULES";
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly CommandRouter _router;
    private readonly string _newRoot;
    private readonly long _found, _notFound, _clash, _outside;

    public RerootTests()
    {
        _db = Database.Open(_temp.File("arrel.db"));
        _library = new Library(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library);
        _newRoot = Path.Combine(_temp.Path, "Mi unidad", "OB - OM - CAPSULES");
        Directory.CreateDirectory(Path.Combine(_newRoot, "ACTUAL-1"));
        File.WriteAllText(Path.Combine(_newRoot, "ACTUAL-1", "Hi és.mp3"), "x");
        File.WriteAllText(Path.Combine(_newRoot, "ACTUAL-1", "Repetida.mp3"), "x");

        _found = Add(OldRoot + @"\ACTUAL-1\Hi és.mp3", playCount: 5);
        _notFound = Add(OldRoot + @"\ACTUAL-1\No hi és.mp3");
        _clash = Add(OldRoot + @"\ACTUAL-1\Repetida.mp3");
        Add(Path.Combine(_newRoot, "ACTUAL-1", "Repetida.mp3"), FileState.Ok); // ja hi ha una pista amb la ruta nova
        _outside = Add(@"G:\.shortcut-targets-by-id\0B7l\OB - OM - CAPSULES2\Una altra.mp3");
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private long Add(string path, FileState state = FileState.Missing, long playCount = 0) =>
        _library.AddTrack(new Track { Path = path, Title = Path.GetFileNameWithoutExtension(path), FileState = state, PlayCount = playCount, MixMs = 1234 });

    private JsonNode Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        var response = _router.Handle(msg.ToJsonString());
        return response["result"] ?? throw new Xunit.Sdk.XunitException(response["error"]!.ToJsonString());
    }

    [Fact]
    public void La_previsualitzacio_compta_sense_canviar_res()
    {
        var p = Send(new() { ["cmd"] = "library.rerootPreview", ["from"] = OldRoot, ["to"] = _newRoot });
        // «…CAPSULES2» no comença per «…CAPSULES\»: no hi entra
        Assert.Equal((3, 1, 1, 1), (p["tracks"]!.GetValue<int>(), p["found"]!.GetValue<int>(), p["notFound"]!.GetValue<int>(), p["conflicts"]!.GetValue<int>()));
        Assert.Equal(3, p["examples"]!.AsArray().Count);
        Assert.StartsWith(OldRoot, _library.GetTrack(_found)!.Path);
    }

    [Fact]
    public void Per_defecte_nomes_canvien_les_que_troben_el_fitxer_amb_la_mateixa_fitxa()
    {
        var r = Send(new() { ["cmd"] = "library.reroot", ["from"] = OldRoot, ["to"] = _newRoot });
        Assert.Equal((1, 2), (r["changed"]!.GetValue<int>(), r["skipped"]!.GetValue<int>()));

        var found = _library.GetTrack(_found)!;
        Assert.Equal(Path.Combine(_newRoot, "ACTUAL-1", "Hi és.mp3"), found.Path);
        Assert.Equal(FileState.Ok, found.FileState);
        Assert.Equal(5, found.PlayCount);   // la mateixa fitxa i historial
        Assert.Equal(1234, found.MixMs);     // els punts no es toquen (és el mateix fitxer)
        Assert.StartsWith(OldRoot, _library.GetTrack(_notFound)!.Path);
        Assert.StartsWith(OldRoot, _library.GetTrack(_clash)!.Path);
        Assert.StartsWith(@"G:\.shortcut-targets-by-id\0B7l\OB - OM - CAPSULES2", _library.GetTrack(_outside)!.Path);
    }

    [Fact]
    public void Tambe_les_que_no_el_troben_si_es_demana_i_les_carpetes_vigilades_es_mouen()
    {
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        _library.SetFolderSettings(sub, OldRoot + @"\ACTUAL-1", renewable: true, TrackType.Music, [], false);

        var r = Send(new() { ["cmd"] = "library.reroot", ["from"] = OldRoot + "\\", ["to"] = _newRoot, ["onlyFound"] = false });

        Assert.Equal((2, 1, 1), (r["changed"]!.GetValue<int>(), r["changedFound"]!.GetValue<int>(), r["foldersMoved"]!.GetValue<int>()));
        var notFound = _library.GetTrack(_notFound)!;
        Assert.Equal(Path.Combine(_newRoot, "ACTUAL-1", "No hi és.mp3"), notFound.Path);
        Assert.Equal(FileState.Missing, notFound.FileState);
        Assert.Equal(Path.Combine(_newRoot, "ACTUAL-1"), _library.GetFolderSettings(sub)!.Folder);
    }

    [Fact]
    public void Si_una_ruta_xoca_no_es_canvia_cap()
    {
        string taken = _library.GetTrack(_outside)!.Path;
        Assert.Throws<DatabaseException>(() => _library.ChangePaths([
            new Library.PathChange(_found, @"C:\nova\Hi és.mp3", false, null, null),
            new Library.PathChange(_notFound, taken, false, null, null), // ja és d'una altra pista
        ]));
        Assert.StartsWith(OldRoot, _library.GetTrack(_found)!.Path); // la primera tampoc no ha canviat
    }

    [Fact]
    public void Tambe_serveix_per_a_una_carpeta_copiada_a_un_altre_lloc()
    {
        // Com «O:\musica\novetats adult contemporary» copiada a «D:\novetats adult contemporary»: les pistes encara no falten
        string original = Path.Combine(_temp.Path, "O", "musica", "novetats adult contemporary");
        string copy = Path.Combine(_temp.Path, "D", "novetats adult contemporary");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(copy);
        foreach (string name in new[] { "Una.mp3", "Dues.mp3" })
        {
            File.WriteAllText(Path.Combine(original, name), "x");
            File.WriteAllText(Path.Combine(copy, name), "x");
        }
        long one = Add(Path.Combine(original, "Una.mp3"), FileState.Ok);
        Add(Path.Combine(original, "Dues.mp3"), FileState.Ok);

        var r = Send(new() { ["cmd"] = "library.reroot", ["from"] = original, ["to"] = copy });

        Assert.Equal((2, 2), (r["changed"]!.GetValue<int>(), r["changedFound"]!.GetValue<int>()));
        Assert.Equal(Path.Combine(copy, "Una.mp3"), _library.GetTrack(one)!.Path);
    }

    [Fact]
    public void Les_carpetes_amb_fitxers_que_falten_les_que_mes_primer()
    {
        var folders = Send(new() { ["cmd"] = "library.missingFolders" })["folders"]!.AsArray();
        Assert.Equal(OldRoot + @"\ACTUAL-1", folders[0]!["folder"]!.GetValue<string>());
        Assert.Equal(3, folders[0]!["count"]!.GetValue<int>());
    }

    [Fact]
    public void L_arrel_nova_ha_d_existir_i_ser_diferent()
    {
        var bad = _router.Handle(new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "library.reroot", ["from"] = OldRoot, ["to"] = @"Z:\no\hi\és" }.ToJsonString());
        Assert.Equal("bad_params", bad["error"]!["code"]!.GetValue<string>());
        var same = _router.Handle(new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "library.reroot", ["from"] = _newRoot + "\\", ["to"] = _newRoot }.ToJsonString());
        Assert.Equal("bad_params", same["error"]!["code"]!.GetValue<string>());
    }
}
