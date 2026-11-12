namespace OnaBages24.Data;

/// <summary>Com tria la pista un pas de rotació.</summary>
public enum RotationSelection
{
    /// <summary>La que fa més temps que no sona (les que no han sonat mai, primer).</summary>
    LeastRecent,
    /// <summary>La que ha sonat menys vegades (selType 2 de RadioDJ, «La menys reproduïda»).</summary>
    LeastPlayed,
    /// <summary>Una qualsevol, a l'atzar.</summary>
    Random,
}

/// <summary>
/// Un pas d'una rotació: d'on es treu la pista (subcategoria o, si no n'hi ha, categoria; i, si cal, una etiqueta, D-027)
/// i com es tria. Els noms són només per mostrar-los.
/// </summary>
public sealed record RotationStep
{
    public long Id { get; init; }
    public int Order { get; init; }
    public long? CategoryId { get; init; }
    public long? SubcategoryId { get; init; }
    public long? TagId { get; init; }
    public RotationSelection Selection { get; init; } = RotationSelection.LeastRecent;
    /// <summary>S'apliquen els criteris de repetició (no repetir pista ni artista). A RadioDJ, activat per a la música.</summary>
    public bool RepeatRule { get; init; } = true;
    /// <summary>Trepitjador: sweeper a l'inici de la pista (com a RadioDJ «A l'inici»), de la categoria de sweepers activa (fase 3, pas 5).</summary>
    public bool Sweeper { get; init; }

    public string? CategoryName { get; init; }
    public string? SubcategoryName { get; init; }
    public string? TagName { get; init; }

    /// <summary>Descripció curta per als registres i la interfície, p. ex. «Sweepers / Ona Bages + SW - catala».</summary>
    public override string ToString() =>
        (SubcategoryName is null ? CategoryName ?? "?" : $"{CategoryName} / {SubcategoryName}") + (TagName is null ? "" : $" + {TagName}");
}

/// <summary>Una rotació: un cicle ordenat de passos que l'AutoDJ va repetint (D-014).</summary>
public sealed record Rotation(long Id, string Name, IReadOnlyList<RotationStep> Steps);

/// <summary>Rotacions de l'AutoDJ (taules <c>rotacio</c> i <c>rotacio_pas</c>, migració 002).</summary>
public sealed class Rotations(Database database)
{
    /// <summary>Totes les rotacions, per id, amb els passos en ordre.</summary>
    public IReadOnlyList<Rotation> List()
    {
        lock (database.Sync)
        {
            var rotations = new List<(long Id, string Name)>();
            using (var command = database.Command("SELECT id, nom FROM rotacio ORDER BY id;"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) rotations.Add((reader.GetInt64(0), reader.GetString(1)));
            }
            return rotations.Select(r => new Rotation(r.Id, r.Name, Steps(r.Id))).ToList();
        }
    }

