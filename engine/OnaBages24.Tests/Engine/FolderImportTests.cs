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
    public void Una_pista_que_falta_amb_el_mateix_nom_de_fitxer_es_recupera_en_lloc_de_duplicar_se()
    {
        string path = Wav("Artista - Cançó.wav");
        long music = _library.EnsureCategory("Music");
        long actualitat = _library.EnsureSubcategory(music, "Actualitat");
        long altra = _library.EnsureSubcategory(music, "Altra");
        // La mateixa cançó, importada de RadioDJ amb la ruta de l'altre ordinador (G:), i que per tant falta
        long old = _library.AddTrack(new Track { Path = @"G:\.shortcut-targets-by-id\x\ACTUAL-1\ARTISTA - cançó.wav", Title = "Cançó",
            SubcategoryId = actualitat, FileState = FileState.Missing, RadioDjId = 25769, PlayCount = 12, CueSource = CueSource.RadioDj, MixMs = 1000 });
        // A una altra subcategoria, amb el mateix nom: no és la mateixa
        long other = _library.AddTrack(new Track { Path = @"G:\altra\Artista - Cançó.wav", SubcategoryId = altra, FileState = FileState.Missing });

        var (outcome, _, _) = FolderImporter.ImportFile(_library, path, new ImportDefaults(actualitat));

        Assert.Equal(ImportOutcome.Reactivated, outcome);
        Assert.Equal(1, _library.TracksInSubcategory(actualitat).Count); // cap duplicat
        var back = _library.GetTrack(old)!;
        Assert.Equal(path, back.Path);
        Assert.Equal(FileState.Ok, back.FileState);
        Assert.Equal(12, back.PlayCount); // la mateixa fitxa i les reproduccions
        Assert.Null(back.CueSource); // l'analitzador els tornarà a calcular
        Assert.InRange(back.DurationMs!.Value, 3990, 4010);
        Assert.Equal(FileState.Missing, _library.GetTrack(other)!.FileState);
    }

    [Fact]
    public void Buidar_la_subcategoria_esborra_les_que_no_son_a_la_carpeta_i_no_toca_les_dels_esdeveniments()
    {
        string kept = Wav("Es queda.wav");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        long keptId = _library.AddTrack(new Track { Path = kept, SubcategoryId = sub });
        long free = _library.AddTrack(new Track { Path = @"C:\vella\Lliure.mp3", SubcategoryId = sub });
        long used = _library.AddTrack(new Track { Path = @"C:\vella\En un esdeveniment.mp3", SubcategoryId = sub });
        long elsewhere = _library.AddTrack(new Track { Path = @"C:\vella\Altra.mp3", SubcategoryId = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Altra") });
        new Events(_db).Save(new ScheduledEvent { Name = "Promo", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{used}}}""" }] });
        Wav("Nova.wav");

        using var importer = new LibraryImporter(_library);
        importer.Start(_music, new ImportDefaults(sub, EmptySubcategory: true), "Music / Actualitat");
        Assert.True(importer.Wait(TimeSpan.FromSeconds(30)));

        var status = importer.Status!;
        Assert.Equal((1, 1, 1), (status.Added, status.Removed, status.KeptForEvents));
        Assert.NotNull(_library.GetTrack(keptId));
        Assert.Null(_library.GetTrack(free));
        Assert.Equal(FileState.Ok, _library.GetTrack(used)!.FileState); // la de l'esdeveniment, com era
        Assert.NotNull(_library.GetTrack(elsewhere)); // d'una altra subcategoria: no es toca
    }

    [Fact]
    public void Una_carpeta_sense_audio_no_buida_la_subcategoria()
    {
        File.WriteAllText(Path.Combine(_music, "notes.txt"), "res");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        long track = _library.AddTrack(new Track { Path = @"C:\vella\Una.mp3", SubcategoryId = sub });

        using var importer = new LibraryImporter(_library);
        importer.Start(_music, new ImportDefaults(sub, EmptySubcategory: true), "Music / Actualitat");
        Assert.True(importer.Wait(TimeSpan.FromSeconds(10)));

        Assert.NotNull(_library.GetTrack(track));
        Assert.Contains("no s'ha buidat", importer.Status!.Warning);
    }

    [Fact]
    public void Un_fitxer_aturat_a_mig_importar_no_deixa_res_a_la_biblioteca()
    {
        string path = Wav("Aturada.wav");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Proves"), "Bossa");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel(); // s'atura mentre s'analitzen els punts (abans de desar res)

        Assert.ThrowsAny<OperationCanceledException>(() =>
            FolderImporter.ImportFile(_library, path, new ImportDefaults(sub, Tags: ["x"]), cancel: cancel.Token));

        Assert.Null(_library.FindByPath(path));
        Assert.Equal(0, _library.CountTracks());
    }

    [Fact]
    public void La_pista_i_les_etiquetes_es_desen_juntes()
    {
        long id = _library.AddTrack(new Track { Path = @"C:\x\a.mp3" }, ["català", "80s"]);
        Assert.Equal(["80s", "català"], _library.GetTags(id));

        // Si la pista no es pot desar (ruta repetida), tampoc no queda cap etiqueta nova
        Assert.Throws<DatabaseException>(() => _library.AddTrack(new Track { Path = @"C:\x\a.mp3" }, ["nova"]));
        Assert.DoesNotContain(_library.SearchFilters().Tags, t => t.Name == "nova");
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
        Assert.Null(last.Tracks); // d'una carpeta no es diuen
        Assert.Contains("malmès.mp3", Assert.Single(last.Unreadable));
        Assert.NotNull(last.Finished);
        lock (seen) Assert.Equal(ImportState.Done, seen[^1].State); // l'últim avís és el del final
        Assert.False(importer.Cancel()); // ja no n'hi ha cap en curs
    }

    [Fact]
    public void Fitxers_solts_nomes_importen_aquests_i_no_buiden_la_subcategoria()
    {
        string one = Wav("Un.wav");
        string two = Wav(Path.Combine("subcarpeta", "Dos.wav"));
        Wav("No triat.wav");
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Actualitat");
        long old = _library.AddTrack(new Track { Path = @"C:\vella\Una.mp3", SubcategoryId = sub });
        using var importer = new LibraryImporter(_library);
        var router = new OnaBages24.Engine.Protocol.CommandRouter(new OnaBages24.Engine.EngineState(), () => { }, _library, importer: importer);
        System.Text.Json.Nodes.JsonObject Send(System.Text.Json.Nodes.JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }
        string Error(System.Text.Json.Nodes.JsonObject r) => r["error"]!["code"]!.GetValue<string>();

        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["files"] = new System.Text.Json.Nodes.JsonArray(), ["subcategoryId"] = sub })));
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["files"] = new System.Text.Json.Nodes.JsonArray(Path.Combine(_music, "no-hi-és.wav")), ["subcategoryId"] = sub })));
        string text = Path.Combine(_music, "notes.txt");
        File.WriteAllText(text, "no és àudio");
        Assert.Equal("bad_params", Error(Send(new() { ["cmd"] = "library.import", ["files"] = new System.Text.Json.Nodes.JsonArray(text), ["subcategoryId"] = sub })));

        // Encara que es demani buidar la subcategoria, amb fitxers solts no es buida
        var started = Send(new() { ["cmd"] = "library.import", ["files"] = new System.Text.Json.Nodes.JsonArray(one, two), ["subcategoryId"] = sub, ["emptySubcategory"] = true });
        Assert.EndsWith("(2 fitxers)", started["result"]!["import"]!["folder"]!.GetValue<string>());
        Assert.True(importer.Wait(TimeSpan.FromSeconds(30)));

        var status = importer.Status!;
        Assert.Equal((ImportState.Done, 2, 2), (status.State, status.Total, status.Added));
        Assert.NotNull(_library.FindByPath(one));
        Assert.NotNull(_library.FindByPath(two));
        Assert.Null(_library.FindByPath(Path.Combine(_music, "No triat.wav")));
        Assert.NotNull(_library.GetTrack(old)); // no s'ha buidat
        Assert.Equal(0, status.Removed);

        // Diu quines pistes en surten, per poder obrir-ne la fitxa
        Assert.Equal([_library.FindByPath(one)!.Id, _library.FindByPath(two)!.Id], status.Tracks!.Select(t => t.Id));
        var json = Send(new() { ["cmd"] = "library.importStatus" })["result"]!["import"]!;
        Assert.Equal(2, json["tracks"]!.AsArray().Count);
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
