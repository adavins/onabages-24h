using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Importació d'una carpeta local i comparació de punts (fase 1, pas 5b).</summary>
public class FolderImportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly string _music;

    public FolderImportTests()
    {
        _db = Database.Open(_temp.File("carpeta.db"));
        _library = new Library(_db);
        _music = Path.Combine(_temp.Path, "música");
        Directory.CreateDirectory(Path.Combine(_music, "subcarpeta"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>WAV amb 0,5 s de silenci, 3 s de to a −6 dB i 0,5 s de silenci.</summary>
    private string Wav(string relativePath)
    {
        const int rate = 44100;
        string path = Path.Combine(_music, relativePath);
        var samples = new List<float>();
        void Part(double seconds, float amplitude)
        {
            for (int i = 0; i < seconds * rate; i++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / rate);
                samples.Add(v);
                samples.Add(v);
            }
        }
        Part(0.5, 0);
        Part(3, 0.5f);
        Part(0.5, 0);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
        return path;
    }

    [Fact]
    public void Importa_els_fitxers_nous_amb_els_punts_i_salta_els_dolents()
    {
        Wav("Artista Un - Primera cançó.wav");
        Wav(Path.Combine("subcarpeta", "Segona.wav"));
        File.WriteAllText(Path.Combine(_music, "malmès.mp3"), "no és àudio");
        File.WriteAllText(Path.Combine(_music, "notes.txt"), "no és àudio i no es mira");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Proves"), "Bossa local");

        var report = FolderImporter.Import(_library, _music, sub);

        Assert.Equal(2, report.Added);
        Assert.Single(report.Unreadable);
        Assert.Contains("malmès.mp3", report.Unreadable[0]);

        var first = _library.FindByPath(Path.Combine(_music, "Artista Un - Primera cançó.wav"))!;
        Assert.Equal(("Artista Un", "Primera cançó"), (first.Artist, first.Title));
        Assert.Equal(sub, first.SubcategoryId);
        Assert.Equal(CueSource.Analyzer, first.CueSource);
        Assert.InRange(first.StartMs!.Value, 495, 505);
        Assert.InRange(first.EndMs!.Value, 3495, 3505);
        Assert.InRange(first.DurationMs!.Value, 3990, 4010);

        // Una segona vegada no hi afegeix res
        var again = FolderImporter.Import(_library, _music, sub);
        Assert.Equal(0, again.Added);
        Assert.Equal(2, again.AlreadyThere);
    }

    [Fact]
    public void Les_pistes_noves_porten_els_valors_per_defecte_i_les_que_faltaven_es_recuperen()
    {
        string path = Wav("Promo - Matinal.wav");
        string missing = Wav("Tornada.wav");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Promos"), "Programes");
        long old = _library.AddTrack(new Track { Path = missing, Title = "Ja hi era", FileState = FileState.Missing, SubcategoryId = sub });

        var report = new FolderImportReport();
        var defaults = new ImportDefaults(sub, TrackType.Promo, ["matins", "promo"], VariableDuration: true);
        foreach (string file in FolderImporter.ListAudioFiles(_music))
        {
            var (outcome, withoutSound, error) = FolderImporter.ImportFile(_library, file, defaults);
            FolderImporter.Count(report, file, outcome, withoutSound, error);
        }

        Assert.Equal((1, 1), (report.Added, report.Reactivated));
        var added = _library.FindByPath(path)!;
        Assert.Equal(TrackType.Promo, added.Type);
        Assert.True(added.VariableDuration);
        Assert.Equal(["matins", "promo"], _library.GetTags(added.Id));
        var back = _library.GetTrack(old)!;
        Assert.Equal(FileState.Ok, back.FileState);
        Assert.Equal("Ja hi era", back.Title); // la mateixa fitxa
    }

    [Fact]
    public void La_importacio_en_segon_pla_avisa_del_progres_i_acaba_amb_l_informe()
    {
        Wav("U.wav");
        Wav(Path.Combine("subcarpeta", "Dos.wav"));
        File.WriteAllText(Path.Combine(_music, "malmès.mp3"), "no és àudio");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Proves"), "Bossa");
        using var importer = new LibraryImporter(_library);
        var seen = new List<ImportStatus>();
        importer.Progress += s => { lock (seen) seen.Add(s); };

        var first = importer.Start(_music, new ImportDefaults(sub), "Proves / Bossa");
        Assert.Equal(ImportState.Listing, first.State);
        Assert.Throws<InvalidOperationException>(() => importer.Start(_music, new ImportDefaults(sub), "Proves / Bossa"));
        Assert.True(importer.Wait(TimeSpan.FromSeconds(30)));

        var last = importer.Status!;
        Assert.Equal(ImportState.Done, last.State);
        Assert.Equal((3, 3, 2), (last.Total, last.Done, last.Added));
        Assert.Contains("malmès.mp3", Assert.Single(last.Unreadable));
        Assert.NotNull(last.Finished);
        lock (seen) Assert.Equal(ImportState.Done, seen[^1].State); // l'últim avís és el del final
        Assert.False(importer.Cancel()); // ja no n'hi ha cap en curs
    }

    [Fact]
    public void Una_carpeta_que_no_existeix_acaba_com_a_fallada()
    {
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Proves"), "Bossa");
        using var importer = new LibraryImporter(_library);
        importer.Start(Path.Combine(_temp.Path, "no-hi-és"), new ImportDefaults(sub), "Proves / Bossa");
        Assert.True(importer.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(ImportState.Failed, importer.Status!.State);
        Assert.Contains("No es troba la carpeta", importer.Status.Error);
    }

    [Fact]
    public void Ordres_library_import_i_importStatus()
    {
        Wav("Una.wav");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Proves"), "Bossa");
        using var importer = new LibraryImporter(_library);
        var router = new OnaBages24.Engine.Protocol.CommandRouter(new OnaBages24.Engine.EngineState(), () => { }, _library, importer: importer);
        System.Text.Json.Nodes.JsonObject Send(System.Text.Json.Nodes.JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }
        string Error(System.Text.Json.Nodes.JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        Assert.Null(Send(new() { ["cmd"] = "library.importStatus" })["result"]!["import"]);
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["folder"] = @"relativa\carpeta", ["subcategoryId"] = sub })));
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["folder"] = _music, ["subcategoryId"] = 999 })));
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["folder"] = _music, ["subcategoryId"] = sub, ["type"] = "cançó" })));

        var started = Send(new() { ["cmd"] = "library.import", ["folder"] = _music, ["subcategoryId"] = sub, ["type"] = "sweeper", ["tags"] = new System.Text.Json.Nodes.JsonArray("català") });
        Assert.Equal("Proves / Bossa", started["result"]!["import"]!["subcategory"]!.GetValue<string>());
        Assert.True(importer.Wait(TimeSpan.FromSeconds(30)));

        var status = Send(new() { ["cmd"] = "library.importStatus" })["result"]!["import"]!;
        Assert.Equal("done", status["state"]!.GetValue<string>());
        Assert.Equal(1, status["added"]!.GetValue<int>());
        var track = _library.FindByPath(Path.Combine(_music, "Una.wav"))!;
        Assert.Equal(TrackType.Sweeper, track.Type);
        Assert.Equal(["català"], _library.GetTags(track.Id));
    }

    [Fact]
    public void Les_pistes_d_una_carpeta_inclouen_les_subcarpetes_i_res_mes()
    {
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 9\a.mp3" });
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 9\sub\b.mp3" });
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 90\c.mp3" });
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA_9\d.mp3" }); // «_» és un comodí de LIKE: no ha de coincidir

        var tracks = _library.TracksInFolder(@"o:\MUSICA\bossa 9\");

        Assert.Equal([@"O:\musica\BOSSA 9\a.mp3", @"O:\musica\BOSSA 9\sub\b.mp3"], tracks.Select(t => t.Path));
    }

    [Fact]
    public void La_comparacio_dona_la_diferencia_amb_els_punts_de_RadioDJ()
    {
        string path = Wav("Comparació.wav");
        _library.AddTrack(new Track
        {
            Path = path, CueSource = CueSource.RadioDj, StartMs = 480, MixMs = 3400, EndMs = 3500,
        });

        var results = CueComparison.Compare(_library.TracksInFolder(_music));

        var only = Assert.Single(results);
        Assert.InRange(only.StartDiff, 15, 25);   // l'analitzador diu ~500 ms
        Assert.InRange(only.MixDiff, 95, 105);    // ~3500 ms (el to s'acaba de cop: sense solapament)
        Assert.InRange(only.EndDiff, -5, 5);
        Assert.Contains("Pistes comparades: 1", CueComparison.Summary(results));
    }

    [Theory]
    [InlineData("Artista - Títol.mp3", null, null, "Artista", "Títol")]
    [InlineData("Artista - Títol - Remix.mp3", null, null, "Artista", "Títol - Remix")]
    [InlineData("sense guió.mp3", null, null, "", "sense guió")]
    [InlineData("Fitxer - Nom.mp3", "Etiqueta", "Títol de l'etiqueta", "Etiqueta", "Títol de l'etiqueta")]
    public void L_artista_i_el_titol_surten_de_les_etiquetes_o_del_nom_del_fitxer(
        string file, string? tagArtist, string? tagTitle, string artist, string title)
    {
        Assert.Equal((artist, title), FolderImporter.NamesFor(file, tagArtist, tagTitle));
    }
}
