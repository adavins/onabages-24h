using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Vigència de les pistes per a la cerca (pestanya Pistes, D-111), segons les seves dates d'inici i de fi.</summary>
public enum TrackValidity
{
    /// <summary>Totes.</summary>
    All,
    /// <summary>Poden sonar avui (sense dates, o avui és dins de les dates).</summary>
    Valid,
    /// <summary>La data de fi ja ha passat.</summary>
    Expired,
    /// <summary>La data d'inici encara no ha arribat.</summary>
    NotYet,
    /// <summary>Tenen dates i el seu període toca l'interval <see cref="TrackQuery.ValidFrom"/>–<see cref="TrackQuery.ValidTo"/>.</summary>
    Overlaps,
}

/// <summary>Per quina columna s'ordena la llista de pistes (D-111).</summary>
public enum TrackSort
{
    /// <summary>Artista i títol (el de sempre).</summary>
    Artist,
    Title,
    Type,
    Duration,
    Plays,
    LastPlayed,
    ValidUntil,
}

/// <summary>Una etiqueta al gestor d'etiquetes (D-111): quantes pistes la tenen i quines rotacions la fan servir.</summary>
public sealed record TagInfo(long Id, string Name, int Tracks, IReadOnlyList<string> Rotations);

/// <summary>
/// Gestió de la biblioteca per a la pestanya Pistes (D-111): la cerca amb tots els filtres i el recompte, els canvis en
/// bloc, esborrar categories i subcategories buides, i el gestor d'etiquetes.
/// </summary>
public sealed partial class Library
{
    // --- Cerca ---

