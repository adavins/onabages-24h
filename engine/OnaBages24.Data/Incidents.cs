namespace OnaBages24.Data;

/// <summary>Com és de greu una incidència (a la interfície: info, avís o greu, amb color + icona + text).</summary>
public enum IncidentSeverity
{
    Info,
    Warning,
    Danger,
}

/// <summary>Una incidència (taula <c>incidencia</c>, migració 009; fase 5, pas 1).</summary>
public sealed record Incident
{
    public long Id { get; init; }
    /// <summary>El tipus, en majúscules: <c>SORTIDA</c>, <c>FITXER</c>, <c>ESDEVENIMENT_OMES</c>…</summary>
    public string Kind { get; init; } = "";
    /// <summary>Què és exactament (p. ex. <c>carpeta:12</c>): no se n'obren dues amb la mateixa clau.</summary>
    public string Key { get; init; } = "";
    public IncidentSeverity Severity { get; init; }
    public string Text { get; init; } = "";
    public string? Detail { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    /// <summary>Quan s'ha resolt; null mentre és oberta.</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>El motor ho ha resolt sol (p. ex. ha saltat una pista que no es podia llegir).</summary>
    public bool Recovered { get; init; }

    public bool IsOpen => EndedAt is null;
}

/// <summary>Filtres de la llista d'incidències. Els que són null no filtren.</summary>
public sealed record IncidentFilter
{
    /// <summary>true: només les obertes; false: només les resoltes.</summary>
    public bool? Open { get; init; }
    /// <summary>Les que han començat a partir d'aquest instant (inclòs).</summary>
    public DateTimeOffset? From { get; init; }
    /// <summary>Les que han començat abans d'aquest instant.</summary>
    public DateTimeOffset? To { get; init; }
    /// <summary>Només aquests tipus (buit = tots).</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Incidències (fase 5, pas 1; D-156): desar-les, tancar-les i llistar-les. Quan s'obren i es tanquen ho decideix el
/// motor (<c>IncidentTracker</c>).
/// </summary>
public sealed class IncidentStore(Database database)
{
    private const string Columns = "id, tipus, clau, gravetat, text, detall, inici, fi, recuperada";

    private static string SeverityText(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Danger => "GREU",
        IncidentSeverity.Warning => "AVIS",
        _ => "INFO",
    };

    private static IncidentSeverity ParseSeverity(string text) => text switch
    {
        "GREU" => IncidentSeverity.Danger,
        "AVIS" => IncidentSeverity.Warning,
        _ => IncidentSeverity.Info,
    };

    /// <summary>Desa una incidència nova (oberta si no té <see cref="Incident.EndedAt"/>). Retorna l'id.</summary>
    public long Add(Incident incident)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO incidencia (tipus, clau, gravetat, text, detall, inici, fi, recuperada) " +
                "VALUES ($tipus, $clau, $gravetat, $text, $detall, $inici, $fi, $recuperada) RETURNING id;");
            command.Parameters.AddWithValue("$tipus", incident.Kind);
            command.Parameters.AddWithValue("$clau", incident.Key);
            command.Parameters.AddWithValue("$gravetat", SeverityText(incident.Severity));
            command.Parameters.AddWithValue("$text", incident.Text);
            command.Parameters.AddWithValue("$detall", (object?)incident.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$inici", DbTime.ToText(incident.StartedAt));
            command.Parameters.AddWithValue("$fi", incident.EndedAt is { } end ? DbTime.ToText(end) : DBNull.Value);
            command.Parameters.AddWithValue("$recuperada", incident.Recovered ? 1 : 0);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Canvia el text, la gravetat i el detall d'una incidència oberta (p. ex. ara falten més fitxers).</summary>
    public void Update(long id, IncidentSeverity severity, string text, string? detail)
    {
        lock (database.Sync)
        {
            using var command = database.Command("UPDATE incidencia SET gravetat = $gravetat, text = $text, detall = $detall WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$gravetat", SeverityText(severity));
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$detall", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Tanca una incidència oberta.</summary>
    public void Close(long id, DateTimeOffset at, bool recovered)
    {
        lock (database.Sync)
        {
            using var command = database.Command("UPDATE incidencia SET fi = $fi, recuperada = $recuperada WHERE id = $id AND fi IS NULL;");
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$fi", DbTime.ToText(at));
            command.Parameters.AddWithValue("$recuperada", recovered ? 1 : 0);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Les que compleixen els filtres, les més recents primer, i quantes n'hi ha en total.</summary>
    public (IReadOnlyList<Incident> Incidents, int Total) List(IncidentFilter filter)
    {
        var where = new List<string>();
        if (filter.Open is { } open) where.Add(open ? "fi IS NULL" : "fi IS NOT NULL");
        if (filter.From is not null) where.Add("inici >= $from");
        if (filter.To is not null) where.Add("inici < $to");
        if (filter.Kinds.Count > 0) where.Add($"tipus IN ({string.Join(", ", filter.Kinds.Select((_, i) => $"$k{i}"))})");
        string condition = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        void Bind(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            if (filter.From is { } from) command.Parameters.AddWithValue("$from", DbTime.ToText(from));
            if (filter.To is { } to) command.Parameters.AddWithValue("$to", DbTime.ToText(to));
            for (int i = 0; i < filter.Kinds.Count; i++) command.Parameters.AddWithValue($"$k{i}", filter.Kinds[i]);
        }

        lock (database.Sync)
        {
            int total;
            using (var count = database.Command($"SELECT count(*) FROM incidencia {condition};"))
            {
                Bind(count);
                total = Convert.ToInt32(count.ExecuteScalar());
            }
            using var command = database.Command($"SELECT {Columns} FROM incidencia {condition} ORDER BY inici DESC, id DESC LIMIT $limit OFFSET $offset;");
            Bind(command);
            command.Parameters.AddWithValue("$limit", Math.Max(1, filter.Limit));
            command.Parameters.AddWithValue("$offset", Math.Max(0, filter.Offset));
            using var r = command.ExecuteReader();
            var list = new List<Incident>();
            while (r.Read())
            {
                list.Add(new Incident
                {
                    Id = r.GetInt64(0),
                    Kind = r.GetString(1),
                    Key = r.GetString(2),
                    Severity = ParseSeverity(r.GetString(3)),
                    Text = r.GetString(4),
                    Detail = r.IsDBNull(5) ? null : r.GetString(5),
                    StartedAt = DbTime.ParseInstant(r.GetString(6)),
                    EndedAt = r.IsDBNull(7) ? null : DbTime.ParseInstant(r.GetString(7)),
                    Recovered = r.GetInt64(8) != 0,
                });
            }
            return (list, total);
        }
    }

    /// <summary>Esborra les resoltes que van començar abans de <paramref name="before"/> (es conserven 1 any). Retorna quantes.</summary>
    public int Purge(DateTimeOffset before)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM incidencia WHERE fi IS NOT NULL AND inici < $before;");
            command.Parameters.AddWithValue("$before", DbTime.ToText(before));
            return command.ExecuteNonQuery();
        }
    }
}
