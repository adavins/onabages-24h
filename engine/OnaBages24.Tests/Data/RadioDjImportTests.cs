using OnaBages24.Data;
using OnaBages24.Data.RadioDj;

namespace OnaBages24.Tests.Data;

/// <summary>Importació de la biblioteca de RadioDJ (fase 1, pas 5a).</summary>
public class RadioDjImportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;

    public RadioDjImportTests()
    {
        _db = Database.Open(_temp.File("importacio.db"));
        _library = new Library(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Carpeta <c>docs/radiodj</c> del repositori (es busca pujant des de la carpeta de les proves).</summary>
    private static string RadioDjDocs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "radiodj")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("No es troba docs/radiodj"), "docs", "radiodj");
    }

    private const string Schema = """
        CREATE TABLE `songs` (
          `ID` int(11) NOT NULL AUTO_INCREMENT,
          `path` varchar(255) NOT NULL,
          `title` varchar(255) NOT NULL,
          PRIMARY KEY (`ID`)
        ) ENGINE=MyISAM;
        """;

    [Fact]
    public void El_lector_entén_els_escapaments_de_MySQL()
    {
        var dump = MySqlDump.FromText(Schema + """
            -- comentari
            INSERT INTO `songs` VALUES (1,'D:\\EMISSIO\\M\ÚSICA\\a.mp3','Sons a cau d\'orella'),(2,'b','dues\nl\ínies, amb (par\èntesis)'),(3,'c',NULL);
            INSERT INTO `songs` VALUES (4,'d','e');
            """);

        var rows = dump.Rows("songs");

        Assert.Equal(4, rows.Count);
        Assert.Equal(@"D:\EMISSIO\MÚSICA\a.mp3", rows[0]["path"]);
        Assert.Equal("Sons a cau d'orella", rows[0]["title"]);
        Assert.Equal("dues\nlínies, amb (parèntesis)", rows[1]["title"]);
        Assert.Null(rows[2]["title"]);
        Assert.Equal(4, rows[3].Long("ID"));
        Assert.Empty(dump.Rows("no_existeix"));
    }

    [Fact]
    public void Dades_sense_estructura_donen_un_error_clar()
    {
        var dump = MySqlDump.FromText("INSERT INTO `songs` VALUES (1,'a','b');");
        Assert.Throws<InvalidDataException>(() => dump.Rows("songs"));
    }

    [Fact]
    public void Importa_les_mostres_del_repositori()
    {
        string docs = RadioDjDocs();
        var dump = MySqlDump.Load(
            Path.Combine(docs, "esquema.sql"), Path.Combine(docs, "dades-referencia.sql"), Path.Combine(docs, "songs_table_examples.sql"));

        var report = RadioDjImporter.ImportLibrary(dump, _library, new RadioDjImportOptions { CheckFiles = false });

        Assert.True(report.TracksAdded > 0);
        Assert.Empty(report.Skipped);
        Assert.Equal(report.TracksAdded, _library.CountTracks());
        Assert.Equal(12, report.Categories);

        // La promo de l'exemple: durada variable, dates de vigència i punts de RadioDJ
        var promo = _library.FindByRadioDjId(2501)!;
        Assert.Equal(TrackType.Promo, promo.Type);
        Assert.True(promo.VariableDuration);
        Assert.Equal("Sons a cau d'orella", promo.Title);
        Assert.Equal(new DateOnly(2026, 9, 21), promo.ValidFrom);
        Assert.Equal(new DateOnly(2027, 6, 30), promo.ValidUntil);
        Assert.Equal((31L, 19_562L, 19_643L), (promo.StartMs!.Value, promo.MixMs!.Value, promo.EndMs!.Value));
        Assert.Equal(CueSource.RadioDj, promo.CueSource);
        Assert.Equal(FileState.Ok, promo.FileState); // no s'han comprovat els fitxers

        // Una cançó: categoria Music → tipus Música, a la seva subcategoria
        var song = _library.FindByRadioDjId(23977)!;
        Assert.Equal(TrackType.Music, song.Type);
        Assert.False(song.VariableDuration);
        Assert.NotNull(song.SubcategoryId);
    }

    [Fact]
    public void Importa_les_rotacions_en_ordre_i_amb_els_generes_com_a_etiquetes()
    {
        string docs = RadioDjDocs();
        var dump = MySqlDump.Load(Path.Combine(docs, "esquema.sql"), Path.Combine(docs, "dades-referencia.sql"));
        var rotations = new Rotations(_db);

        var report = RadioDjImporter.ImportLibrary(dump, _library, new RadioDjImportOptions { CheckFiles = false }, rotations);

        var all = rotations.List();
        Assert.Equal(5, all.Count);
        Assert.Equal("OnaBages", all[0].Name); // l'id més baix de RadioDJ (4): és la que farà servir l'AutoDJ per defecte

        // La rotació OnaBages del backup de referència: 12 passos, en l'ordre de «ord»
        var onaBages = all[0];
        Assert.Equal(12, onaBages.Steps.Count);
        Assert.Equal("Ona Bages Adult 5", onaBages.Steps[0].SubcategoryName);
        Assert.Equal(RotationSelection.LeastRecent, onaBages.Steps[0].Selection);
        Assert.True(onaBages.Steps[0].RepeatRule);
        Assert.Equal(RotationSelection.Random, onaBages.Steps[1].Selection);

        // Pas 3: sweeper en català (gènere 150 → etiqueta), sense criteris de repetició
        var sweeper = onaBages.Steps[2];
        Assert.Equal("Sweepers", sweeper.CategoryName);
        Assert.Equal("Ona Bages", sweeper.SubcategoryName);
        Assert.Equal("SW - catala", sweeper.TagName);
        Assert.False(sweeper.RepeatRule);
        Assert.Equal("Jingles", onaBages.Steps[11].CategoryName);

        // Dance90, com a la pantalla de RadioDJ: pas 1 aleatori amb trepitjador a l'inici; pas 3 «la menys reproduïda» (selType 2)
        var dance = all.Single(r => r.Name == "Dance90").Steps;
        Assert.Equal(RotationSelection.Random, dance[0].Selection);
        Assert.True(dance[0].Sweeper);
        Assert.Equal("Jingles", dance[1].CategoryName);
        Assert.Equal(RotationSelection.LeastPlayed, dance[2].Selection);
        Assert.False(dance[2].Sweeper);
        Assert.Empty(report.RotationWarnings);

        // Tornar a importar no duplica: substitueix els passos
        RadioDjImporter.ImportLibrary(dump, _library, new RadioDjImportOptions { CheckFiles = false }, rotations);
        Assert.Equal(5, rotations.List().Count);
        Assert.Equal(12, rotations.List()[0].Steps.Count);
    }

    [Fact]
    public void Tornar_a_importar_actualitza_sense_duplicar()
    {
        string docs = RadioDjDocs();
        var dump = MySqlDump.Load(
            Path.Combine(docs, "esquema.sql"), Path.Combine(docs, "dades-referencia.sql"), Path.Combine(docs, "songs_table_examples.sql"));
        var options = new RadioDjImportOptions { CheckFiles = false };

        var first = RadioDjImporter.ImportLibrary(dump, _library, options);
        var second = RadioDjImporter.ImportLibrary(dump, _library, options);

        Assert.Equal(0, second.TracksAdded);
        Assert.Equal(first.TracksAdded, second.TracksUpdated);
        Assert.Equal(first.TracksAdded, _library.CountTracks());
    }

    [Fact]
    public void Amb_el_canvi_d_arrel_es_troben_els_fitxers_i_els_altres_queden_com_a_que_falten()
    {
        // Una carpeta local que fa de «NAS», amb un dels dos fitxers
        string music = Path.Combine(_temp.Path, "musica", "BOSSA 9");
        Directory.CreateDirectory(music);
        File.WriteAllBytes(Path.Combine(music, "hi-es.mp3"), [1, 2, 3]);

        var dump = MySqlDump.FromText(FullSchema + """
            INSERT INTO `category` VALUES (1,'Music');
            INSERT INTO `subcategory` VALUES (5,1,'Ona Bages Adult 9');
            INSERT INTO `genre` VALUES (96,'Other'),(150,'SW - catala');
            INSERT INTO `songs` VALUES
              (10,'D:\\EMISSIO\\M\ÚSICA\\OnaBages\\BOSSA 9\\hi-es.mp3',1,'2026-01-08 10:00:00',0,'2002-01-01 00:00:01','2002-01-01 00:00:01',0,5,96,50.0,200.5,'&sta=0.1&xta=195.2&end=199.9&fin=0&fou=4.7','A','T','','1900'),
              (11,'D:\\EMISSIO\\M\ÚSICA\\OnaBages\\BOSSA 9\\no-hi-es.mp3',1,'2002-01-01 00:00:01',0,'2002-01-01 00:00:01','2002-01-01 00:00:01',0,5,150,50.0,180,'&','B','U','','2001');
            """);

        var report = RadioDjImporter.ImportLibrary(dump, _library, new RadioDjImportOptions
        {
            RootChanges = [new RootChange(@"D:\EMISSIO\MÚSICA\OnaBages\", Path.Combine(_temp.Path, "musica") + @"\")],
        });

        var found = _library.FindByRadioDjId(10)!;
        Assert.Equal(Path.Combine(music, "hi-es.mp3"), found.Path);
        Assert.Equal(FileState.Ok, found.FileState);
        Assert.Equal(3, found.FileSize);
        Assert.Equal("", found.Year); // 1900 = sense any
        Assert.Empty(_library.GetTags(found.Id)); // «Other» no és etiqueta

        var missing = _library.FindByRadioDjId(11)!;
        Assert.Equal(FileState.Missing, missing.FileState);
        Assert.Null(missing.MixMs); // «&» sense punts: s'analitzarà
        Assert.Equal(["SW - catala"], _library.GetTags(missing.Id));

        Assert.Equal(1, report.FilesMissing);
        Assert.Equal(1, report.WithoutCuePoints);

        // L'hora de l'última reproducció es desa en UTC (RadioDJ la desa en hora local)
        var expected = new DateTimeOffset(DateTime.SpecifyKind(new DateTime(2026, 1, 8, 10, 0, 0), DateTimeKind.Local)).ToUniversalTime();
        Assert.Equal(expected, found.LastPlayed);
    }

    private const string FullSchema = """
        CREATE TABLE `category` (
          `ID` int(11) NOT NULL AUTO_INCREMENT,
          `name` varchar(100) NOT NULL,
          PRIMARY KEY (`ID`)
        );
        CREATE TABLE `subcategory` (
          `ID` int(11) NOT NULL AUTO_INCREMENT,
          `parentid` int(11) NOT NULL,
          `name` varchar(100) NOT NULL,
          PRIMARY KEY (`ID`)
        );
        CREATE TABLE `genre` (
          `ID` int(11) NOT NULL AUTO_INCREMENT,
          `name` varchar(100) NOT NULL,
          PRIMARY KEY (`ID`)
        );
        CREATE TABLE `songs` (
          `ID` int(11) NOT NULL AUTO_INCREMENT,
          `path` varchar(255) NOT NULL,
          `enabled` int(1) NOT NULL DEFAULT '0',
          `date_played` datetime DEFAULT '2002-01-01 00:00:01',
          `count_played` mediumint(9) NOT NULL DEFAULT '0',
          `start_date` datetime DEFAULT '2002-01-01 00:00:01',
          `end_date` datetime DEFAULT '2002-01-01 00:00:01',
          `song_type` tinyint(2) NOT NULL,
          `id_subcat` int(11) NOT NULL,
          `id_genre` int(11) NOT NULL,
          `weight` double(5,1) NOT NULL DEFAULT '50.0',
          `duration` double(11,5) NOT NULL,
          `cue_times` varchar(255) NOT NULL DEFAULT '&',
          `artist` varchar(255) NOT NULL,
          `title` varchar(255) NOT NULL,
          `album` varchar(255) NOT NULL,
          `year` varchar(4) NOT NULL DEFAULT '1900',
          PRIMARY KEY (`ID`)
        );
        """;

    [Theory]
    [InlineData("Music", 0, @"D:\EMISSIO\MÚSICA\a.mp3", TrackType.Music)]
    [InlineData("Sweepers", 2, @"D:\EMISSIO\sweepers\a.mp3", TrackType.Sweeper)]
    [InlineData("Jingles", 1, @"D:\EMISSIO\caretes\a.mp3", TrackType.StationId)]
    [InlineData("Station IDs", 4, @"D:\EMISSIO\caretes\formules\FOR - INICI Dance90.mp3", TrackType.StationId)]
    [InlineData("Station IDs", 7, @"C:\RadioDJ\hores\15.mp3", TrackType.TimeAnnouncement)]
    [InlineData("Station IDs", 4, @"C:\RadioDJ\hores\SENYALS HORARIS.mp3", TrackType.TimeSignal)]
    [InlineData("Promos", 7, @"D:\EMISSIO\falques\promos\a.mp3", TrackType.Promo)]
    [InlineData("Commercials", 7, @"D:\EMISSIO\falques\a.mp3", TrackType.Commercial)]
    [InlineData("Radio Shows", 7, @"D:\EMISSIO\programes\a.mp3", TrackType.Program)]
    [InlineData("Capsules", 7, @"D:\EMISSIO\capsules\a.mp3", TrackType.Program)]
    [InlineData("Sound Effects", 6, @"D:\EMISSIO\efectes\a.mp3", TrackType.Other)]
    public void El_tipus_surt_de_la_categoria_i_de_la_carpeta(string category, int songType, string path, TrackType expected)
    {
        Assert.Equal(expected, RadioDjImporter.TypeFor(category, songType, path));
    }

    [Theory]
    [InlineData("&sta=0.0308616780045351&xta=19.5622448979592&end=19.642970521542&fin=0&fou=0.0807", 19.6, 31, 19_562, 19_643)]
    [InlineData("&sta=0.5&end=100", 100, 500, 100_000, 100_000)]   // sense punt de mescla: sense solapament
    [InlineData("&sta=0.5&xta=90", 100, 500, 90_000, 100_000)]     // sense final: la durada
    public void Llegeix_els_punts_de_RadioDJ(string cueTimes, double duration, long start, long mix, long end)
    {
        Assert.Equal((start, mix, end), RadioDjImporter.ParseCueTimes(cueTimes, duration));
    }

    [Theory]
    [InlineData("&")]
    [InlineData("")]
    [InlineData("&sta=50&xta=10&end=100")] // inici després de la mescla
    public void Uns_punts_buits_o_incoherents_no_s_importen(string cueTimes)
    {
        Assert.Null(RadioDjImporter.ParseCueTimes(cueTimes, 100));
    }

    [Fact]
    public void El_canvi_d_arrel_aplica_el_primer_que_coincideix_sense_distingir_majuscules()
    {
        RootChange[] changes = [new(@"D:\EMISSIO\MÚSICA\OnaBages\", @"O:\musica\"), new(@"D:\EMISSIO\", @"\\NAS\EMISSIO\")];

        Assert.Equal(@"O:\musica\BOSSA 9\a.mp3", RadioDjImporter.ApplyRootChanges(@"d:\emissio\música\onabages\BOSSA 9\a.mp3", changes));
        Assert.Equal(@"\\NAS\EMISSIO\falques\a.mp3", RadioDjImporter.ApplyRootChanges(@"D:\EMISSIO\falques\a.mp3", changes));
        Assert.Equal(@"C:\RadioDJ\hores\00.mp3", RadioDjImporter.ApplyRootChanges(@"C:\RadioDJ\hores\00.mp3", changes));
    }
}