    /// <summary>La condició WHERE (sense la paraula) i els paràmetres d'una cerca.</summary>
    private static (string Where, List<(string, object)> Parameters) SearchCondition(TrackQuery query)
    {
        var words = (TextSearch.Fold(query.Text) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray();
        var conditions = new List<string>();
        var parameters = new List<(string, object)>();
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
        // Dins d'esdeveniments: alguna acció PISTA, AUXILIAR o STREAM hi apunta
        if (query.InEvents is { } inEvents)
        {
            conditions.Add((inEvents ? "" : "NOT ") + "EXISTS (SELECT 1 FROM accio a WHERE a.tipus IN ('PISTA', 'AUXILIAR', 'STREAM') " +
                           "AND json_valid(a.params) AND json_extract(a.params, '$.songId') = pista.id)");
        }
        // Vigència (les dates són 'AAAA-MM-DD': es comparen com a text)
        string today = DbTime.ToText(query.Today);
        switch (query.Validity)
        {
            case TrackValidity.Valid:
                conditions.Add("(data_inici IS NULL OR data_inici <= $avui) AND (data_fi IS NULL OR data_fi >= $avui)");
                parameters.Add(("$avui", today));
                break;
            case TrackValidity.Expired:
                conditions.Add("data_fi < $avui");
                parameters.Add(("$avui", today));
                break;
            case TrackValidity.NotYet:
                conditions.Add("data_inici > $avui");
                parameters.Add(("$avui", today));
                break;
            case TrackValidity.Overlaps:
                conditions.Add("(data_inici IS NOT NULL OR data_fi IS NOT NULL)");
                if (query.ValidTo is { } to)
                {
                    conditions.Add("(data_inici IS NULL OR data_inici <= $vto)");
                    parameters.Add(("$vto", DbTime.ToText(to)));
                }
                if (query.ValidFrom is { } from)
                {
                    conditions.Add("(data_fi IS NULL OR data_fi >= $vfrom)");
                    parameters.Add(("$vfrom", DbTime.ToText(from)));
                }
                break;
        }
        // Última reproducció (instants UTC en text ISO: es comparen com a text)
        if (query.NeverPlayed) conditions.Add("darrera_reproduccio IS NULL");
        if (query.PlayedFrom is { } playedFrom)
        {
            conditions.Add("darrera_reproduccio >= $pfrom");
            parameters.Add(("$pfrom", DbTime.ToText(playedFrom)));
        }
        if (query.PlayedBefore is { } playedBefore)
        {
            conditions.Add("darrera_reproduccio < $pto");
            parameters.Add(("$pto", DbTime.ToText(playedBefore)));
        }
        return (conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "", parameters);
    }

    /// <summary>L'ordre de la llista: la columna triada i, a igualtat, artista i títol. Els buits (null), sempre al final.</summary>
    private static string SearchOrder(TrackQuery query)
    {
        string dir = query.Descending ? "DESC" : "ASC";
        string column = query.Sort switch
        {
            TrackSort.Title => $"titol COLLATE NOCASE {dir}",
            TrackSort.Type => $"tipus {dir}",
            TrackSort.Duration => $"durada_ms IS NULL, durada_ms {dir}",
            TrackSort.Plays => $"reproduccions {dir}",
            TrackSort.LastPlayed => $"darrera_reproduccio IS NULL, darrera_reproduccio {dir}",
            TrackSort.ValidUntil => $"data_fi IS NULL, data_fi {dir}",
            _ => $"artista COLLATE NOCASE {dir}, titol COLLATE NOCASE {dir}",
        };
        return $"ORDER BY {column}, artista COLLATE NOCASE, titol COLLATE NOCASE, id";
    }

    /// <summary>Quantes pistes compleixen la cerca (sense límit).</summary>
    public int Count(TrackQuery query)
    {
        var (where, parameters) = SearchCondition(query);
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT count(*) FROM pista {where};");
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>Els ids de les pistes que compleixen la cerca, en el mateix ordre (per seleccionar-les totes), com a molt <paramref name="max"/>.</summary>
    public IReadOnlyList<long> SearchIds(TrackQuery query, int max)
    {
        var (where, parameters) = SearchCondition(query);
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT id FROM pista {where} {SearchOrder(query)} LIMIT $max;");
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.Parameters.AddWithValue("$max", max);
            using var r = command.ExecuteReader();
            var ids = new List<long>();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }
    }

    // --- Canvis en bloc ---

    /// <summary>Canvia la subcategoria de moltes pistes alhora (null = sense subcategoria). Retorna quantes han canviat.</summary>
    /// <exception cref="DatabaseException">Si la subcategoria no existeix.</exception>
    public int SetSubcategory(IReadOnlyCollection<long> trackIds, long? subcategoryId)
    {
        if (subcategoryId is { } sub && GetSubcategory(sub) is null) throw new DatabaseException($"No existeix la subcategoria {sub}.");
        return UpdateMany(trackIds, "subcategoria_id = $valor", subcategoryId is { } s ? s : DBNull.Value);
    }

    /// <summary>Activa o desactiva moltes pistes alhora. Retorna quantes han canviat.</summary>
    public int SetEnabled(IReadOnlyCollection<long> trackIds, bool enabled) =>
        UpdateMany(trackIds, "activa = $valor", enabled ? 1 : 0);

    private int UpdateMany(IReadOnlyCollection<long> trackIds, string set, object value)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            int changed = 0;
            foreach (long id in trackIds.Distinct())
            {
                using var command = database.Command($"UPDATE pista SET {set}, modificada = $ara WHERE id = $id;", transaction);
                command.Parameters.AddWithValue("$valor", value);
                command.Parameters.AddWithValue("$ara", DbTime.ToText(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$id", id);
                changed += command.ExecuteNonQuery();
            }
            transaction.Commit();
            return changed;
        }
    }

    /// <summary>Posa una etiqueta (es crea si no existeix) a moltes pistes alhora. Retorna a quantes s'ha afegit.</summary>
    public int AddTag(IReadOnlyCollection<long> trackIds, string tag)
    {
        long tagId = EnsureTag(tag.Trim());
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            int added = 0;
            foreach (long id in trackIds.Distinct())
            {
                using var command = database.Command(
                    "INSERT OR IGNORE INTO pista_etiqueta (pista_id, etiqueta_id) SELECT id, $tag FROM pista WHERE id = $id;", transaction);
                command.Parameters.AddWithValue("$tag", tagId);
                command.Parameters.AddWithValue("$id", id);
                added += command.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        }
    }

    /// <summary>Treu una etiqueta de moltes pistes alhora (l'etiqueta continua existint). Retorna de quantes s'ha tret.</summary>
    public int RemoveTag(IReadOnlyCollection<long> trackIds, long tagId)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            int removed = 0;
            foreach (long id in trackIds.Distinct())
            {
                using var command = database.Command("DELETE FROM pista_etiqueta WHERE pista_id = $id AND etiqueta_id = $tag;", transaction);
                command.Parameters.AddWithValue("$tag", tagId);
                command.Parameters.AddWithValue("$id", id);
                removed += command.ExecuteNonQuery();
            }
            transaction.Commit();
            return removed;
        }
    }

    /// <summary>Quantes vegades ha sonat una pista des d'un instant (per avisar abans d'esborrar-la: certificats, D-011).</summary>
    public int PlaysSince(long trackId, DateTimeOffset since)
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT count(*) FROM historial WHERE pista_id = $id AND inici >= $des;");
            command.Parameters.AddWithValue("$id", trackId);
            command.Parameters.AddWithValue("$des", DbTime.ToText(since));
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    // --- Esborrar categories i subcategories (només buides) ---

    /// <summary>
    /// Per què no es pot esborrar una subcategoria des de la biblioteca: té pistes, la fan servir passos de rotació o bosses,
    /// o té carpeta vigilada. Buit = es pot esborrar (la configuració i els esdeveniments els mira qui crida).
    /// </summary>
    public IReadOnlyList<string> SubcategoryBlockers(long subcategoryId)
    {
        var reasons = new List<string>();
        lock (database.Sync)
        {
            int tracks = Scalar("SELECT count(*) FROM pista WHERE subcategoria_id = $id;", subcategoryId);
            if (tracks > 0) reasons.Add($"té {tracks} {(tracks == 1 ? "pista" : "pistes")}");
            var rotations = Names("SELECT DISTINCT r.nom FROM rotacio_pas p JOIN rotacio r ON r.id = p.rotacio_id WHERE p.subcategoria_id = $id ORDER BY r.nom;", subcategoryId);
            if (rotations.Count > 0) reasons.Add($"la fan servir les rotacions {Quoted(rotations)}");
            var bags = Names("SELECT nom FROM bossa WHERE subcategoria_id = $id ORDER BY nom;", subcategoryId);
            if (bags.Count > 0) reasons.Add($"la fan servir les bosses {Quoted(bags)}");
            if (Names("SELECT carpeta FROM subcategoria WHERE id = $id AND carpeta IS NOT NULL;", subcategoryId).Count > 0)
                reasons.Add("té una carpeta vigilada (treu-la abans)");
        }
        return reasons;
    }

    /// <summary>Per què no es pot esborrar una categoria: té subcategories o la fan servir passos de rotació. Buit = es pot.</summary>
    public IReadOnlyList<string> CategoryBlockers(long categoryId)
    {
        var reasons = new List<string>();
        lock (database.Sync)
        {
            int subs = Scalar("SELECT count(*) FROM subcategoria WHERE categoria_id = $id;", categoryId);
            if (subs > 0) reasons.Add($"té {subs} {(subs == 1 ? "subcategoria" : "subcategories")} (esborra-les abans)");
            var rotations = Names("SELECT DISTINCT r.nom FROM rotacio_pas p JOIN rotacio r ON r.id = p.rotacio_id WHERE p.categoria_id = $id ORDER BY r.nom;", categoryId);
            if (rotations.Count > 0) reasons.Add($"la fan servir les rotacions {Quoted(rotations)}");
        }
        return reasons;
    }

    /// <summary>Esborra una subcategoria. Qui crida ha comprovat abans que es pot (<see cref="SubcategoryBlockers"/>).</summary>
    public void DeleteSubcategory(long id)
    {
        var blockers = SubcategoryBlockers(id);
        if (blockers.Count > 0) throw new DatabaseException("No es pot esborrar la subcategoria: " + string.Join("; ", blockers) + ".");
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM subcategoria WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la subcategoria {id}.");
        }
    }

    /// <summary>Esborra una categoria buida (sense subcategories).</summary>
    public void DeleteCategory(long id)
    {
        var blockers = CategoryBlockers(id);
        if (blockers.Count > 0) throw new DatabaseException("No es pot esborrar la categoria: " + string.Join("; ", blockers) + ".");
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM categoria WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la categoria {id}.");
        }
    }

    // --- Gestor d'etiquetes ---

    /// <summary>Totes les etiquetes, per nom, amb quantes pistes la tenen i quines rotacions la fan servir.</summary>
    public IReadOnlyList<TagInfo> Tags()
    {
        lock (database.Sync)
        {
            var list = new List<(long Id, string Name, int Tracks)>();
            using (var command = database.Command(
                "SELECT e.id, e.nom, (SELECT count(*) FROM pista_etiqueta pe WHERE pe.etiqueta_id = e.id) FROM etiqueta e ORDER BY e.nom COLLATE NOCASE;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
            }
            return list.Select(t => new TagInfo(t.Id, t.Name, t.Tracks, RotationsUsingTag(t.Id))).ToList();
        }
    }

    private IReadOnlyList<string> RotationsUsingTag(long tagId) =>
        Names("SELECT DISTINCT r.nom FROM rotacio_pas p JOIN rotacio r ON r.id = p.rotacio_id WHERE p.etiqueta_id = $id ORDER BY r.nom;", tagId);

    /// <summary>Canvia el nom d'una etiqueta. Les rotacions hi apunten per id: continuen funcionant.</summary>
    /// <exception cref="DatabaseException">Si ja n'hi ha una altra amb aquest nom (cal fusionar-les) o no existeix.</exception>
    public void RenameTag(long id, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new DatabaseException("L'etiqueta ha de tenir nom.");
        lock (database.Sync)
        {
            if (ExistingTagName(name, id) is { } other) throw new DatabaseException($"Ja hi ha una etiqueta «{other}»: per ajuntar-les, fusiona-les.");
            using var command = database.Command("UPDATE etiqueta SET nom = $nom WHERE id = $id;");
            command.Parameters.AddWithValue("$nom", name);
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix l'etiqueta {id}.");
        }
    }

    /// <summary>
    /// Fusiona dues etiquetes (p. ex. «catala» dins de «català»): les pistes i els passos de rotació de
    /// <paramref name="fromId"/> passen a <paramref name="intoId"/>, i <paramref name="fromId"/> s'esborra. Tot o res.
    /// </summary>
    public void MergeTags(long fromId, long intoId)
    {
        if (fromId == intoId) throw new DatabaseException("No es pot fusionar una etiqueta amb ella mateixa.");
        lock (database.Sync)
        {
            if (Scalar("SELECT count(*) FROM etiqueta WHERE id IN ($id, " + intoId + ");", fromId) < 2)
                throw new DatabaseException("Alguna de les dues etiquetes ja no existeix.");
            using var transaction = database.BeginTransaction();
            foreach (string sql in new[]
            {
                "INSERT OR IGNORE INTO pista_etiqueta (pista_id, etiqueta_id) SELECT pista_id, $into FROM pista_etiqueta WHERE etiqueta_id = $from;",
                "DELETE FROM pista_etiqueta WHERE etiqueta_id = $from;",
                "UPDATE rotacio_pas SET etiqueta_id = $into WHERE etiqueta_id = $from;",
                "DELETE FROM etiqueta WHERE id = $from;",
            })
            {
                using var command = database.Command(sql, transaction);
                command.Parameters.AddWithValue("$from", fromId);
                command.Parameters.AddWithValue("$into", intoId);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>Esborra una etiqueta (i la treu de les pistes que la tenen).</summary>
    /// <exception cref="DatabaseException">Si la fa servir alguna rotació (deixaria de funcionar) o no existeix.</exception>
    public void DeleteTag(long id)
    {
        var rotations = RotationsUsingTag(id);
        if (rotations.Count > 0)
            throw new DatabaseException($"No es pot esborrar: la fan servir les rotacions {Quoted(rotations)}. Canvia-les abans, o fusiona l'etiqueta amb una altra.");
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM etiqueta WHERE id = $id;"); // pista_etiqueta: ON DELETE CASCADE
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix l'etiqueta {id}.");
        }
    }

    /// <summary>
    /// El nom d'una etiqueta que ja existeix i que és la mateixa sense distingir majúscules, també les accentuades
    /// («CATALÀ» → «català»; SQLite només iguala les de l'alfabet anglès), o null. Els accents sí que compten: «catala» i
    /// «català» són dues etiquetes (es fusionen amb el gestor).
    /// </summary>
    private string? ExistingTagName(string name, long exceptId = 0, SqliteTransaction? transaction = null)
    {
        using var command = database.Command("SELECT id, nom FROM etiqueta;", transaction);
        using var r = command.ExecuteReader();
        while (r.Read())
        {
            if (r.GetInt64(0) != exceptId && string.Equals(r.GetString(1), name, StringComparison.InvariantCultureIgnoreCase)) return r.GetString(1);
        }
        return null;
    }

    private string? ExistingTagName(string name, SqliteTransaction transaction) => ExistingTagName(name, 0, transaction);

    // --- Ajudes ---

    private int Scalar(string sql, long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command(sql);
            command.Parameters.AddWithValue("$id", id);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private IReadOnlyList<string> Names(string sql, long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command(sql);
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    private static string Quoted(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"«{n}»"));
}
