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

/// <summary>
/// Una cerca a la biblioteca (fase 3, pas 3a; D-079): les paraules (totes han de sortir a l'artista, el títol, l'àlbum o
/// la ruta, sense accents ni majúscules) i, opcionalment, una categoria, una subcategoria i una etiqueta.
/// </summary>
public sealed record TrackQuery
{
    public string Text { get; init; } = "";
    public long? CategoryId { get; init; }
    public long? SubcategoryId { get; init; }
    public long? TagId { get; init; }
    /// <summary>Només les pistes d'aquest tipus (p. ex. els streams, per a l'acció STREAM).</summary>
    public TrackType? Type { get; init; }
    /// <summary>Només les pistes en aquest estat (gestor de pistes, fase 3, pas 7a).</summary>
    public TrackStateFilter State { get; init; } = TrackStateFilter.All;
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>Filtre per estat del gestor de pistes (fase 3, pas 7a).</summary>
public enum TrackStateFilter
{
    All,
    /// <summary>Amb el fitxer que falta.</summary>
    Missing,
    /// <summary>Retirades d'una bossa renovable.</summary>
    Retired,
    /// <summary>Desactivades a mà.</summary>
    Disabled,
    /// <summary>Sense subcategoria.</summary>
    Unclassified,
}

/// <summary>Quantes pistes hi ha en cada estat que mira el gestor de pistes.</summary>
public sealed record TrackStateCounts(int All, int Missing, int Retired, int Disabled, int Unclassified);

/// <summary>Una subcategoria amb la seva categoria.</summary>
public sealed record SubcategoryRef(long Id, string Name, long CategoryId, string CategoryName);

/// <summary>
/// La carpeta vigilada d'una subcategoria (fase 3, pas 7c; D-026, D-033, D-090) i com queden les pistes noves que
/// s'hi importen soles. <c>Folder</c> null = no en té.
/// </summary>
public sealed record FolderSettings(
    long SubcategoryId, string Name, string? Folder, bool Renewable,
    TrackType DefaultType, IReadOnlyList<string> DefaultTags, bool DefaultVariableDuration);

/// <summary>Què ha fet la vigilància d'una carpeta amb un fitxer (registre_carpetes).</summary>
public enum FolderAction { Imported, Recovered, Missing, Deleted, Updated, Error, Folder }

/// <summary>Una línia del registre de les carpetes vigilades.</summary>
public sealed record FolderLogEntry(long Id, DateTimeOffset Time, long? SubcategoryId, string Subcategory, string Path, FolderAction Action, string? Detail);

/// <summary>Un esdeveniment que fa servir una pista (en una acció PISTA o AUXILIAR).</summary>
public sealed record TrackUse(long EventId, string EventName);

/// <summary>Una opció dels filtres de la cerca, amb quantes pistes té.</summary>
public sealed record FilterOption(long Id, string Name, int Count);

/// <summary>Una categoria amb les seves subcategories.</summary>
public sealed record CategoryFilter(long Id, string Name, int Count, IReadOnlyList<FilterOption> Subcategories);

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

    /// <summary>
    /// La pista pot sonar aquest dia segons les seves dates d'inici i de fi (D-110): l'AutoDJ i els esdeveniments no fan
    /// sonar les que encara no han començat o ja s'han acabat.
    /// </summary>
    public bool ValidOn(DateOnly date) => (ValidFrom is not { } from || date >= from) && (ValidUntil is not { } until || date <= until);
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

    /// <summary>Crea una categoria (gestor de pistes, fase 3, pas 7a). Retorna l'id.</summary>
    /// <exception cref="DatabaseException">Si ja n'hi ha una amb aquest nom.</exception>
    public long CreateCategory(string name) =>
        InsertNamed("INSERT INTO categoria (nom) VALUES ($nom); SELECT last_insert_rowid();", name, null, $"Ja hi ha una categoria «{name}».");

