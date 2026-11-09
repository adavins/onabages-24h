using System.Globalization;
using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Tipus de pista (D-023, D-026): què és la pista. Llista tancada.</summary>
public enum TrackType
{
    Music, Sweeper, StationId, Promo, Commercial, Program, News, TimeAnnouncement, TimeSignal, Stream, Other,
}

/// <summary>Estat del fitxer d'una pista (D-026, D-033).</summary>
public enum FileState
{
    /// <summary>El fitxer hi és.</summary>
    Ok,
    /// <summary>El fitxer ha desaparegut: avís, i l'AutoDJ no la tria.</summary>
    Missing,
    /// <summary>Ha sortit d'una bossa renovable: no és un error.</summary>
    Retired,
}

/// <summary>D'on surt la informació dels punts d'encadenament.</summary>
public enum CueSource { RadioDj, Analyzer, Manual }

/// <summary>D'on ve un element de la cua (D-034).</summary>
public enum QueueOrigin { AutoDj, Manual, Event }

/// <summary>Una pista de la biblioteca. Els temps van en mil·lisegons; null = encara no se sap.</summary>
public sealed record Track
{
    public long Id { get; init; }
    public required string Path { get; init; }
    public TrackType Type { get; init; } = TrackType.Music;
    public bool VariableDuration { get; init; }
    public long? SubcategoryId { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public string Album { get; init; } = "";
    public string Year { get; init; } = "";
    public long? DurationMs { get; init; }
    public long? StartMs { get; init; }
    public long? MixMs { get; init; }
    public long? EndMs { get; init; }
    public CueSource? CueSource { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ValidUntil { get; init; }
    public bool Enabled { get; init; } = true;
    public FileState FileState { get; init; } = FileState.Ok;
    public double Priority { get; init; } = 50;
    public long PlayCount { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public long? FileSize { get; init; }
    public DateTimeOffset? FileModified { get; init; }
    public long? RadioDjId { get; init; }
    public string? Notes { get; init; }
}

/// <summary>Una entrada de l'historial de reproducció.</summary>
public sealed record HistoryEntry(
    long Id, long? TrackId, DateTimeOffset Started, long? PlayedMs, bool? Complete,
    QueueOrigin Origin, string Artist, string Title, string Path);

/// <summary>
/// Biblioteca: pistes, categories, subcategories, etiquetes, historial i configuració.
/// Cada mètode és una operació completa i segura des de qualsevol fil.
/// </summary>
public sealed class Library(Database database)
{
    // --- Categories i subcategories ---

    /// <summary>Retorna l'id de la categoria amb aquest nom; si no existeix, la crea.</summary>
    public long EnsureCategory(string name)
    {
        lock (database.Sync)
        {
            using (var insert = database.Command("INSERT OR IGNORE INTO categoria (nom) VALUES ($nom);"))
            {
                insert.Parameters.AddWithValue("$nom", name);
                insert.ExecuteNonQuery();
            }
            using var select = database.Command("SELECT id FROM categoria WHERE nom = $nom;");
            select.Parameters.AddWithValue("$nom", name);
            return (long)select.ExecuteScalar()!;
        }
    }

    /// <summary>Retorna l'id de la subcategoria amb aquest nom dins de la categoria; si no existeix, la crea.</summary>
    public long EnsureSubcategory(long categoryId, string name)
    {
        lock (database.Sync)
        {
            using (var insert = database.Command("INSERT OR IGNORE INTO subcategoria (categoria_id, nom) VALUES ($cat, $nom);"))
            {
                insert.Parameters.AddWithValue("$cat", categoryId);
                insert.Parameters.AddWithValue("$nom", name);
                insert.ExecuteNonQuery();
            }
            using var select = database.Command("SELECT id FROM subcategoria WHERE categoria_id = $cat AND nom = $nom;");
            select.Parameters.AddWithValue("$cat", categoryId);
            select.Parameters.AddWithValue("$nom", name);
            return (long)select.ExecuteScalar()!;
        }
    }

    // --- Pistes ---

    private const string TrackColumns =
        "ruta, tipus, durada_variable, subcategoria_id, artista, titol, album, any_edicio, durada_ms, inici_ms, mescla_ms, final_ms, " +
        "punts_origen, data_inici, data_fi, activa, estat_fitxer, prioritat, reproduccions, darrera_reproduccio, fitxer_mida, " +
        "fitxer_modificat, radiodj_id, notes";

    /// <summary>Afegeix una pista nova. Retorna l'id.</summary>
    /// <exception cref="DatabaseException">Si ja hi ha una pista amb la mateixa ruta.</exception>
    public long AddTrack(Track track)
    {
        lock (database.Sync)
        {
            string now = DbTime.ToText(DateTimeOffset.UtcNow);
            using var command = database.Command(
                $"INSERT INTO pista ({TrackColumns}, creada, modificada) VALUES ({ParameterList()}, $ara, $ara); SELECT last_insert_rowid();");
            AddTrackParameters(command, track);
            command.Parameters.AddWithValue("$ara", now);
            try
            {
                return (long)command.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // restricció: la ruta ja hi és
            {
                throw new DatabaseException($"Ja hi ha una pista amb aquesta ruta: {track.Path}", ex);
            }
        }
    }

    /// <summary>Desa els canvis d'una pista existent (per id).</summary>
    public void UpdateTrack(Track track)
    {
        lock (database.Sync)
        {
            var sets = string.Join(", ", TrackColumns.Split(", ").Select(c => $"{c} = ${c}"));
            using var command = database.Command($"UPDATE pista SET {sets}, modificada = $ara WHERE id = $id;");
            AddTrackParameters(command, track);
            command.Parameters.AddWithValue("$ara", DbTime.ToText(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", track.Id);
            if (command.ExecuteNonQuery() == 0)
                throw new DatabaseException($"No existeix la pista {track.Id}.");
        }
    }

    public Track? GetTrack(long id) => QueryTracks("WHERE id = $v", id).FirstOrDefault();

    public Track? FindByPath(string path) => QueryTracks("WHERE ruta = $v", path).FirstOrDefault();

    public Track? FindByRadioDjId(long radioDjId) => QueryTracks("WHERE radiodj_id = $v", radioDjId).FirstOrDefault();

    public IReadOnlyList<Track> TracksInSubcategory(long subcategoryId) =>
        QueryTracks("WHERE subcategoria_id = $v ORDER BY id", subcategoryId);

    /// <summary>Pistes la ruta de les quals comença per aquesta carpeta (sense distingir majúscules).</summary>
    public IReadOnlyList<Track> TracksInFolder(string folder)
    {
        string prefix = folder.TrimEnd('\\') + "\\";
        // LIKE amb els caràcters especials escapats (%, _ i el mateix caràcter d'escapament)
        string pattern = prefix.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
        return QueryTracks("WHERE ruta LIKE $v ESCAPE '!' ORDER BY ruta", pattern);
    }

    /// <summary>
    /// Pistes que cal (tornar a) analitzar amb el nostre analitzador (D-042): punts de RadioDJ o sense punts, amb el
    /// fitxer disponible i que no siguin streams. Mai les de punts manuals. Per ordre d'id, a partir de <paramref name="afterId"/>.
    /// </summary>
    public IReadOnlyList<Track> TracksNeedingCueAnalysis(long afterId, int limit)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                $"SELECT id FROM pista WHERE id > $after AND estat_fitxer = 'OK' AND tipus <> 'STREAM' " +
                "AND (punts_origen IS NULL OR punts_origen = 'RADIODJ') ORDER BY id LIMIT $limit;");
            command.Parameters.AddWithValue("$after", afterId);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            reader.Close();
            return ids.Select(id => QueryTracks("WHERE id = $v", id)[0]).ToList();
        }
    }

    public long CountTracks()
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT COUNT(*) FROM pista;");
            return (long)command.ExecuteScalar()!;
        }
    }

    // --- Etiquetes ---

    /// <summary>Substitueix les etiquetes d'una pista (crea les que no existeixen).</summary>
    public void SetTags(long trackId, IEnumerable<string> tags)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            using (var clear = database.Command("DELETE FROM pista_etiqueta WHERE pista_id = $p;", transaction))
            {
                clear.Parameters.AddWithValue("$p", trackId);
                clear.ExecuteNonQuery();
            }
            foreach (string tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var add = database.Command(
                    "INSERT OR IGNORE INTO etiqueta (nom) VALUES ($nom); " +
                    "INSERT OR IGNORE INTO pista_etiqueta (pista_id, etiqueta_id) SELECT $p, id FROM etiqueta WHERE nom = $nom;",
                    transaction);
                add.Parameters.AddWithValue("$nom", tag);
                add.Parameters.AddWithValue("$p", trackId);
                add.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public IReadOnlyList<string> GetTags(long trackId)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT e.nom FROM etiqueta e JOIN pista_etiqueta pe ON pe.etiqueta_id = e.id WHERE pe.pista_id = $p ORDER BY e.nom;");
            command.Parameters.AddWithValue("$p", trackId);
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    // --- Historial ---

    /// <summary>Anota que una pista ha començat a sonar. Retorna l'id de l'entrada (per tancar-la quan s'acabi).</summary>
    public long AddHistory(Track track, DateTimeOffset started, QueueOrigin origin)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO historial (pista_id, inici, origen, artista, titol, ruta) VALUES ($p, $inici, $origen, $artista, $titol, $ruta); " +
                "UPDATE pista SET reproduccions = reproduccions + 1, darrera_reproduccio = $inici WHERE id = $p; " +
                "SELECT last_insert_rowid();");
            command.Parameters.AddWithValue("$p", track.Id);
            command.Parameters.AddWithValue("$inici", DbTime.ToText(started));
            command.Parameters.AddWithValue("$origen", ToText(origin));
            command.Parameters.AddWithValue("$artista", track.Artist);
            command.Parameters.AddWithValue("$titol", track.Title);
            command.Parameters.AddWithValue("$ruta", track.Path);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Tanca una entrada de l'historial: quant ha sonat i si ha arribat al final previst.</summary>
    public void FinishHistory(long entryId, TimeSpan played, bool complete)
    {
        lock (database.Sync)
        {
            using var command = database.Command("UPDATE historial SET durada_ms = $d, sencera = $s WHERE id = $id;");
            command.Parameters.AddWithValue("$d", (long)played.TotalMilliseconds);
            command.Parameters.AddWithValue("$s", complete ? 1 : 0);
            command.Parameters.AddWithValue("$id", entryId);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Historial entre dues hores (des d'inclosa, fins a exclosa), en ordre de reproducció.</summary>
    public IReadOnlyList<HistoryEntry> History(DateTimeOffset from, DateTimeOffset to)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta FROM historial " +
                "WHERE inici >= $from AND inici < $to ORDER BY inici, id;");
            command.Parameters.AddWithValue("$from", DbTime.ToText(from));
            command.Parameters.AddWithValue("$to", DbTime.ToText(to));
            using var reader = command.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (reader.Read())
            {
                list.Add(new HistoryEntry(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    DbTime.ParseInstant(reader.GetString(2)),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4) == 1,
                    ParseOrigin(reader.GetString(5)),
                    reader.GetString(6), reader.GetString(7), reader.GetString(8)));
            }
            return list;
        }
    }

