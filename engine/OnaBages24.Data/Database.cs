using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Error de la base de dades que l'operador ha de veure (el missatge és en català).</summary>
public sealed class DatabaseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// La base de dades SQLite d'Ona Bages 24h (D-004). Només n'hi ha una connexió, oberta tota l'estona,
/// i totes les operacions hi passen d'una en una (bloqueig <see cref="Sync"/>).
///
/// En obrir-la s'apliquen les migracions que faltin (fitxers <c>Migrations/NNN_nom.sql</c>, inclosos a la DLL).
/// La versió de l'esquema es desa a la mateixa base de dades (<c>PRAGMA user_version</c>).
/// </summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Bloqueig per fer les operacions d'una en una (la connexió no es pot fer servir des de dos fils alhora).</summary>
    internal object Sync { get; } = new();

    /// <summary>Ruta del fitxer de la base de dades.</summary>
    public string Path { get; }

    /// <summary>Versió de l'esquema que coneix aquesta versió de l'aplicació (la de l'última migració).</summary>
    public static int LatestVersion => Migrations.Value[^1].Version;

    private Database(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    /// <summary>
    /// Obre (o crea) la base de dades i hi aplica les migracions que faltin.
    /// </summary>
    /// <exception cref="DatabaseException">Si la base de dades és d'una versió més nova de l'aplicació, o no es pot obrir.</exception>
    public static Database Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, // en tancar, el fitxer queda lliure de seguida (còpies, proves)
        }.ToString());

        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
            var database = new Database(path, connection);
            database.Migrate();
            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Versió de l'esquema d'aquesta base de dades.</summary>
    public int Version
    {
        get { lock (Sync) return ReadVersion(_connection); }
    }

    /// <summary>Aplica les migracions que faltin, cadascuna sencera o gens (transacció).</summary>
    internal void Migrate()
    {
        lock (Sync)
        {
            int current = ReadVersion(_connection);
            if (current > LatestVersion)
                throw new DatabaseException(
                    $"La base de dades és d'una versió més nova de l'aplicació (esquema {current}; aquesta versió en coneix fins al {LatestVersion}). Cal actualitzar Ona Bages 24h.");

            foreach (var migration in Migrations.Value.Where(m => m.Version > current))
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    Execute(_connection, migration.Sql, transaction);
                    Execute(_connection, $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)};", transaction);
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    throw new DatabaseException($"No s'ha pogut aplicar la migració {migration.Name}: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>Crea una ordre SQL sobre la connexió. Només per a les classes d'aquest projecte, dins de <see cref="Sync"/>.</summary>
    internal SqliteCommand Command(string sql, SqliteTransaction? transaction = null)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    internal SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    internal SqliteConnection Connection => _connection;

    internal static int ReadVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();

    // --- Migracions incloses a la DLL ---

    private sealed record Migration(int Version, string Name, string Sql);

    private static readonly Lazy<Migration[]> Migrations = new(LoadMigrations);

    /// <summary>Llegeix els fitxers <c>Migrations/NNN_nom.sql</c>, ordenats pel número.</summary>
    private static Migration[] LoadMigrations()
    {
        var assembly = typeof(Database).Assembly;
        const string prefix = "OnaBages24.Data.Migrations.";
        var list = new List<Migration>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string name = resource[prefix.Length..];
            // Les DLL no admeten noms de recurs que comencin per una xifra: .NET hi posa un «_» al davant.
            string digits = new(name.TrimStart('_').TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            list.Add(new Migration(int.Parse(digits, CultureInfo.InvariantCulture), name, reader.ReadToEnd()));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
                throw new InvalidOperationException($"Les migracions han d'anar numerades seguides des de 001 (falta la {i + 1:000}).");
        }
        return list.ToArray();
    }
}
