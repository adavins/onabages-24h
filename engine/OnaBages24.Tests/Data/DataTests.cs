using Microsoft.Data.Sqlite;
using OnaBages24.Data;

namespace OnaBages24.Tests.Data;

/// <summary>Carpeta temporal per a una prova, que s'esborra en acabar.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OnaBages24Tests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* algun fitxer encara obert: ja s'esborrarà */ }
    }
}

/// <summary>Base de dades i migracions (fase 1, pas 4).</summary>
public class DatabaseTests
{
    [Fact]
    public void Una_base_de_dades_nova_queda_a_l_ultima_versio()
    {
        using var temp = new TempFolder();
        using var db = Database.Open(temp.File("nova.db"));

        Assert.True(Database.LatestVersion >= 1);
        Assert.Equal(Database.LatestVersion, db.Version);
    }

    [Fact]
    public void En_tornar_a_obrir_es_conserven_les_dades()
    {
        using var temp = new TempFolder();
        string path = temp.File("dades.db");
        using (var db = Database.Open(path))
            new Library(db).AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3", Title = "A" });

        using var reopened = Database.Open(path);
        Assert.Equal(1, new Library(reopened).CountTracks());
        Assert.Equal(Database.LatestVersion, reopened.Version);
    }

    [Fact]
    public void No_obre_una_base_de_dades_d_una_versio_mes_nova()
    {
        using var temp = new TempFolder();
        string path = temp.File("futura.db");
        SetVersion(path, Database.LatestVersion + 5);

        var error = Assert.Throws<DatabaseException>(() => Database.Open(path));
        Assert.Contains("més nova", error.Message);
    }

    /// <summary>Crea un fitxer SQLite buit amb una versió d'esquema concreta.</summary>
    internal static void SetVersion(string path, int version)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE IF NOT EXISTS x (a); PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
    }
}

