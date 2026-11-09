using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Una còpia de seguretat de la carpeta de còpies.</summary>
public sealed record BackupFile(string Path, DateTime Created);

/// <summary>
/// Còpies de seguretat de la base de dades (D-032). Es fan amb l'eina de còpia de SQLite, que copia
/// la base de dades oberta sense aturar res.
///
/// Les còpies es desen a una carpeta, amb el nom <c>onabages24-AAAA-MM-DD_HHMMSS.db</c> (hora local).
/// La rotació conserva l'última còpia de cada dia dels últims <see cref="DailyToKeep"/> dies i l'última
/// de cada mes dels últims <see cref="MonthlyToKeep"/> mesos.
/// </summary>
public sealed class Backups(Database database, string folder)
{
    private const string Prefix = "onabages24-";
    private const string SafetyPrefix = "onabages24-abans-de-restaurar-";
    private const string NameFormat = "yyyy-MM-dd_HHmmss";

    public string Folder { get; } = folder;
    public int DailyToKeep { get; init; } = 14;
    public int MonthlyToKeep { get; init; } = 12;

    /// <summary>Fa una còpia ara i aplica la rotació. Retorna la ruta de la còpia.</summary>
    /// <param name="now">Hora local (per a les proves).</param>
    public string Create(DateTime? now = null)
    {
        DateTime time = now ?? DateTime.Now;
        string path = Copy(Prefix + time.ToString(NameFormat, CultureInfo.InvariantCulture) + ".db");
        Prune(time);
        return path;
    }

    /// <summary>Copia la base de dades a la carpeta de còpies amb aquest nom de fitxer.</summary>
    private string Copy(string fileName)
    {
        Directory.CreateDirectory(Folder);
        string path = System.IO.Path.Combine(Folder, fileName);

        lock (database.Sync)
        {
            using var destination = OpenFile(path, SqliteOpenMode.ReadWriteCreate);
            database.Connection.BackupDatabase(destination);
            // La còpia hereta el mode WAL; la passem al mode clàssic perquè sigui un sol fitxer independent.
            using var command = destination.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = DELETE;";
            command.ExecuteNonQuery();
        }
        return path;
    }

    /// <summary>Fa la còpia diària si avui encara no se n'ha fet cap. Retorna la ruta, o null si no calia.</summary>
    public string? CreateIfDue(DateTime? now = null)
    {
        DateTime time = now ?? DateTime.Now;
        return List().Any(b => b.Created.Date == time.Date) ? null : Create(time);
    }

    /// <summary>Les còpies de la carpeta, de la més nova a la més antiga.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<BackupFile>();
        foreach (string path in Directory.EnumerateFiles(Folder, Prefix + "*.db"))
        {
            string stamp = System.IO.Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
            if (DateTime.TryParseExact(stamp, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                list.Add(new BackupFile(path, created));
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>Quines còpies sobren segons la rotació (càlcul separat per poder-lo provar).</summary>
    public static IReadOnlyList<BackupFile> SelectToDelete(IEnumerable<BackupFile> backups, DateTime now, int dailyToKeep, int monthlyToKeep)
    {
        var all = backups.OrderByDescending(b => b.Created).ToList();
        var keep = new HashSet<BackupFile>();

        // L'última de cada dia, dels últims N dies (avui inclòs)
        DateTime firstDay = now.Date.AddDays(-(dailyToKeep - 1));
        foreach (var day in all.Where(b => b.Created.Date >= firstDay).GroupBy(b => b.Created.Date))
            keep.Add(day.First());

        // L'última de cada mes, dels últims N mesos (aquest inclòs)
        var firstMonth = new DateTime(now.Year, now.Month, 1).AddMonths(-(monthlyToKeep - 1));
        foreach (var month in all.Where(b => b.Created >= firstMonth).GroupBy(b => (b.Created.Year, b.Created.Month)))
            keep.Add(month.First());

        return all.Where(b => !keep.Contains(b)).ToList();
    }

    private void Prune(DateTime now)
    {
        foreach (var old in SelectToDelete(List(), now, DailyToKeep, MonthlyToKeep))
        {
            try { File.Delete(old.Path); }
            catch (IOException) { /* es tornarà a provar a la còpia següent */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Versió de l'esquema d'una còpia, sense modificar-la.</summary>
    /// <exception cref="DatabaseException">Si el fitxer no és una base de dades vàlida.</exception>
    public static int ReadVersion(string backupPath)
    {
        if (!File.Exists(backupPath)) throw new DatabaseException($"No es troba la còpia: {backupPath}");
        using var copy = ReadableCopy.Of(backupPath);
        try
        {
            using var connection = OpenFile(copy.Path, SqliteOpenMode.ReadWrite);
            return Database.ReadVersion(connection);
        }
        catch (SqliteException ex)
        {
            throw new DatabaseException($"El fitxer no és una còpia vàlida: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Restaura una còpia sobre la base de dades oberta, sense tancar-la (D-032):
    /// 1. comprova la versió: si la còpia és d'un esquema més nou, es rebutja;
    /// 2. fa una còpia de l'estat actual (per poder desfer);
    /// 3. copia el contingut de la còpia a la base de dades;
    /// 4. aplica les migracions que faltin (si la còpia és d'una versió anterior).
    /// L'àudio no depèn de la base de dades: l'emissió continua mentrestant.
    /// </summary>
    /// <returns>La ruta de la còpia de l'estat anterior (<c>onabages24-abans-de-restaurar-…</c>, que no s'esborra sola).</returns>
    public string Restore(string backupPath, DateTime? now = null)
    {
        int version = ReadVersion(backupPath);
        if (version > Database.LatestVersion)
            throw new DatabaseException(
                $"La còpia és d'una versió més nova de l'aplicació (esquema {version}; aquesta versió en coneix fins al {Database.LatestVersion}). No es pot restaurar.");
        if (version == 0)
            throw new DatabaseException("El fitxer no és una còpia d'Ona Bages 24h (no té esquema).");

        // Còpia de l'estat actual, amb un nom propi i fora de la rotació: la rotació podria esborrar
        // la mateixa còpia que es vol restaurar (només conserva l'última de cada dia).
        DateTime time = now ?? DateTime.Now;
        string safety = Copy(SafetyPrefix + time.ToString(NameFormat, CultureInfo.InvariantCulture) + ".db");

        lock (database.Sync)
        {
            using var copy = ReadableCopy.Of(backupPath);
            using var source = OpenFile(copy.Path, SqliteOpenMode.ReadWrite);
            source.BackupDatabase(database.Connection);
        }

        database.Migrate();
        return safety;
    }

    /// <summary>
    /// Duplicat temporal d'una còpia, per llegir-la sense tocar mai l'original. Cal perquè SQLite no pot obrir només
    /// per llegir una base de dades en mode WAL (p. ex. una còpia feta a mà del fitxer de l'emissió).
    /// </summary>
    private sealed class ReadableCopy : IDisposable
    {
        public string Path { get; }

        private ReadableCopy(string path) => Path = path;

        public static ReadableCopy Of(string original)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"onabages24-restaura-{Guid.NewGuid():N}.db");
            File.Copy(original, path);
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(original + suffix)) File.Copy(original + suffix, path + suffix);
            }
            return new ReadableCopy(path);
        }

        public void Dispose()
        {
            foreach (string file in new[] { Path, Path + "-wal", Path + "-shm" })
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static SqliteConnection OpenFile(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
