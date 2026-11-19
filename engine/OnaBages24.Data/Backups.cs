using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Una còpia de seguretat de la carpeta de còpies.</summary>
public sealed record BackupFile(string Path, DateTime Created);

/// <summary>Què es conserva de l'estat actual en restaurar una còpia (D-152).</summary>
public sealed record RestoreOptions
{
    /// <summary>L'historial i el registre d'execució posteriors a la còpia (per defecte, sí).</summary>
    public bool KeepHistory { get; init; } = true;
    /// <summary>Les claus de configuració que es queden amb el valor d'ara (les que depenen de l'ordinador).</summary>
    public IReadOnlyList<string> KeepSettings { get; init; } = [];
}

/// <summary>El resultat d'una restauració: la còpia de l'estat anterior i quantes entrades de l'historial i del registre s'han conservat.</summary>
public sealed record RestoreResult(string SafetyPath, int KeptHistory, int KeptExecutions);

/// <summary>Què hi ha en una còpia (D-152). <paramref name="Problem"/>: per què no es pot restaurar, o null.</summary>
public sealed record BackupInfo(int Version, string? Problem, int Tracks, int Events, int Rotations, int Campaigns, DateTimeOffset? LastPlayed);

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
    private const string MaintenancePrefix = "onabages24-abans-del-manteniment-";
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

    /// <summary>
    /// La còpia d'abans d'aplicar el manteniment de la programació (D-194). Com la d'abans de restaurar, no entra a la
    /// rotació: es queda fins que algú l'esborri. Retorna la ruta.
    /// </summary>
    public string CreateBeforeMaintenance(DateTime? now = null) =>
        Copy(MaintenancePrefix + (now ?? DateTime.Now).ToString(NameFormat, CultureInfo.InvariantCulture) + ".db");

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
    /// Restaura una còpia sobre la base de dades oberta, sense conservar res de l'estat actual. Vegeu
    /// <see cref="Restore(string, RestoreOptions, DateTime?)"/>.
    /// </summary>
    /// <returns>La ruta de la còpia de l'estat anterior (<c>onabages24-abans-de-restaurar-…</c>, que no s'esborra sola).</returns>
    public string Restore(string backupPath, DateTime? now = null) => Restore(backupPath, new RestoreOptions { KeepHistory = false }, now).SafetyPath;

    /// <summary>
    /// Restaura una còpia sobre la base de dades oberta, sense tancar-la (D-032, D-152):
    /// 1. comprova la versió: si la còpia és d'un esquema més nou, o no és d'Ona Bages 24h, es rebutja sense tocar res;
    /// 2. fa una còpia de l'estat actual (per poder desfer);
    /// 3. copia el contingut de la còpia a la base de dades i aplica les migracions que faltin;
    /// 4. si es demana, hi torna a posar l'historial i el registre d'execució d'ara que són posteriors a la còpia (perquè no
    ///    es perdin emissions dels certificats, D-011) i els valors d'ara de les claus de configuració indicades (les que
    ///    depenen de l'ordinador, com la sortida d'àudio);
    /// 5. les incidències es queden totes com ara (són el registre d'aquest ordinador, D-156), també les obertes; i també
    ///    els usuaris i el registre de canvis (D-164: una còpia antiga no pot tornar a activar un usuari ni deixar fora
    ///    l'administrador d'ara).
    /// Tot dins del mateix bloqueig: cap altre codi escriu a mig fer. L'àudio no depèn de la base de dades.
    /// </summary>
    public RestoreResult Restore(string backupPath, RestoreOptions options, DateTime? now = null)
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
            List<object?[]> history = [], executions = [], incidents = [], users = [], changes = [];
            var settings = new Dictionary<string, string?>();
            using (var source = OpenFile(copy.Path, SqliteOpenMode.ReadWrite))
            {
                if (options.KeepHistory)
                {
                    // El que s'ha emès després de l'última entrada de la còpia
                    string? lastHistory = Scalar(source, "historial", "SELECT max(inici) FROM historial;");
                    string? lastExecution = Scalar(source, "registre_execucio", "SELECT max(programat_per) FROM registre_execucio;");
                    history = Rows(database.Connection,
                        "SELECT id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta, campanya_id FROM historial " +
                        "WHERE $after IS NULL OR inici > $after ORDER BY id;", lastHistory);
                    executions = Rows(database.Connection,
                        "SELECT id, esdeveniment_id, nom, programat_per, executat_a, resultat, detall FROM registre_execucio " +
                        "WHERE $after IS NULL OR programat_per > $after ORDER BY id;", lastExecution);
                }
                // Les incidències d'ara, totes (D-156)
                incidents = Rows(database.Connection,
                    "SELECT id, tipus, clau, gravetat, text, detall, inici, fi, recuperada FROM incidencia WHERE $after IS NULL ORDER BY id;", null);
                // Els usuaris i el registre de canvis d'ara, tots (D-164)
                users = Rows(database.Connection, $"SELECT {UserColumns} FROM usuari WHERE $after IS NULL ORDER BY id;", null);
                changes = Rows(database.Connection, $"SELECT {ChangeColumns} FROM registre_canvis WHERE $after IS NULL ORDER BY id;", null);
                foreach (string key in options.KeepSettings)
                {
                    using var read = database.Command("SELECT valor FROM configuracio WHERE clau = $k;");
                    read.Parameters.AddWithValue("$k", key);
                    settings[key] = read.ExecuteScalar() as string;
                }
                source.BackupDatabase(database.Connection);
            }
            database.Migrate();

            using var transaction = database.BeginTransaction();
            foreach (var row in history) InsertHistory(row, transaction);
            foreach (var row in executions) InsertExecution(row, transaction);
            using (var clear = database.Command("DELETE FROM incidencia;", transaction)) clear.ExecuteNonQuery();
            foreach (var row in incidents)
            {
                using var insert = database.Command(
                    "INSERT INTO incidencia (id, tipus, clau, gravetat, text, detall, inici, fi, recuperada) " +
                    "VALUES ($id, $tipus, $clau, $gravetat, $text, $detall, $inici, $fi, $recuperada);", transaction);
                string[] names = ["$id", "$tipus", "$clau", "$gravetat", "$text", "$detall", "$inici", "$fi", "$recuperada"];
                for (int i = 0; i < names.Length; i++) insert.Parameters.AddWithValue(names[i], row[i] ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            using (var clear = database.Command("DELETE FROM registre_canvis; DELETE FROM usuari;", transaction)) clear.ExecuteNonQuery();
            foreach (var row in users) InsertRow("usuari", UserColumns, row, transaction);
            foreach (var row in changes) InsertRow("registre_canvis", ChangeColumns, row, transaction);
            foreach (var (key, value) in settings)
            {
                using var write = database.Command(value is null
                    ? "DELETE FROM configuracio WHERE clau = $k;"
                    : "INSERT INTO configuracio (clau, valor) VALUES ($k, $v) ON CONFLICT(clau) DO UPDATE SET valor = excluded.valor;", transaction);
                write.Parameters.AddWithValue("$k", key);
                if (value is not null) write.Parameters.AddWithValue("$v", value);
                write.ExecuteNonQuery();
            }
            transaction.Commit();
            return new RestoreResult(safety, history.Count, executions.Count);
        }
    }

    private const string UserColumns = "id, nom, nom_complet, rol, empremta, sal, iteracions, actiu, provisional, creat, darrer_acces";
    private const string ChangeColumns = "id, moment, usuari_id, usuari_nom, ordre, resum, detall";

    /// <summary>Torna a posar una fila tal com era (amb el mateix id), a la taula i les columnes indicades.</summary>
    private void InsertRow(string table, string columns, object?[] row, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        string[] names = columns.Split(", ");
        using var insert = database.Command(
            $"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", names.Select((_, i) => $"$c{i}"))});", transaction);
        for (int i = 0; i < names.Length; i++) insert.Parameters.AddWithValue($"$c{i}", row[i] ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Una entrada de l'historial d'abans, a la base de dades restaurada. Conserva l'id si està lliure (el motor encara pot
    /// tenir obert el de la pista que sona); la pista, si a la restaurada hi ha la mateixa (o una amb la mateixa ruta); la
    /// campanya, si hi és.
    /// </summary>
    private void InsertHistory(object?[] row, SqliteTransaction transaction)
    {
        long? id = Free("historial", row[0], transaction);
        object? trackId = null;
        using (var find = database.Command(
            "SELECT id FROM pista WHERE id = $id AND ruta = $ruta UNION ALL SELECT id FROM pista WHERE ruta = $ruta LIMIT 1;", transaction))
        {
            find.Parameters.AddWithValue("$id", row[1] ?? DBNull.Value);
            find.Parameters.AddWithValue("$ruta", row[8] ?? "");
            trackId = find.ExecuteScalar();
        }
        object? campaignId = null;
        if (row[9] is not null)
        {
            using var find = database.Command("SELECT id FROM campanya WHERE id = $id;", transaction);
            find.Parameters.AddWithValue("$id", row[9]);
            campaignId = find.ExecuteScalar();
        }
        using var insert = database.Command(
            "INSERT INTO historial (id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta, campanya_id) " +
            "VALUES ($id, $pista, $inici, $durada, $sencera, $origen, $artista, $titol, $ruta, $campanya);", transaction);
        insert.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        insert.Parameters.AddWithValue("$pista", trackId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$inici", row[2]);
        insert.Parameters.AddWithValue("$durada", row[3] ?? DBNull.Value);
        insert.Parameters.AddWithValue("$sencera", row[4] ?? DBNull.Value);
        insert.Parameters.AddWithValue("$origen", row[5]);
        insert.Parameters.AddWithValue("$artista", row[6]);
        insert.Parameters.AddWithValue("$titol", row[7]);
        insert.Parameters.AddWithValue("$ruta", row[8]);
        insert.Parameters.AddWithValue("$campanya", campaignId ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    /// <summary>Una línia del registre d'execució d'abans, a la base de dades restaurada (amb l'id, si està lliure).</summary>
    private void InsertExecution(object?[] row, SqliteTransaction transaction)
    {
        long? id = Free("registre_execucio", row[0], transaction);
        using var insert = database.Command(
            "INSERT INTO registre_execucio (id, esdeveniment_id, nom, programat_per, executat_a, resultat, detall) " +
            "VALUES ($id, $esdeveniment, $nom, $programat, $executat, $resultat, $detall);", transaction);
        insert.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        insert.Parameters.AddWithValue("$esdeveniment", row[1] ?? DBNull.Value);
        insert.Parameters.AddWithValue("$nom", row[2]);
        insert.Parameters.AddWithValue("$programat", row[3]);
        insert.Parameters.AddWithValue("$executat", row[4] ?? DBNull.Value);
        insert.Parameters.AddWithValue("$resultat", row[5]);
        insert.Parameters.AddWithValue("$detall", row[6] ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    /// <summary>L'id, si a la taula encara no hi és; si no, null (se'n posarà un de nou).</summary>
    private long? Free(string table, object? id, SqliteTransaction transaction)
    {
        if (id is not long value) return null;
        using var check = database.Command($"SELECT 1 FROM {table} WHERE id = $id;", transaction);
        check.Parameters.AddWithValue("$id", value);
        return check.ExecuteScalar() is null ? value : null;
    }

    /// <summary>Un valor d'una consulta, o null si la taula no existeix (una còpia d'un esquema antic).</summary>
    private static string? Scalar(SqliteConnection connection, string table, string sql)
    {
        if (!TableExists(connection, table)) return null;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Les files d'una consulta amb el paràmetre <c>$after</c>.</summary>
    private static List<object?[]> Rows(SqliteConnection connection, string sql, string? after)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// Què hi ha en una còpia, sense modificar-la, per ensenyar-ho abans de restaurar-la: la versió de l'esquema, si es pot
    /// restaurar i quantes pistes, esdeveniments, rotacions i campanyes té, i l'última emissió de l'historial.
    /// </summary>
    /// <exception cref="DatabaseException">Si el fitxer no és una base de dades vàlida.</exception>
    public static BackupInfo Inspect(string backupPath)
    {
        int version = ReadVersion(backupPath);
        using var copy = ReadableCopy.Of(backupPath);
        using var connection = OpenFile(copy.Path, SqliteOpenMode.ReadWrite);
        int Count(string table)
        {
            if (!TableExists(connection, table)) return 0;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        string? last = Scalar(connection, "historial", "SELECT max(inici) FROM historial;");
        string? problem = version == 0 ? "El fitxer no és una còpia d'Ona Bages 24h (no té esquema)."
            : version > Database.LatestVersion ? $"La còpia és d'una versió més nova de l'aplicació (esquema {version}; aquesta versió en coneix fins al {Database.LatestVersion})."
            : null;
        return new BackupInfo(version, problem, Count("pista"), Count("esdeveniment"), Count("rotacio"), Count("campanya"),
            last is null ? null : DbTime.ParseInstant(last));
    }

    /// <summary>Les còpies de l'estat d'abans de cada restauració (fora de la rotació), de la més nova a la més antiga.</summary>
    public IReadOnlyList<BackupFile> ListSafety() => ListWithPrefix(SafetyPrefix);

    /// <summary>Les còpies d'abans del manteniment de la programació (D-194), de la més nova a la més antiga.</summary>
    public IReadOnlyList<BackupFile> ListMaintenance() => ListWithPrefix(MaintenancePrefix);

    private IReadOnlyList<BackupFile> ListWithPrefix(string prefix)
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<BackupFile>();
        foreach (string path in Directory.EnumerateFiles(Folder, prefix + "*.db"))
        {
            string stamp = System.IO.Path.GetFileNameWithoutExtension(path)[prefix.Length..];
            if (DateTime.TryParseExact(stamp, NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created))
                list.Add(new BackupFile(path, created));
        }
        return list.OrderByDescending(b => b.Created).ToList();
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