    public Rotation? Get(long id) => List().FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Desa una rotació amb aquests passos (en aquest ordre). Si ja n'hi ha una amb el mateix nom, se'n substitueixen els passos.
    /// Retorna l'id.
    /// </summary>
    public long Save(string name, IEnumerable<RotationStep> steps, long? radioDjId = null)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            long id;
            using (var upsert = database.Command(
                "INSERT INTO rotacio (nom, radiodj_id) VALUES ($nom, $rdj) ON CONFLICT(nom) DO UPDATE SET radiodj_id = excluded.radiodj_id; " +
                "SELECT id FROM rotacio WHERE nom = $nom;", transaction))
            {
                upsert.Parameters.AddWithValue("$nom", name);
                upsert.Parameters.AddWithValue("$rdj", (object?)radioDjId ?? DBNull.Value);
                id = (long)upsert.ExecuteScalar()!;
            }
            WriteSteps(id, steps, transaction);
            transaction.Commit();
            return id;
        }
    }

    /// <summary>Crea una rotació nova (editor de rotacions, fase 3, pas 8a). Retorna l'id.</summary>
    /// <exception cref="DatabaseException">Si ja n'hi ha una amb aquest nom.</exception>
    public long Create(string name, IEnumerable<RotationStep> steps)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            long id;
            using (var insert = database.Command("INSERT INTO rotacio (nom) VALUES ($nom); SELECT last_insert_rowid();", transaction))
            {
                insert.Parameters.AddWithValue("$nom", name);
                try { id = (long)insert.ExecuteScalar()!; }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new DatabaseException($"Ja hi ha una rotació «{name}».", ex);
                }
            }
            WriteSteps(id, steps, transaction);
            transaction.Commit();
            return id;
        }
    }

    /// <summary>Canvia el nom i els passos d'una rotació (tot o res).</summary>
    /// <exception cref="DatabaseException">Si no existeix o el nom ja és d'una altra.</exception>
    public void Update(long id, string name, IEnumerable<RotationStep> steps)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            using (var update = database.Command("UPDATE rotacio SET nom = $nom WHERE id = $id;", transaction))
            {
                update.Parameters.AddWithValue("$nom", name);
                update.Parameters.AddWithValue("$id", id);
                try
                {
                    if (update.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la rotació {id}.");
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    throw new DatabaseException($"Ja hi ha una altra rotació «{name}».", ex);
                }
            }
            WriteSteps(id, steps, transaction);
            transaction.Commit();
        }
    }

    /// <summary>Esborra una rotació (amb els passos). No comprova qui la fa servir: ho fa qui la crida.</summary>
    public void Delete(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM rotacio WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la rotació {id}.");
        }
    }

    /// <summary>Els esdeveniments amb una acció ROTACIO que posa aquesta rotació (id i nom), per ordre de nom.</summary>
    public IReadOnlyList<(long Id, string Name)> EventsUsingRotation(long rotationId)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT DISTINCT e.id, e.nom FROM accio a JOIN esdeveniment e ON e.id = a.esdeveniment_id " +
                "WHERE a.tipus = 'ROTACIO' AND json_valid(a.params) AND json_extract(a.params, '$.rotacioId') = $id ORDER BY e.nom COLLATE NOCASE;");
            command.Parameters.AddWithValue("$id", rotationId);
            using var r = command.ExecuteReader();
            var list = new List<(long, string)>();
            while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Substitueix els passos d'una rotació dins d'una transacció oberta.</summary>
    private void WriteSteps(long id, IEnumerable<RotationStep> steps, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
            using (var clear = database.Command("DELETE FROM rotacio_pas WHERE rotacio_id = $r;", transaction))
            {
                clear.Parameters.AddWithValue("$r", id);
                clear.ExecuteNonQuery();
            }
            int order = 0;
            foreach (var step in steps)
            {
                using var insert = database.Command(
                    "INSERT INTO rotacio_pas (rotacio_id, ordre, categoria_id, subcategoria_id, etiqueta_id, seleccio, repeticio, sweeper) " +
                    "VALUES ($r, $ordre, $cat, $sub, $tag, $sel, $rep, $sw);", transaction);
                insert.Parameters.AddWithValue("$r", id);
                insert.Parameters.AddWithValue("$ordre", order++);
                insert.Parameters.AddWithValue("$cat", (object?)step.CategoryId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$sub", (object?)step.SubcategoryId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$tag", (object?)step.TagId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$sel", step.Selection switch
                {
                    RotationSelection.Random => "ALEATORIA",
                    RotationSelection.LeastPlayed => "MENYS_SONADA",
                    _ => "MENYS_RECENT",
                });
                insert.Parameters.AddWithValue("$rep", step.RepeatRule ? 1 : 0);
                insert.Parameters.AddWithValue("$sw", step.Sweeper ? 1 : 0);
                insert.ExecuteNonQuery();
            }
    }

    /// <summary>Passos d'una rotació, en ordre, amb els noms de la categoria, la subcategoria i l'etiqueta.</summary>
    private List<RotationStep> Steps(long rotationId)
    {
        // Si el pas té subcategoria, la categoria que es mostra és la de la subcategoria
        using var command = database.Command(
            "SELECT p.id, p.ordre, COALESCE(p.categoria_id, s.categoria_id), p.subcategoria_id, p.etiqueta_id, p.seleccio, p.repeticio, p.sweeper, " +
            "c.nom, s.nom, e.nom FROM rotacio_pas p " +
            "LEFT JOIN subcategoria s ON s.id = p.subcategoria_id " +
            "LEFT JOIN categoria c ON c.id = COALESCE(p.categoria_id, s.categoria_id) " +
            "LEFT JOIN etiqueta e ON e.id = p.etiqueta_id " +
            "WHERE p.rotacio_id = $r ORDER BY p.ordre, p.id;");
        command.Parameters.AddWithValue("$r", rotationId);
        using var r = command.ExecuteReader();
        var list = new List<RotationStep>();
        while (r.Read())
        {
            list.Add(new RotationStep
            {
                Id = r.GetInt64(0),
                Order = r.GetInt32(1),
                CategoryId = r.IsDBNull(2) ? null : r.GetInt64(2),
                SubcategoryId = r.IsDBNull(3) ? null : r.GetInt64(3),
                TagId = r.IsDBNull(4) ? null : r.GetInt64(4),
                Selection = r.GetString(5) switch
                {
                    "ALEATORIA" => RotationSelection.Random,
                    "MENYS_SONADA" => RotationSelection.LeastPlayed,
                    _ => RotationSelection.LeastRecent,
                },
                RepeatRule = r.GetInt64(6) == 1,
                Sweeper = r.GetInt64(7) == 1,
                CategoryName = r.IsDBNull(8) ? null : r.GetString(8),
                SubcategoryName = r.IsDBNull(9) ? null : r.GetString(9),
                TagName = r.IsDBNull(10) ? null : r.GetString(10),
            });
        }
        return list;
    }
}