    /// <summary>Crea una subcategoria dins d'una categoria. Retorna l'id.</summary>
    /// <exception cref="DatabaseException">Si la categoria ja en té una amb aquest nom, o la categoria no existeix.</exception>
    public long CreateSubcategory(long categoryId, string name) =>
        InsertNamed("INSERT INTO subcategoria (categoria_id, nom) VALUES ($cat, $nom); SELECT last_insert_rowid();", name, categoryId,
            $"Aquesta categoria ja té una subcategoria «{name}» (o la categoria no existeix).");

    /// <summary>Canvia el nom d'una categoria. Les rotacions i els esdeveniments hi apunten per id: no cal tocar-los.</summary>
    public void RenameCategory(long id, string name) =>
        RenameNamed("UPDATE categoria SET nom = $nom WHERE id = $id;", id, name, $"Ja hi ha una categoria «{name}».", "categoria");

    /// <summary>Canvia el nom d'una subcategoria.</summary>
    public void RenameSubcategory(long id, string name) =>
        RenameNamed("UPDATE subcategoria SET nom = $nom WHERE id = $id;", id, name, $"Aquesta categoria ja té una subcategoria «{name}».", "subcategoria");

    private long InsertNamed(string sql, string name, long? categoryId, string duplicate)
    {
        lock (database.Sync)
        {
            using var command = database.Command(sql);
            command.Parameters.AddWithValue("$nom", name);
            if (categoryId is { } cat) command.Parameters.AddWithValue("$cat", cat);
            try
            {
                return (long)command.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // restricció: nom repetit o categoria que no existeix
            {
                throw new DatabaseException(duplicate, ex);
            }
        }
    }

    private void RenameNamed(string sql, long id, string name, string duplicate, string what)
    {
        lock (database.Sync)
        {
            using var command = database.Command(sql);
            command.Parameters.AddWithValue("$nom", name);
            command.Parameters.AddWithValue("$id", id);
            try
            {
                if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la {what} {id}.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                throw new DatabaseException(duplicate, ex);
            }
        }
    }

    // --- Carpetes vigilades (fase 3, pas 7c) ---

    private const string FolderColumns =
        "s.id, c.nom || ' / ' || s.nom, s.carpeta, s.renovable, s.tipus_defecte, s.etiquetes_defecte, s.durada_variable_defecte";

    /// <summary>La configuració de la carpeta d'una subcategoria, o null si la subcategoria no existeix.</summary>
    public FolderSettings? GetFolderSettings(long subcategoryId) =>
        QueryFolders("WHERE s.id = $id", subcategoryId).FirstOrDefault();

    /// <summary>Les subcategories que tenen carpeta vigilada.</summary>
    public IReadOnlyList<FolderSettings> WatchedFolders() =>
        QueryFolders("WHERE s.carpeta IS NOT NULL AND s.carpeta <> '' ORDER BY c.nom COLLATE NOCASE, s.nom COLLATE NOCASE", null);

    /// <summary>Desa la carpeta d'una subcategoria (null = deixa de vigilar-ne cap) i com queden les pistes noves.</summary>
    public void SetFolderSettings(long subcategoryId, string? folder, bool renewable, TrackType defaultType,
        IEnumerable<string> defaultTags, bool defaultVariableDuration)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "UPDATE subcategoria SET carpeta = $carpeta, renovable = $renovable, tipus_defecte = $tipus, " +
                "etiquetes_defecte = $etiquetes, durada_variable_defecte = $variable WHERE id = $id;");
            var tags = defaultTags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            command.Parameters.AddWithValue("$carpeta", string.IsNullOrWhiteSpace(folder) ? DBNull.Value : folder);
            command.Parameters.AddWithValue("$renovable", renewable ? 1 : 0);
            command.Parameters.AddWithValue("$tipus", ToText(defaultType));
            command.Parameters.AddWithValue("$etiquetes", tags.Count > 0 ? string.Join(", ", tags) : DBNull.Value);
            command.Parameters.AddWithValue("$variable", defaultVariableDuration ? 1 : 0);
            command.Parameters.AddWithValue("$id", subcategoryId);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la subcategoria {subcategoryId}.");
        }
    }

    private IReadOnlyList<FolderSettings> QueryFolders(string where, long? id)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {FolderColumns} FROM subcategoria s JOIN categoria c ON c.id = s.categoria_id {where};");
            if (id is { } value) command.Parameters.AddWithValue("$id", value);
            using var r = command.ExecuteReader();
            var list = new List<FolderSettings>();
            while (r.Read())
            {
                list.Add(new FolderSettings(
                    r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt64(3) == 1,
                    r.IsDBNull(4) ? TrackType.Music : ParseType(r.GetString(4)),
                    r.IsDBNull(5) ? [] : r.GetString(5).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    r.GetInt64(6) == 1));
            }
            return list;
        }
    }

    private static readonly (FolderAction Action, string Text)[] FolderActionNames =
    [
        (FolderAction.Imported, "IMPORTADA"), (FolderAction.Recovered, "RECUPERADA"), (FolderAction.Missing, "FALTA"),
        (FolderAction.Deleted, "ESBORRADA"), (FolderAction.Updated, "ACTUALITZADA"), (FolderAction.Error, "ERROR"),
        (FolderAction.Folder, "CARPETA"),
    ];

    /// <summary>Anota una línia al registre de les carpetes vigilades.</summary>
    public void AddFolderLog(long? subcategoryId, string subcategory, string path, FolderAction action, string? detail = null)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO registre_carpetes (moment, subcategoria_id, subcategoria, ruta, accio, detall) VALUES ($moment, $sub, $nom, $ruta, $accio, $detall);");
            command.Parameters.AddWithValue("$moment", DbTime.ToText(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$sub", (object?)subcategoryId ?? DBNull.Value);
            command.Parameters.AddWithValue("$nom", subcategory);
            command.Parameters.AddWithValue("$ruta", path);
            command.Parameters.AddWithValue("$accio", FolderActionNames.First(a => a.Action == action).Text);
            command.Parameters.AddWithValue("$detall", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>El registre de les carpetes vigilades, els més recents primer (d'una subcategoria o de totes).</summary>
    public IReadOnlyList<FolderLogEntry> FolderLog(int limit, long? subcategoryId = null)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT id, moment, subcategoria_id, subcategoria, ruta, accio, detall FROM registre_carpetes " +
                (subcategoryId is null ? "" : "WHERE subcategoria_id = $sub ") + "ORDER BY id DESC LIMIT $limit;");
            if (subcategoryId is { } sub) command.Parameters.AddWithValue("$sub", sub);
            command.Parameters.AddWithValue("$limit", limit);
            using var r = command.ExecuteReader();
            var list = new List<FolderLogEntry>();
            while (r.Read())
            {
                list.Add(new FolderLogEntry(r.GetInt64(0), DbTime.ParseInstant(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2),
                    r.GetString(3), r.GetString(4), FolderActionNames.First(a => a.Text == r.GetString(5)).Action, r.IsDBNull(6) ? null : r.GetString(6)));
            }
            return list;
        }
    }

    /// <summary>Els noms de les rotacions amb algun pas d'aquesta subcategoria (per avisar abans de buidar-la).</summary>
    public IReadOnlyList<string> RotationsUsingSubcategory(long subcategoryId)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT DISTINCT r.nom FROM rotacio_pas p JOIN rotacio r ON r.id = p.rotacio_id WHERE p.subcategoria_id = $id ORDER BY r.nom COLLATE NOCASE;");
            command.Parameters.AddWithValue("$id", subcategoryId);
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    /// <summary>Una subcategoria amb la seva categoria, o null si no existeix.</summary>
    public SubcategoryRef? GetSubcategory(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT s.id, s.nom, c.id, c.nom FROM subcategoria s JOIN categoria c ON c.id = s.categoria_id WHERE s.id = $id;");
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            return r.Read() ? new SubcategoryRef(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3)) : null;
        }
    }

    /// <summary>«Categoria / Subcategoria» d'una subcategoria (per descriure-la a la interfície), o null si no existeix.</summary>
    public string? SubcategoryName(long subcategoryId)
    {
        lock (database.Sync)
        {
            using var select = database.Command(
                "SELECT c.nom || ' / ' || s.nom FROM subcategoria s JOIN categoria c ON c.id = s.categoria_id WHERE s.id = $id;");
            select.Parameters.AddWithValue("$id", subcategoryId);
            return select.ExecuteScalar() as string;
        }
    }

    // --- Pistes ---

    private const string TrackColumns =
        "ruta, tipus, durada_variable, subcategoria_id, artista, titol, album, any_edicio, durada_ms, inici_ms, mescla_ms, final_ms, " +
        "punts_origen, data_inici, data_fi, activa, estat_fitxer, prioritat, reproduccions, darrera_reproduccio, fitxer_mida, " +
        "fitxer_modificat, radiodj_id, notes";

    /// <summary>
    /// Afegeix una pista nova, amb les etiquetes si n'hi ha. Tot en una sola transacció (fase 3, pas 7b): si el motor
    /// s'atura a mig fer, o hi és tot (la pista amb les etiquetes) o no hi és res. Retorna l'id.
    /// </summary>
    /// <exception cref="DatabaseException">Si ja hi ha una pista amb la mateixa ruta.</exception>
    public long AddTrack(Track track, IEnumerable<string>? tags = null)
    {
        lock (database.Sync)
        {
            string now = DbTime.ToText(DateTimeOffset.UtcNow);
            using var transaction = database.BeginTransaction();
            using var command = database.Command(
                $"INSERT INTO pista ({TrackColumns}, creada, modificada) VALUES ({ParameterList()}, $ara, $ara); SELECT last_insert_rowid();",
                transaction);
            AddTrackParameters(command, track);
            command.Parameters.AddWithValue("$ara", now);
            long id;
            try
            {
                id = (long)command.ExecuteScalar()!;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // restricció: la ruta ja hi és
            {
                throw new DatabaseException($"Ja hi ha una pista amb aquesta ruta: {track.Path}", ex);
            }
            if (tags is not null) WriteTags(id, tags, transaction);
            transaction.Commit();
            return id;
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
            try
            {
                if (command.ExecuteNonQuery() == 0)
                    throw new DatabaseException($"No existeix la pista {track.Id}.");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // restricció: la ruta ja és d'una altra pista
            {
                throw new DatabaseException($"Ja hi ha una altra pista amb aquesta ruta: {track.Path}", ex);
            }
        }
    }

    /// <summary>
    /// Els esdeveniments que fan servir una pista (accions PISTA, AUXILIAR o STREAM, D-108), per ordre de nom. Per avisar abans d'esborrar-la
    /// o de canviar-la (gestor de pistes, fase 3, pas 7a).
    /// </summary>
    public IReadOnlyList<TrackUse> EventsUsingTrack(long trackId)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT DISTINCT e.id, e.nom FROM accio a JOIN esdeveniment e ON e.id = a.esdeveniment_id " +
                "WHERE a.tipus IN ('PISTA', 'AUXILIAR', 'STREAM') AND json_valid(a.params) AND json_extract(a.params, '$.songId') = $id " +
                "ORDER BY e.nom COLLATE NOCASE, e.id;");
            command.Parameters.AddWithValue("$id", trackId);
            using var r = command.ExecuteReader();
            var list = new List<TrackUse>();
            while (r.Read()) list.Add(new TrackUse(r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>
    /// Esborra una pista de la biblioteca (el fitxer no es toca mai). L'historial la conserva (artista, títol i ruta, sense
    /// l'enllaç) i les etiquetes se'n treuen. No es pot esborrar si la fa servir algun esdeveniment.
    /// </summary>
    /// <exception cref="DatabaseException">Si no existeix o la fa servir algun esdeveniment.</exception>
    public void DeleteTrack(long id)
    {
        lock (database.Sync)
        {
            var uses = EventsUsingTrack(id);
            if (uses.Count > 0)
                throw new DatabaseException($"No es pot esborrar: la fa servir {(uses.Count == 1 ? "l'esdeveniment" : "els esdeveniments")} " +
                                            string.Join(", ", uses.Select(u => $"«{u.EventName}»")) + ".");
            using var command = database.Command("DELETE FROM pista WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la pista {id}.");
        }
    }

    /// <summary>Les rutes de les pistes amb el fitxer que falta (canvi d'arrel, fase 3, pas 7d).</summary>
    public IReadOnlyList<string> MissingPaths()
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT ruta FROM pista WHERE estat_fitxer = 'FALTA';");
            using var r = command.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>Una ruta nova per a una pista (canvi d'arrel): si el fitxer hi és, amb la mida i la data.</summary>
    public sealed record PathChange(long TrackId, string NewPath, bool Exists, long? FileSize, DateTimeOffset? FileModified);

    /// <summary>
    /// Canvia la ruta de moltes pistes alhora, <b>en una sola transacció</b> (canvi d'arrel, fase 3, pas 7d; D-092): o canvien
    /// totes o cap. Amb el fitxer, la pista queda bé (i se'n desa la mida i la data); sense, «el fitxer falta». La fitxa, els
    /// punts i l'historial no es toquen (és el mateix fitxer en un altre lloc).
    /// </summary>
    /// <exception cref="DatabaseException">Si alguna ruta nova ja és d'una altra pista (no es canvia res).</exception>
    public void ChangePaths(IEnumerable<PathChange> changes)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            string now = DbTime.ToText(DateTimeOffset.UtcNow);
            foreach (var c in changes)
            {
                using var command = database.Command(
                    "UPDATE pista SET ruta = $ruta, estat_fitxer = $estat, fitxer_mida = coalesce($mida, fitxer_mida), " +
                    "fitxer_modificat = coalesce($modificat, fitxer_modificat), modificada = $ara WHERE id = $id;", transaction);
                command.Parameters.AddWithValue("$ruta", c.NewPath);
                command.Parameters.AddWithValue("$estat", c.Exists ? "OK" : "FALTA");
                command.Parameters.AddWithValue("$mida", (object?)c.FileSize ?? DBNull.Value);
                command.Parameters.AddWithValue("$modificat", c.FileModified is { } m ? DbTime.ToText(m) : DBNull.Value);
                command.Parameters.AddWithValue("$ara", now);
                command.Parameters.AddWithValue("$id", c.TrackId);
                try
                {
                    command.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new DatabaseException($"La ruta nova ja és d'una altra pista: {c.NewPath}. No s'ha canviat res.", ex);
                }
            }
            transaction.Commit();
        }
    }

    /// <summary>Quantes pistes hi ha en cada estat que mira el gestor de pistes.</summary>
    public TrackStateCounts StateCounts()
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT count(*), " +
                "coalesce(sum(estat_fitxer = 'FALTA'), 0), coalesce(sum(estat_fitxer = 'RETIRADA'), 0), " +
                "coalesce(sum(activa = 0), 0), coalesce(sum(subcategoria_id IS NULL), 0) FROM pista;");
            using var r = command.ExecuteReader();
            r.Read();
            return new TrackStateCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }
    }

    public Track? GetTrack(long id) => QueryTracks("WHERE id = $v", id).FirstOrDefault();

    public Track? FindByPath(string path) => QueryTracks("WHERE ruta = $v", path).FirstOrDefault();

    public Track? FindByRadioDjId(long radioDjId) => QueryTracks("WHERE radiodj_id = $v", radioDjId).FirstOrDefault();

    /// <summary>
    /// Una pista de la subcategoria amb el fitxer que falta (o retirada) i el mateix nom de fitxer (sense la carpeta, sense
    /// distingir majúscules), o null. La més antiga si n'hi ha diverses. En importar una carpeta (fase 3, pas 7b), perquè
    /// una bossa que s'ha mogut (o que en un altre ordinador es veu amb una altra ruta) es recuperi en lloc de duplicar-se.
    /// </summary>
    public Track? FindMissingByFileName(long subcategoryId, string fileName) =>
        QueryTracks("WHERE subcategoria_id = $sub AND estat_fitxer IN ('FALTA', 'RETIRADA') " +
                    "AND lower(substr(ruta, -length($nom))) = lower($nom) ORDER BY id LIMIT 1",
            ("$sub", subcategoryId), ("$nom", "\\" + fileName)).FirstOrDefault();

    public IReadOnlyList<Track> TracksInSubcategory(long subcategoryId) =>
        QueryTracks("WHERE subcategoria_id = $v ORDER BY id", subcategoryId);

    /// <summary>Pistes la ruta de les quals comença per aquesta carpeta (sense distingir majúscules).</summary>
    /// <summary>
    /// Cerca pistes per text (fase 2, pas 8b): totes les paraules han de sortir a l'artista, al títol o a la ruta,
    /// sense distingir majúscules. Sense text, les primeres per ordre. Com a molt <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<Track> Search(string text, int limit) => Search(new TrackQuery { Text = text, Limit = limit });

    /// <summary>
    /// Cerca a la biblioteca (fase 3, pas 3a; D-079): sense accents ni majúscules («magazin» troba «Magazín»), amb la
    /// funció <c>sense_accents</c> que registra <see cref="Database"/>. Una cerca de «conté» mira totes les files igualment
    /// (cap índex no hi ajuda); amb unes quantes milers de pistes són pocs mil·lisegons. Per artista i títol.
    /// </summary>
    public IReadOnlyList<Track> Search(TrackQuery query)
    {
        var words = (TextSearch.Fold(query.Text) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
        var conditions = new List<string>();
        var parameters = new List<(string, object)> { ("$limit", query.Limit), ("$offset", query.Offset) };
        for (int i = 0; i < words.Length; i++)
        {
            // LIKE amb els caràcters especials escapats (%, _ i el mateix caràcter d'escapament)
            string pattern = "%" + words[i].Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%";
            conditions.Add($"(sense_accents(artista) LIKE $w{i} ESCAPE '!' OR sense_accents(titol) LIKE $w{i} ESCAPE '!' " +
                           $"OR sense_accents(album) LIKE $w{i} ESCAPE '!' OR sense_accents(ruta) LIKE $w{i} ESCAPE '!')");
            parameters.Add(($"$w{i}", pattern));
        }
        if (query.SubcategoryId is { } sub)
        {
            conditions.Add("subcategoria_id = $sub");
            parameters.Add(("$sub", sub));
        }
        else if (query.CategoryId is { } cat)
        {
            conditions.Add("subcategoria_id IN (SELECT id FROM subcategoria WHERE categoria_id = $cat)");
            parameters.Add(("$cat", cat));
        }
        if (query.TagId is { } tag)
        {
            conditions.Add("EXISTS (SELECT 1 FROM pista_etiqueta pe WHERE pe.pista_id = pista.id AND pe.etiqueta_id = $tag)");
            parameters.Add(("$tag", tag));
        }
        if (query.Type is { } type)
        {
            conditions.Add("tipus = $type");
            parameters.Add(("$type", ToText(type)));
        }
        switch (query.State)
        {
            case TrackStateFilter.Missing: conditions.Add("estat_fitxer = 'FALTA'"); break;
            case TrackStateFilter.Retired: conditions.Add("estat_fitxer = 'RETIRADA'"); break;
            case TrackStateFilter.Disabled: conditions.Add("activa = 0"); break;
            case TrackStateFilter.Unclassified: conditions.Add("subcategoria_id IS NULL"); break;
        }
        string where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        return QueryTracks($"{where} ORDER BY artista COLLATE NOCASE, titol COLLATE NOCASE, id LIMIT $limit OFFSET $offset", parameters.ToArray());
    }

    /// <summary>Les opcions dels filtres de la cerca: categories (amb les subcategories) i etiquetes, amb quantes pistes tenen.</summary>
    public (IReadOnlyList<CategoryFilter> Categories, IReadOnlyList<FilterOption> Tags) SearchFilters()
    {
        lock (database.Sync)
        {
            var subs = new List<(long CategoryId, FilterOption Option)>();
            using (var command = database.Command(
                "SELECT s.categoria_id, s.id, s.nom, (SELECT count(*) FROM pista p WHERE p.subcategoria_id = s.id) " +
                "FROM subcategoria s ORDER BY s.nom COLLATE NOCASE;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) subs.Add((r.GetInt64(0), new FilterOption(r.GetInt64(1), r.GetString(2), r.GetInt32(3))));
            }
            var categories = new List<CategoryFilter>();
            using (var command = database.Command("SELECT id, nom FROM categoria ORDER BY nom COLLATE NOCASE;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    var own = subs.Where(s => s.CategoryId == id).Select(s => s.Option).ToList();
                    categories.Add(new CategoryFilter(id, r.GetString(1), own.Sum(s => s.Count), own));
                }
            }
            var tags = new List<FilterOption>();
            using (var command = database.Command(
                "SELECT e.id, e.nom, (SELECT count(*) FROM pista_etiqueta pe WHERE pe.etiqueta_id = e.id) FROM etiqueta e ORDER BY e.nom COLLATE NOCASE;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) tags.Add(new FilterOption(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
            }
            return (categories, tags);
        }
    }

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

    /// <summary>
    /// Pistes que pot triar un pas de rotació (fase 1, pas 8): de la subcategoria (o, si el pas no en té, de qualsevol
    /// subcategoria de la categoria), amb l'etiqueta si el pas en demana, activades, amb el fitxer disponible, vigents el
    /// dia <paramref name="today"/> i que no siguin streams (els streams de les rotacions arriben a la fase 2).
    /// </summary>
    public IReadOnlyList<Track> TracksForRotationStep(RotationStep step, DateOnly today)
    {
        var where = new List<string> { AvailableCondition };
        var parameters = new List<(string, object)> { ("$avui", DbTime.ToText(today)) };
        if (step.SubcategoryId is { } sub)
        {
            where.Add("subcategoria_id = $sub");
            parameters.Add(("$sub", sub));
        }
        else if (step.CategoryId is { } cat)
        {
            where.Add("subcategoria_id IN (SELECT id FROM subcategoria WHERE categoria_id = $cat)");
            parameters.Add(("$cat", cat));
        }
        else return [];
        if (step.TagId is { } tag)
        {
            where.Add("id IN (SELECT pista_id FROM pista_etiqueta WHERE etiqueta_id = $tag)");
            parameters.Add(("$tag", tag));
        }
        return QueryTracks("WHERE " + string.Join(" AND ", where) + " ORDER BY id", parameters.ToArray());
    }

    /// <summary>Música disponible de tota la biblioteca: la reserva de l'AutoDJ quan cap pas de la rotació no troba res (mai silenci).</summary>
    public IReadOnlyList<Track> AvailableMusic(DateOnly today) =>
        QueryTracks($"WHERE tipus = 'MUSICA' AND {AvailableCondition} ORDER BY id", ("$avui", DbTime.ToText(today)));

    /// <summary>Pista que pot sonar avui: activada, amb el fitxer, vigent (paràmetre <c>$avui</c>) i que no és un stream.</summary>
    private const string AvailableCondition =
        "activa = 1 AND estat_fitxer = 'OK' AND tipus <> 'STREAM' " +
        "AND (data_inici IS NULL OR data_inici <= $avui) AND (data_fi IS NULL OR data_fi >= $avui)";

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
            WriteTags(trackId, tags, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Substitueix les etiquetes d'una pista dins d'una transacció que ja està oberta.</summary>
    private void WriteTags(long trackId, IEnumerable<string> tags, SqliteTransaction transaction)
    {
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
    }

    /// <summary>Retorna l'id de l'etiqueta amb aquest nom; si no existeix, la crea.</summary>
    public long EnsureTag(string name)
    {
        lock (database.Sync)
        {
            using (var insert = database.Command("INSERT OR IGNORE INTO etiqueta (nom) VALUES ($nom);"))
            {
                insert.Parameters.AddWithValue("$nom", name.Trim());
                insert.ExecuteNonQuery();
            }
            using var select = database.Command("SELECT id FROM etiqueta WHERE nom = $nom;");
            select.Parameters.AddWithValue("$nom", name.Trim());
            return (long)select.ExecuteScalar()!;
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

    /// <summary>
    /// Anota que una pista ha començat a sonar i n'actualitza la darrera reproducció. Retorna l'id de l'entrada (per tancar-la
    /// quan s'acabi). Una pista amb <c>Id</c> 0 (un fitxer que no és a la biblioteca) s'anota sense <c>pista_id</c>.
    /// </summary>
    public long AddHistory(Track track, DateTimeOffset started, QueueOrigin origin)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO historial (pista_id, inici, origen, artista, titol, ruta) VALUES ($p, $inici, $origen, $artista, $titol, $ruta); " +
                "UPDATE pista SET reproduccions = reproduccions + 1, darrera_reproduccio = $inici WHERE id = $p; " +
                "SELECT last_insert_rowid();");
            command.Parameters.AddWithValue("$p", track.Id > 0 ? track.Id : DBNull.Value);
            command.Parameters.AddWithValue("$inici", DbTime.ToText(started));
            command.Parameters.AddWithValue("$origen", ToText(origin));
            command.Parameters.AddWithValue("$artista", track.Artist);
            command.Parameters.AddWithValue("$titol", track.Title);
            command.Parameters.AddWithValue("$ruta", track.Path);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Tanca una entrada de l'historial: quant ha sonat i si ha arribat al final previst.</summary>
    /// <summary>
    /// Tanca les entrades de l'historial que es van quedar obertes (el motor es va aturar de cop mentre sonaven): com a
    /// tallades i amb durada 0, que vol dir «no se sap quant va sonar» (entremig el motor pot haver estat aturat una
    /// estona: el temps fins a l'entrada següent no serviria). Retorna quantes (fase 3, pas 2a).
    /// </summary>
    public int CloseOpenHistory()
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "UPDATE historial SET sencera = 0, durada_ms = 0 WHERE durada_ms IS NULL;");
            return command.ExecuteNonQuery();
        }
    }

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
            while (reader.Read()) list.Add(ReadHistoryEntry(reader));
            return list;
        }
    }

    /// <summary>
    /// Cerca a l'historial (fase 1, pas 9): les entrades més recents primer, com a molt <paramref name="limit"/>.
    /// <paramref name="from"/> (inclosa) i <paramref name="to"/> (exclosa) són opcionals; <paramref name="text"/>, si n'hi ha,
    /// ha de ser a l'artista o al títol (sense distingir majúscules; els accents sí que compten).
    /// </summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(DateTimeOffset? from, DateTimeOffset? to, string? text, int limit)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (from is { } f) { where.Add("inici >= $from"); parameters.Add(("$from", DbTime.ToText(f))); }
        if (to is { } t) { where.Add("inici < $to"); parameters.Add(("$to", DbTime.ToText(t))); }
        if (!string.IsNullOrWhiteSpace(text))
        {
            // Els caràcters especials de LIKE (%, _ i \) es busquen tal qual
            string pattern = "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            where.Add("(artista LIKE $text ESCAPE '\\' OR titol LIKE $text ESCAPE '\\')");
            parameters.Add(("$text", pattern));
        }

        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT id, pista_id, inici, durada_ms, sencera, origen, artista, titol, ruta FROM historial " +
                (where.Count > 0 ? "WHERE " + string.Join(" AND ", where) + " " : "") +
                "ORDER BY inici DESC, id DESC LIMIT $limit;");
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var list = new List<HistoryEntry>();
            while (reader.Read()) list.Add(ReadHistoryEntry(reader));
            return list;
        }
    }

    private static HistoryEntry ReadHistoryEntry(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetInt64(1),
        DbTime.ParseInstant(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4) == 1,
        ParseOrigin(reader.GetString(5)),
        reader.GetString(6), reader.GetString(7), reader.GetString(8));

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

    private IReadOnlyList<Track> QueryTracks(string where, object value) => QueryTracks(where, ("$v", value));

    private IReadOnlyList<Track> QueryTracks(string where, params (string Name, object Value)[] parameters)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT id, {TrackColumns} FROM pista {where};");
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
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