/// <summary>Pistes, classificació, historial i configuració.</summary>
public class LibraryTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;

    public LibraryTests()
    {
        _db = Database.Open(_temp.File("biblioteca.db"));
        _library = new Library(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Una_pista_es_desa_i_es_llegeix_sencera()
    {
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Ona Bages Adult 1");
        var original = new Track
        {
            Path = @"D:\EMISSIO\Adult1\cançó.mp3",
            Type = TrackType.Commercial,
            VariableDuration = true,
            SubcategoryId = sub,
            Artist = "Artista",
            Title = "Títol amb accents: l·l",
            Album = "Àlbum",
            Year = "1999",
            DurationMs = 215_400,
            StartMs = 150,
            MixMs = 210_000,
            EndMs = 214_800,
            CueSource = CueSource.RadioDj,
            ValidFrom = new DateOnly(2025, 9, 15),
            ValidUntil = new DateOnly(2026, 6, 30),
            Enabled = false,
            FileState = FileState.Missing,
            Priority = 42.5,
            PlayCount = 7,
            LastPlayed = new DateTimeOffset(2026, 1, 8, 10, 0, 0, TimeSpan.FromHours(1)),
            FileSize = 3_456_789,
            FileModified = new DateTimeOffset(2025, 12, 1, 8, 30, 0, TimeSpan.Zero),
            RadioDjId = 1234,
            Notes = "nota",
        };

        long id = _library.AddTrack(original);
        var read = _library.GetTrack(id);

        Assert.Equal(original with { Id = id }, read);
    }

    [Fact]
    public void La_ruta_no_es_pot_repetir_encara_que_canviin_les_majuscules()
    {
        _library.AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3" });

        Assert.Throws<DatabaseException>(() => _library.AddTrack(new Track { Path = @"d:\emissio\A.MP3" }));
        Assert.NotNull(_library.FindByPath(@"D:\Emissio\A.mp3"));
    }

    [Fact]
    public void Una_pista_es_pot_modificar()
    {
        long id = _library.AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3", Title = "Abans" });
        var track = _library.GetTrack(id)! with { Title = "Després", FileState = FileState.Retired };

        _library.UpdateTrack(track);

        Assert.Equal(track, _library.GetTrack(id));
        Assert.Throws<DatabaseException>(() => _library.UpdateTrack(track with { Id = 999 }));
    }

    [Fact]
    public void Les_categories_i_subcategories_no_es_dupliquen()
    {
        long cat = _library.EnsureCategory("Music");
        Assert.Equal(cat, _library.EnsureCategory("music"));

        long sub = _library.EnsureSubcategory(cat, "Dance90");
        Assert.Equal(sub, _library.EnsureSubcategory(cat, "Dance90"));
        Assert.NotEqual(sub, _library.EnsureSubcategory(_library.EnsureCategory("Sweepers"), "Dance90"));
    }

    [Fact]
    public void Les_etiquetes_es_substitueixen_i_no_es_repeteixen()
    {
        long id = _library.AddTrack(new Track { Path = @"D:\EMISSIO\s.mp3", Type = TrackType.Sweeper });

        _library.SetTags(id, ["Català", "català", " Generals ", ""]);
        Assert.Equal(["Català", "Generals"], _library.GetTags(id));

        _library.SetTags(id, ["Nadal"]);
        Assert.Equal(["Nadal"], _library.GetTags(id));
    }

    [Fact]
    public void L_historial_desa_l_hora_en_UTC_i_compta_les_reproduccions()
    {
        long id = _library.AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3", Artist = "Art", Title = "Tit" });
        var track = _library.GetTrack(id)!;
        // 27/10/2025 a les 2:30 en horari d'hivern (+1); en UTC, 1:30
        var started = new DateTimeOffset(2025, 10, 26, 2, 30, 0, TimeSpan.FromHours(1));

        long entry = _library.AddHistory(track, started, QueueOrigin.Event);
        _library.FinishHistory(entry, TimeSpan.FromSeconds(200), complete: true);

        var history = _library.History(started.AddMinutes(-1), started.AddMinutes(1));
        var only = Assert.Single(history);
        Assert.Equal(started, only.Started);
        Assert.Equal(200_000, only.PlayedMs);
        Assert.True(only.Complete);
        Assert.Equal(QueueOrigin.Event, only.Origin);
        Assert.Equal(("Art", "Tit"), (only.Artist, only.Title));

        var after = _library.GetTrack(id)!;
        Assert.Equal(1, after.PlayCount);
        Assert.Equal(started, after.LastPlayed);
    }

    [Fact]
    public void L_historial_conserva_el_titol_del_moment()
    {
        long id = _library.AddTrack(new Track { Path = @"D:\EMISSIO\falca.mp3", Title = "Falca antiga" });
        var now = DateTimeOffset.UtcNow;
        _library.AddHistory(_library.GetTrack(id)!, now, QueueOrigin.AutoDj);

        _library.UpdateTrack(_library.GetTrack(id)! with { Title = "Falca nova" });

        Assert.Equal("Falca antiga", Assert.Single(_library.History(now.AddSeconds(-1), now.AddSeconds(1))).Title);
    }

    [Fact]
    public void La_configuracio_es_desa_per_clau()
    {
        Assert.Null(_library.GetSetting("sortida"));

        _library.SetSetting("sortida", """{"id":"abc"}""");
        _library.SetSetting("sortida", """{"id":"def"}""");

        Assert.Equal("""{"id":"def"}""", _library.GetSetting("sortida"));
    }
}

/// <summary>Còpies de seguretat i restauració (D-032).</summary>
public class BackupTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Backups _backups;

    public BackupTests()
    {
        _db = Database.Open(_temp.File("emissio.db"));
        _library = new Library(_db);
        _backups = new Backups(_db, _temp.File("copies"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Una_copia_es_pot_restaurar_i_abans_es_desa_l_estat_actual()
    {
        _library.AddTrack(new Track { Path = @"D:\EMISSIO\abans.mp3" });
        string copy = _backups.Create(new DateTime(2026, 11, 9, 3, 0, 0));
        _library.AddTrack(new Track { Path = @"D:\EMISSIO\despres.mp3" });
        Assert.Equal(2, _library.CountTracks());

        string safety = _backups.Restore(copy, new DateTime(2026, 11, 9, 12, 0, 0));

        // Es pot continuar fent servir la mateixa connexió
        Assert.Equal(1, _library.CountTracks());
        Assert.NotNull(_library.FindByPath(@"D:\EMISSIO\abans.mp3"));

        // La còpia restaurada no s'ha esborrat, i la de l'estat anterior queda fora de la rotació
        Assert.True(File.Exists(copy));
        Assert.DoesNotContain(_backups.List(), b => b.Path == safety);

        // La còpia de seguretat de l'estat anterior té les dues pistes
        using var previous = Database.Open(safety);
        Assert.Equal(2, new Library(previous).CountTracks());
    }

    [Fact]
    public void Una_copia_d_una_versio_mes_nova_es_rebutja_sense_tocar_res()
    {
        _library.AddTrack(new Track { Path = @"D:\EMISSIO\a.mp3" });
        string future = _temp.File("futura.db");
        DatabaseTests.SetVersion(future, Database.LatestVersion + 1);

        var error = Assert.Throws<DatabaseException>(() => _backups.Restore(future));

        Assert.Contains("més nova", error.Message);
        Assert.Equal(1, _library.CountTracks());
        Assert.Empty(_backups.List()); // ni tan sols s'ha fet la còpia de l'estat actual
    }

    [Fact]
    public void Un_fitxer_que_no_es_una_base_de_dades_es_rebutja()
    {
        string bad = _temp.File("no-es-db.db");
        File.WriteAllText(bad, "això no és SQLite");

        Assert.Throws<DatabaseException>(() => _backups.Restore(bad));
    }

    [Fact]
    public void La_copia_diaria_nomes_es_fa_un_cop_al_dia()
    {
        var day = new DateTime(2026, 11, 9, 3, 0, 0);

        Assert.NotNull(_backups.CreateIfDue(day));
        Assert.Null(_backups.CreateIfDue(day.AddHours(10)));
        Assert.NotNull(_backups.CreateIfDue(day.AddDays(1)));
        Assert.Equal(2, _backups.List().Count);
        Assert.Equal(Database.LatestVersion, Backups.ReadVersion(_backups.List()[0].Path));
    }

    [Fact]
    public void La_rotacio_conserva_l_ultima_de_cada_dia_i_de_cada_mes()
    {
        var now = new DateTime(2026, 11, 9, 12, 0, 0);
        var backups = new List<BackupFile>();
        // Una còpia cada dia a les 3:00 durant 400 dies, i una segona avui a les 11:00
        for (int i = 0; i < 400; i++)
            backups.Add(new BackupFile($"d{i}", now.Date.AddDays(-i).AddHours(3)));
        backups.Add(new BackupFile("avui-2", now.Date.AddHours(11)));

        var toDelete = Backups.SelectToDelete(backups, now, dailyToKeep: 14, monthlyToKeep: 12).ToHashSet();
        var kept = backups.Where(b => !toDelete.Contains(b)).ToList();

        // Avui només es conserva la més nova
        Assert.Contains(kept, b => b.Path == "avui-2");
        Assert.DoesNotContain(kept, b => b.Path == "d0");
        // Els 13 dies anteriors, sencers
        for (int i = 1; i < 14; i++) Assert.Contains(kept, b => b.Path == $"d{i}");
        // Una per mes dels 12 mesos (des del desembre del 2025), i res més antic
        Assert.All(kept, b => Assert.True(b.Created >= new DateTime(2025, 12, 1)));
        var months = kept.Select(b => (b.Created.Year, b.Created.Month)).Distinct().Count();
        Assert.Equal(12, months);
        // 14 diàries + 10 mesos més (el novembre i l'octubre ja hi són per les diàries) = 24
        Assert.Equal(14 + 10, kept.Count);
    }
}
