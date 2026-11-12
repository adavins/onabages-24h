using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Carpetes vigilades i bosses renovables (fase 3, pas 7c; D-026, D-033, D-090).</summary>
public sealed class FolderWatcherTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly string _folder;
    private readonly long _sub;
    private readonly FolderWatcher _watcher;

    public FolderWatcherTests()
    {
        _db = Database.Open(_temp.File("vigilades.db"));
        _library = new Library(_db);
        _folder = Path.Combine(_temp.Path, "bossa");
        Directory.CreateDirectory(_folder);
        _sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        _library.SetFolderSettings(_sub, _folder, renewable: false, TrackType.Music, ["actualitat"], false);
        _watcher = new FolderWatcher(_library);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Un WAV de <paramref name="seconds"/> s de to, amb la data d'escriptura de fa una estona (ja copiat), si no es diu el contrari.</summary>
    private string Wav(string name, double seconds = 2, bool fresh = false)
    {
        string path = Path.Combine(_folder, name);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)))
        {
            var samples = new float[(int)(seconds * 44100) * 2];
            for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * (float)Math.Sin(i * 0.03);
            writer.WriteSamples(samples, 0, samples.Length);
        }
        if (!fresh) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
        return path;
    }

    private IReadOnlyList<FolderAction> Log() => _library.FolderLog(100).Select(e => e.Action).Reverse().ToList();

    [Fact]
    public void Un_fitxer_nou_s_importa_amb_els_valors_per_defecte()
    {
        string path = Wav("Artista - Nova.wav");
        Assert.True(_watcher.ScanAll());

        var track = _library.FindByPath(path)!;
        Assert.Equal(_sub, track.SubcategoryId);
        Assert.Equal(["actualitat"], _library.GetTags(track.Id));
        Assert.Equal([FolderAction.Imported], Log());
        var status = Assert.Single(_watcher.Status);
        Assert.Equal((1, 1, 0), (status.Files, status.Tracks, status.Missing));
        Assert.Null(status.Problem);

        Assert.False(_watcher.ScanAll()); // la segona vegada, res de nou
    }

    [Fact]
    public void Un_fitxer_que_s_acaba_de_copiar_espera_a_la_revisio_seguent()
    {
        string path = Wav("Copiant.wav", fresh: true);
        Assert.False(_watcher.ScanAll());
        Assert.Null(_library.FindByPath(path));
    }

    [Fact]
    public void Un_fitxer_que_desapareix_queda_com_que_falta_a_la_segona_revisio_i_es_recupera_si_torna()
    {
        string path = Wav("Marxa.wav");
        Wav("Es queda.wav"); // si la carpeta quedés buida, es tractaria com a no disponible (i no es tocaria res)
        _watcher.ScanAll();
        string moved = Path.Combine(_temp.Path, "Marxa.wav");
        File.Move(path, moved);

        Assert.False(_watcher.ScanAll()); // primera revisió sense el fitxer: s'espera (potser s'està sincronitzant)
        Assert.Equal(FileState.Ok, _library.FindByPath(path)!.FileState);
        Assert.True(_watcher.ScanAll());
        Assert.Equal(FileState.Missing, _library.FindByPath(path)!.FileState);
        Assert.Equal(1, _watcher.Status[0].Missing);

        File.Move(moved, path);
        Assert.True(_watcher.ScanAll());
        Assert.Equal(FileState.Ok, _library.FindByPath(path)!.FileState);
        Assert.Equal([FolderAction.Imported, FolderAction.Imported, FolderAction.Missing, FolderAction.Recovered], Log());
    }

    [Fact]
    public void En_una_bossa_renovable_les_que_surten_s_esborren_excepte_les_dels_esdeveniments()
    {
        _library.SetFolderSettings(_sub, _folder, renewable: true, TrackType.Music, [], false);
        string free = Wav("Surt.wav");
        string used = Wav("Surt però és en un esdeveniment.wav");
        _watcher.ScanAll();
        long usedId = _library.FindByPath(used)!.Id;
        new Events(_db).Save(new ScheduledEvent { Name = "Promo", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{usedId}}}""" }] });
        Wav("Es queda.wav");
        File.Delete(free);
        File.Delete(used);

        _watcher.ScanAll();
        _watcher.ScanAll();

        Assert.Null(_library.FindByPath(free));
        Assert.Equal(FileState.Missing, _library.GetTrack(usedId)!.FileState);
        Assert.Contains(FolderAction.Deleted, Log());
    }

    [Fact]
    public void Si_la_carpeta_no_hi_es_o_surt_buida_no_es_toca_res()
    {
        string path = Wav("Una.wav");
        _watcher.ScanAll();

        File.Move(path, Path.Combine(_temp.Path, "Una.wav")); // carpeta buida (Drive desconnectat…)
        _watcher.ScanAll();
        _watcher.ScanAll();
        Assert.Equal(FileState.Ok, _library.FindByPath(path)!.FileState);
        Assert.Contains("buida", _watcher.Status[0].Problem);

        Directory.Delete(_folder);
        _watcher.ScanAll();
        _watcher.ScanAll();
        Assert.Equal(FileState.Ok, _library.FindByPath(path)!.FileState);
        Assert.Contains("No es troba", _watcher.Status[0].Problem);
        Assert.Contains(FolderAction.Folder, Log());
    }

    [Fact]
    public void Un_fitxer_substituit_es_la_mateixa_pista_amb_la_durada_nova()
    {
        string path = Wav("Programa.wav", seconds: 2);
        _watcher.ScanAll();
        var before = _library.FindByPath(path)!;

        Wav("Programa.wav", seconds: 4); // l'Automatitzador el substitueix
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        Assert.True(_watcher.ScanAll());

        var after = _library.FindByPath(path)!;
        Assert.Equal(before.Id, after.Id);
        Assert.InRange(after.DurationMs!.Value, 3990, 4010);
        Assert.Null(after.CueSource); // l'analitzador els tornarà a calcular
        Assert.Contains(FolderAction.Updated, Log());
    }

    [Fact]
    public void Una_pista_que_faltava_amb_el_mateix_nom_es_recupera_en_lloc_de_duplicar_se()
    {
        long old = _library.AddTrack(new Track { Path = @"G:\altre ordinador\bossa\Artista - Cançó.wav", SubcategoryId = _sub,
            FileState = FileState.Missing, PlayCount = 7 });
        string path = Wav("Artista - Cançó.wav");

        _watcher.ScanAll();

        Assert.Equal(path, _library.GetTrack(old)!.Path);
        Assert.Single(_library.TracksInSubcategory(_sub));
        Assert.Equal([FolderAction.Recovered], Log());
    }

    [Fact]
    public void Ordres_folders_get_set_i_log()
    {
        var router = new CommandRouter(new EngineState(), () => { }, _library, watcher: _watcher);
        JsonObject Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }
        string Error(JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        var got = Send(new() { ["cmd"] = "folders.get", ["subcategoryId"] = _sub })["result"]!["folder"]!;
        Assert.Equal(_folder, got["folder"]!.GetValue<string>());

        long other = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Altra");
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "folders.set", ["subcategoryId"] = other, ["folder"] = Path.Combine(_temp.Path, "no-hi-és") })));
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "folders.set", ["subcategoryId"] = other, ["folder"] = _folder }))); // ja la vigila Actualitat
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "folders.set", ["subcategoryId"] = other, ["folder"] = _temp.Path }))); // la conté

        string own = Path.Combine(_temp.Path, "altra");
        Directory.CreateDirectory(own);
        var set = Send(new() { ["cmd"] = "folders.set", ["subcategoryId"] = other, ["folder"] = own + "\\", ["renewable"] = true,
            ["type"] = "sweeper", ["tags"] = new JsonArray("català") })["result"]!["folder"]!;
        Assert.Equal(own, set["folder"]!.GetValue<string>());
        Assert.True(set["renewable"]!.GetValue<bool>());
        Assert.Equal("sweeper", set["type"]!.GetValue<string>());

        // Deixar de vigilar-la
        Assert.Null(Send(new() { ["cmd"] = "folders.set", ["subcategoryId"] = other, ["folder"] = null })["result"]!["folder"]!["folder"]);

        Wav("Una.wav");
        _watcher.ScanAll();
        var log = Send(new() { ["cmd"] = "folders.log", ["subcategoryId"] = _sub })["result"]!["entries"]!.AsArray();
        Assert.Equal("imported", log[0]!["action"]!.GetValue<string>());
        Assert.Single(Send(new() { ["cmd"] = "folders.status" })["result"]!["folders"]!.AsArray());
    }
}