    // --- Configuració ---

    /// <summary>Valor (JSON) d'una clau de configuració, o null si no hi és.</summary>
    public string? GetSetting(string key)
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT valor FROM configuracio WHERE clau = $k;");
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string jsonValue)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO configuracio (clau, valor) VALUES ($k, $v) ON CONFLICT(clau) DO UPDATE SET valor = excluded.valor;");
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", jsonValue);
            command.ExecuteNonQuery();
        }
    }

    // --- Conversions entre el codi (anglès) i la base de dades (català, com a l'especificació) ---

    private static readonly (TrackType Type, string Text)[] TypeNames =
    [
        (TrackType.Music, "MUSICA"), (TrackType.Sweeper, "SWEEPER"), (TrackType.StationId, "IDENTIFICACIO"),
        (TrackType.Promo, "PROMO"), (TrackType.Commercial, "FALCA"), (TrackType.Program, "PROGRAMA"),
        (TrackType.News, "INFORMATIU"), (TrackType.TimeAnnouncement, "LOCUCIO_HORARIA"),
        (TrackType.TimeSignal, "SENYAL_HORARI"), (TrackType.Stream, "STREAM"), (TrackType.Other, "ALTRES"),
    ];

    private static string ToText(TrackType type) => TypeNames.First(t => t.Type == type).Text;
    private static TrackType ParseType(string text) => TypeNames.First(t => t.Text == text).Type;

    private static string ToText(FileState state) => state switch
    {
        FileState.Missing => "FALTA",
        FileState.Retired => "RETIRADA",
        _ => "OK",
    };

    private static FileState ParseFileState(string text) => text switch
    {
        "FALTA" => FileState.Missing,
        "RETIRADA" => FileState.Retired,
        _ => FileState.Ok,
    };

    private static string ToText(CueSource source) => source switch
    {
        CueSource.RadioDj => "RADIODJ",
        CueSource.Manual => "MANUAL",
        _ => "ANALITZADOR",
    };

    private static CueSource ParseCueSource(string text) => text switch
    {
        "RADIODJ" => CueSource.RadioDj,
        "MANUAL" => CueSource.Manual,
        _ => CueSource.Analyzer,
    };

    private static string ToText(QueueOrigin origin) => origin switch
    {
        QueueOrigin.Manual => "MANUAL",
        QueueOrigin.Event => "ESDEVENIMENT",
        _ => "AUTODJ",
    };

    private static QueueOrigin ParseOrigin(string text) => text switch
    {
        "MANUAL" => QueueOrigin.Manual,
        "ESDEVENIMENT" => QueueOrigin.Event,
        _ => QueueOrigin.AutoDj,
    };

    private static string ParameterList() => string.Join(", ", TrackColumns.Split(", ").Select(c => "$" + c));

    private static void AddTrackParameters(SqliteCommand command, Track t)
    {
        var p = command.Parameters;
        p.AddWithValue("$ruta", t.Path);
        p.AddWithValue("$tipus", ToText(t.Type));
        p.AddWithValue("$durada_variable", t.VariableDuration ? 1 : 0);
        p.AddWithValue("$subcategoria_id", (object?)t.SubcategoryId ?? DBNull.Value);
        p.AddWithValue("$artista", t.Artist);
        p.AddWithValue("$titol", t.Title);
        p.AddWithValue("$album", t.Album);
        p.AddWithValue("$any_edicio", t.Year);
        p.AddWithValue("$durada_ms", (object?)t.DurationMs ?? DBNull.Value);
        p.AddWithValue("$inici_ms", (object?)t.StartMs ?? DBNull.Value);
        p.AddWithValue("$mescla_ms", (object?)t.MixMs ?? DBNull.Value);
        p.AddWithValue("$final_ms", (object?)t.EndMs ?? DBNull.Value);
        p.AddWithValue("$punts_origen", t.CueSource is { } cs ? ToText(cs) : DBNull.Value);
        p.AddWithValue("$data_inici", t.ValidFrom is { } from ? DbTime.ToText(from) : DBNull.Value);
        p.AddWithValue("$data_fi", t.ValidUntil is { } until ? DbTime.ToText(until) : DBNull.Value);
        p.AddWithValue("$activa", t.Enabled ? 1 : 0);
        p.AddWithValue("$estat_fitxer", ToText(t.FileState));
        p.AddWithValue("$prioritat", t.Priority);
        p.AddWithValue("$reproduccions", t.PlayCount);
        p.AddWithValue("$darrera_reproduccio", t.LastPlayed is { } lp ? DbTime.ToText(lp) : DBNull.Value);
        p.AddWithValue("$fitxer_mida", (object?)t.FileSize ?? DBNull.Value);
        p.AddWithValue("$fitxer_modificat", t.FileModified is { } fm ? DbTime.ToText(fm) : DBNull.Value);
        p.AddWithValue("$radiodj_id", (object?)t.RadioDjId ?? DBNull.Value);
        p.AddWithValue("$notes", (object?)t.Notes ?? DBNull.Value);
    }

    private IReadOnlyList<Track> QueryTracks(string where, object value)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT id, {TrackColumns} FROM pista {where};");
            command.Parameters.AddWithValue("$v", value);
            using var r = command.ExecuteReader();
            var list = new List<Track>();
            while (r.Read())
            {
                list.Add(new Track
                {
                    Id = r.GetInt64(0),
                    Path = r.GetString(1),
                    Type = ParseType(r.GetString(2)),
                    VariableDuration = r.GetInt64(3) == 1,
                    SubcategoryId = NullableLong(r, 4),
                    Artist = r.GetString(5),
                    Title = r.GetString(6),
                    Album = r.GetString(7),
                    Year = r.GetString(8),
                    DurationMs = NullableLong(r, 9),
                    StartMs = NullableLong(r, 10),
                    MixMs = NullableLong(r, 11),
                    EndMs = NullableLong(r, 12),
                    CueSource = r.IsDBNull(13) ? null : ParseCueSource(r.GetString(13)),
                    ValidFrom = r.IsDBNull(14) ? null : DbTime.ParseDate(r.GetString(14)),
                    ValidUntil = r.IsDBNull(15) ? null : DbTime.ParseDate(r.GetString(15)),
                    Enabled = r.GetInt64(16) == 1,
                    FileState = ParseFileState(r.GetString(17)),
                    Priority = r.GetDouble(18),
                    PlayCount = r.GetInt64(19),
                    LastPlayed = r.IsDBNull(20) ? null : DbTime.ParseInstant(r.GetString(20)),
                    FileSize = NullableLong(r, 21),
                    FileModified = r.IsDBNull(22) ? null : DbTime.ParseInstant(r.GetString(22)),
                    RadioDjId = NullableLong(r, 23),
                    Notes = r.IsDBNull(24) ? null : r.GetString(24),
                });
            }
            return list;
        }
    }

    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
}
