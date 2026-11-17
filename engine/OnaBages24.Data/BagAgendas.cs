namespace OnaBages24.Data;

/// <summary>
/// Una agenda de bosses (D-170): canvia soles les subcategories d'unes bosses amb nom el dia 1 de cada mes, rotant per
/// una llista de subcategories. Cada mes sonen tantes subcategories seguides com bosses hi ha, i avancen d'una; la que
/// continua d'un mes a l'altre es queda a la mateixa bossa (no canvia de pas de rotació).
/// </summary>
/// <param name="StartMonth">El mes en què sonen les primeres subcategories de la llista (el dia no compta: sempre l'1).</param>
/// <param name="AppliedMonth">L'últim mes en què s'ha fet el canvi (el dia és l'1), o null si encara cap.</param>
public sealed record BagAgenda(long Id, string Name, DateOnly StartMonth, bool Active, IReadOnlyList<long> BagIds,
    IReadOnlyList<long> SubcategoryIds, DateOnly? AppliedMonth)
{
    /// <summary>
    /// Quina subcategoria té cada bossa el mes de <paramref name="date"/> (bossa, subcategoria), o una llista buida si és
    /// abans del mes d'inici. Exemple amb Adult 1…9 i les bosses A i B: mes 1, A=1 i B=2; mes 2, A=3 i B=2; mes 3, A=3 i
    /// B=4… mes 8, A=9 i B=8; mes 9, A=9 i B=1; mes 10, A=2 i B=1. Es compta amb mesos de calendari (sense hores: l'horari d'estiu no hi
    /// fa res).
    /// </summary>
    public IReadOnlyList<(long BagId, long SubcategoryId)> Assignment(DateOnly date)
    {
        int month = MonthsBetween(StartMonth, date);
        if (month < 0 || BagIds.Count == 0 || SubcategoryIds.Count == 0) return [];
        // Les subcategories d'aquest mes són les de les posicions month, month+1… (tantes com bosses). La posició i va
        // sempre a la bossa i mod (nombre de bosses): així la que continua el mes següent es queda a la mateixa bossa.
        var list = new (long BagId, long SubcategoryId)[BagIds.Count];
        for (int i = month; i < month + BagIds.Count; i++)
        {
            list[i % BagIds.Count] = (BagIds[i % BagIds.Count], SubcategoryIds[i % SubcategoryIds.Count]);
        }
        return list;
    }

    /// <summary>
    /// Si avui toca fer el canvi: l'agenda és activa, ja ha arribat el mes d'inici i aquest mes encara no s'ha fet (també
    /// si el motor estava aturat el dia 1: es fa en arrencar). Un canvi a mà a mig mes no es desfà fins al mes següent.
    /// </summary>
    public bool IsDue(DateOnly today) =>
        Active && MonthsBetween(StartMonth, today) >= 0 && (AppliedMonth is not { } applied || FirstOfMonth(applied) != FirstOfMonth(today));

    /// <summary>Mesos de calendari de <paramref name="from"/> a <paramref name="to"/> (sense mirar el dia).</summary>
    public static int MonthsBetween(DateOnly from, DateOnly to) => (to.Year * 12 + to.Month) - (from.Year * 12 + from.Month);

    /// <summary>El dia 1 del mes.</summary>
    public static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>«2026-12» (com es desa).</summary>
    public static string MonthText(DateOnly date) => $"{date.Year:D4}-{date.Month:D2}";

    /// <summary>Llegeix «2026-12» (el dia 1). Null si no és vàlid.</summary>
    public static DateOnly? ParseMonth(string? text) =>
        text is { Length: 7 } && text[4] == '-' && int.TryParse(text[..4], out int y) && int.TryParse(text[5..], out int m)
            && y is >= 2000 and <= 9999 && m is >= 1 and <= 12 ? new DateOnly(y, m, 1) : null;

    /// <summary>
    /// Per què no es pot desar (o null si és vàlida): cal un nom, com a mínim una bossa i tantes subcategories com bosses
    /// (si no, una subcategoria aniria a dues bosses alhora), sense repetir-ne cap.
    /// </summary>
    public static string? Problem(string name, IReadOnlyList<long> bagIds, IReadOnlyList<long> subcategoryIds)
    {
        if (string.IsNullOrWhiteSpace(name)) return "L'agenda necessita un nom.";
        if (bagIds.Count == 0) return "L'agenda necessita com a mínim una bossa.";
        if (bagIds.Distinct().Count() != bagIds.Count) return "Una bossa hi surt dues vegades.";
        if (subcategoryIds.Distinct().Count() != subcategoryIds.Count) return "Una subcategoria hi surt dues vegades.";
        if (subcategoryIds.Count < bagIds.Count)
            return $"Amb {bagIds.Count} bosses calen com a mínim {bagIds.Count} subcategories (cada mes en sonen tantes com bosses).";
        return null;
    }
}

