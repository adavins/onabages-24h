using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Assistent «Renova la bossa» (fase 3, pas 7c-3; D-091).</summary>
public sealed class RenewTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly LibraryImporter _importer;
    private readonly CommandRouter _router;
    private readonly string _old, _new;
    private readonly long _sub, _kept, _leaving, _inEvent;

    public RenewTests()
    {
        _db = Database.Open(_temp.File("renova.db"));
        _library = new Library(_db);
        _importer = new LibraryImporter(_library);
        _router = new CommandRouter(new EngineState(), () => { }, _library, importer: _importer);
        _old = Path.Combine(_temp.Path, "ACTUAL-1");
        _new = Path.Combine(_temp.Path, "ACTUAL-2");
        Directory.CreateDirectory(_old);
        Directory.CreateDirectory(_new);
        _sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        _library.SetFolderSettings(_sub, _old, renewable: true, TrackType.Music, ["actualitat"], false);

        // La bossa d'ara: una cançó que continua, una que surt i una que surt però és en un esdeveniment
        _kept = _library.AddTrack(new Track { Path = Wav(_old, "Es queda.wav"), Title = "Es queda", SubcategoryId = _sub, PlayCount = 9 });
        _leaving = _library.AddTrack(new Track { Path = Wav(_old, "Surt.wav"), Title = "Surt", SubcategoryId = _sub });
        _inEvent = _library.AddTrack(new Track { Path = Wav(_old, "Promo.wav"), Title = "Promo", SubcategoryId = _sub });
        new Events(_db).Save(new ScheduledEvent { Name = "Promo", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{_inEvent}}}""" }] });

        // La bossa nova: la que continua i una de nova
        Wav(_new, "Es queda.wav");
        Wav(_new, "Nova.wav");
    }

    public void Dispose()
    {
        _importer.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private static string Wav(string folder, string name)
    {
        string path = Path.Combine(folder, name);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
        var samples = new float[44100 * 2 * 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.5f * (float)Math.Sin(i * 0.03);
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    [Fact]
    public void La_previsualitzacio_diu_que_es_mante_que_entra_i_que_surt_sense_canviar_res()
    {
        var p = Send(new() { ["cmd"] = "folders.renewPreview", ["subcategoryId"] = _sub, ["folder"] = _new })["result"]!;

        Assert.Equal((1, 1, 1, 1), (p["keep"]!.GetValue<int>(), p["enter"]!.GetValue<int>(), p["leave"]!.GetValue<int>(), p["leaveInEvents"]!.GetValue<int>()));
        Assert.Equal("Es queda", p["keepNames"]![0]!.GetValue<string>());
        Assert.Equal("Nova", p["enterNames"]![0]!.GetValue<string>());
        Assert.Equal("Surt", p["leaveNames"]![0]!.GetValue<string>());
        Assert.Equal(3, _library.TracksInSubcategory(_sub).Count); // no ha canviat res
    }

    [Fact]
    public void Renovar_mante_la_fitxa_de_les_que_continuen_importa_les_noves_i_esborra_les_que_surten()
    {
        var started = Send(new() { ["cmd"] = "folders.renew", ["subcategoryId"] = _sub, ["folder"] = _new, ["watch"] = true });
        Assert.True(started["result"]!["import"]!["renew"]!.GetValue<bool>());
        Assert.True(_importer.Wait(TimeSpan.FromSeconds(30)));

        var status = _importer.Status!;
        Assert.Equal(ImportState.Done, status.State);
        Assert.Equal((1, 1, 1, 1), (status.Moved, status.Added, status.Removed, status.KeptForEvents));

        var kept = _library.GetTrack(_kept)!;
        Assert.Equal(Path.Combine(_new, "Es queda.wav"), kept.Path); // mateixa pista (i historial), a la carpeta nova
        Assert.Equal(9, kept.PlayCount);
        Assert.NotNull(_library.FindByPath(Path.Combine(_new, "Nova.wav")));
        Assert.Equal(["actualitat"], _library.GetTags(_library.FindByPath(Path.Combine(_new, "Nova.wav"))!.Id));
        Assert.Null(_library.GetTrack(_leaving));
        Assert.NotNull(_library.GetTrack(_inEvent)); // la fa servir un esdeveniment: no es toca
        Assert.Equal(_new, _library.GetFolderSettings(_sub)!.Folder); // ara vigila la carpeta nova
    }

    [Fact]
    public void Amb_una_carpeta_sense_audio_no_es_renova_res()
    {
        string empty = Path.Combine(_temp.Path, "buida");
        Directory.CreateDirectory(empty);
        Send(new() { ["cmd"] = "folders.renew", ["subcategoryId"] = _sub, ["folder"] = empty, ["watch"] = true });
        Assert.True(_importer.Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(ImportState.Failed, _importer.Status!.State);
        Assert.Equal(3, _library.TracksInSubcategory(_sub).Count);
        Assert.Equal(_old, _library.GetFolderSettings(_sub)!.Folder);
    }
}