/// <summary>Agendes de bosses (taules <c>agenda_bosses</c>, <c>agenda_bossa</c> i <c>agenda_subcategoria</c>, migració 012; D-170).</summary>
public sealed class BagAgendas(Database database)
{
    /// <summary>Totes les agendes, per nom.</summary>
    public IReadOnlyList<BagAgenda> List()
    {
        lock (database.Sync)
        {
            var rows = new List<(long Id, string Name, string Start, bool Active, string? Applied)>();
            using (var command = database.Command("SELECT id, nom, mes_inici, activa, aplicada_mes FROM agenda_bosses ORDER BY nom COLLATE NOCASE;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) != 0, r.IsDBNull(4) ? null : r.GetString(4)));
            }
            return rows.Select(row => new BagAgenda(row.Id, row.Name, BagAgenda.ParseMonth(row.Start) ?? new DateOnly(2000, 1, 1), row.Active,
                Ids("SELECT bossa_id FROM agenda_bossa WHERE agenda_id = $id ORDER BY posicio;", row.Id),
                Ids("SELECT subcategoria_id FROM agenda_subcategoria WHERE agenda_id = $id ORDER BY posicio;", row.Id),
                BagAgenda.ParseMonth(row.Applied))).ToList();
        }
    }

    public BagAgenda? Get(long id) => List().FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// Crea (sense <paramref name="id"/>) o substitueix una agenda, tot o res. En desar-la, es torna a fer el canvi d'aquest
    /// mes (l'agenda pot haver canviat). Retorna l'id.
    /// </summary>
    /// <exception cref="DatabaseException">Si no és vàlida, el nom ja és d'una altra, una bossa ja és en una altra agenda o
    /// una bossa o subcategoria no existeix.</exception>
    public long Save(long? id, string name, DateOnly startMonth, bool active, IReadOnlyList<long> bagIds, IReadOnlyList<long> subcategoryIds)
    {
        name = name.Trim();
        if (BagAgenda.Problem(name, bagIds, subcategoryIds) is { } problem) throw new DatabaseException(problem);
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            try
            {
                if (id is { } existing)
                {
                    using var update = database.Command(
                        "UPDATE agenda_bosses SET nom = $nom, mes_inici = $inici, activa = $activa, aplicada_mes = NULL WHERE id = $id;", transaction);
                    update.Parameters.AddWithValue("$id", existing);
                    Fill(update);
                    if (update.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix l'agenda {existing}.");
                }
                else
                {
                    using var insert = database.Command(
                        "INSERT INTO agenda_bosses (nom, mes_inici, activa) VALUES ($nom, $inici, $activa); SELECT last_insert_rowid();", transaction);
                    Fill(insert);
                    id = (long)insert.ExecuteScalar()!;
                }
                using (var clear = database.Command(
                    "DELETE FROM agenda_bossa WHERE agenda_id = $id; DELETE FROM agenda_subcategoria WHERE agenda_id = $id;", transaction))
                {
                    clear.Parameters.AddWithValue("$id", id);
                    clear.ExecuteNonQuery();
                }
                InsertList(transaction, "agenda_bossa", "bossa_id", id.Value, bagIds);
                InsertList(transaction, "agenda_subcategoria", "subcategoria_id", id.Value, subcategoryIds);
                transaction.Commit();
                return id.Value;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 19 = restricció: nom repetit, bossa d'una altra agenda o id que no existeix. Es desfà abans de mirar quina.
                transaction.Rollback();
                throw new DatabaseException(ConstraintText(id, name, bagIds), ex);
            }
        }

        void Fill(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            command.Parameters.AddWithValue("$nom", name);
            command.Parameters.AddWithValue("$inici", BagAgenda.MonthText(startMonth));
            command.Parameters.AddWithValue("$activa", active ? 1 : 0);
        }
    }

    /// <summary>Anota que el canvi d'aquest mes ja s'ha fet.</summary>
    public void MarkApplied(long id, DateOnly month)
    {
        lock (database.Sync)
        {
            using var command = database.Command("UPDATE agenda_bosses SET aplicada_mes = $mes WHERE id = $id;");
            command.Parameters.AddWithValue("$mes", BagAgenda.MonthText(month));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Esborra una agenda (les bosses es queden amb la subcategoria que tenen).</summary>
    public void Delete(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM agenda_bosses WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix l'agenda {id}.");
        }
    }

    /// <summary>Els noms de les agendes que fan servir aquesta bossa (per no deixar-la esborrar).</summary>
    public IReadOnlyList<string> UsingBag(long bagId) =>
        Names("SELECT a.nom FROM agenda_bossa b JOIN agenda_bosses a ON a.id = b.agenda_id WHERE b.bossa_id = $id ORDER BY a.nom;", bagId);

    /// <summary>Els noms de les agendes que fan servir aquesta subcategoria (per no deixar-la esborrar).</summary>
    public IReadOnlyList<string> UsingSubcategory(long subcategoryId) =>
        Names("SELECT a.nom FROM agenda_subcategoria s JOIN agenda_bosses a ON a.id = s.agenda_id WHERE s.subcategoria_id = $id ORDER BY a.nom;", subcategoryId);

    private void InsertList(Microsoft.Data.Sqlite.SqliteTransaction transaction, string table, string column, long id, IReadOnlyList<long> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            using var insert = database.Command($"INSERT INTO {table} (agenda_id, posicio, {column}) VALUES ($id, $pos, $valor);", transaction);
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$pos", i);
            insert.Parameters.AddWithValue("$valor", values[i]);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>El missatge d'una restricció que ha fallat en desar (es busca quina ha estat, fora de la transacció).</summary>
    private string ConstraintText(long? id, string name, IReadOnlyList<long> bagIds)
    {
        using (var sameName = database.Command("SELECT count(*) FROM agenda_bosses WHERE nom = $nom COLLATE NOCASE AND id IS NOT $id;"))
        {
            sameName.Parameters.AddWithValue("$nom", name);
            sameName.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
            if ((long)sameName.ExecuteScalar()! > 0) return $"Ja hi ha una agenda «{name}».";
        }
        foreach (long bag in bagIds)
        {
            using var other = database.Command(
                "SELECT a.nom, (SELECT nom FROM bossa WHERE id = $bossa) FROM agenda_bossa b JOIN agenda_bosses a ON a.id = b.agenda_id " +
                "WHERE b.bossa_id = $bossa AND a.id IS NOT $id;");
            other.Parameters.AddWithValue("$bossa", bag);
            other.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
            using var r = other.ExecuteReader();
            if (r.Read()) return $"La bossa «{(r.IsDBNull(1) ? bag : r.GetString(1))}» ja és a l'agenda «{r.GetString(0)}».";
        }
        return "Alguna bossa o subcategoria de l'agenda no existeix.";
    }

    private List<long> Ids(string sql, long id)
    {
        using var command = database.Command(sql);
        command.Parameters.AddWithValue("$id", id);
        using var r = command.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    private List<string> Names(string sql, long id)
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
}
