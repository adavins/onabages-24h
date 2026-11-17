namespace OnaBages24.Data;

/// <summary>D'on ve una entrada del registre unificat.</summary>
public enum LogSource
{
    /// <summary>Una incidència (taula <c>incidencia</c>).</summary>
    Incident,
    /// <summary>Un canvi fet per un usuari (taula <c>registre_canvis</c>, excepte les sessions).</summary>
    Change,
    /// <summary>Una sessió: inici, tancament, inactivitat, bloqueig (<c>registre_canvis</c>, ordres <c>session.*</c>).</summary>
    Session,
    /// <summary>L'execució d'un esdeveniment (taula <c>registre_execucio</c>).</summary>
    Event,
}

/// <summary>Una entrada del registre unificat (D-167): els camps comuns de les tres taules.</summary>
public sealed record LogEntry
{
    public LogSource Source { get; init; }
    /// <summary>L'id a la seva taula.</summary>
    public long Id { get; init; }
    /// <summary>Quan: l'inici de la incidència, el moment del canvi o quan s'ha executat l'esdeveniment (o quan tocava).</summary>
    public DateTimeOffset At { get; init; }
    /// <summary>Quan s'ha resolt (només les incidències; null si és oberta).</summary>
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>El tipus d'incidència, l'ordre del canvi o el resultat de l'esdeveniment (<c>OK</c>, <c>OMES_TARD</c>…).</summary>
    public string Kind { get; init; } = "";
    public IncidentSeverity Severity { get; init; }
    /// <summary>El text de la incidència, el resum del canvi o el nom de l'esdeveniment.</summary>
    public string Text { get; init; } = "";
    public string? Detail { get; init; }
    public bool Recovered { get; init; }
    public long? UserId { get; init; }
    public string UserName { get; init; } = "";

    public bool IsOpen => Source == LogSource.Incident && EndedAt is null;
}

/// <summary>Filtres del registre unificat. Els que són null o buits no filtren.</summary>
public sealed record LogFilter
{
    /// <summary>D'on (buit = de tot arreu).</summary>
    public IReadOnlyList<LogSource> Sources { get; init; } = [];
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    /// <summary>La gravetat mínima (els canvis i les sessions són <see cref="IncidentSeverity.Info"/>).</summary>
    public IncidentSeverity? MinSeverity { get; init; }
    /// <summary>Només les incidències obertes (sense mirar les dates).</summary>
    public bool OpenOnly { get; init; }
    /// <summary>Només aquests tipus d'incidència (i, per tant, només incidències).</summary>
    public IReadOnlyList<string> IncidentKinds { get; init; } = [];
    /// <summary>Només els canvis d'aquestes ordres (prefixos; i, per tant, només canvis i sessions).</summary>
    public IReadOnlyList<string> CommandPrefixes { get; init; } = [];
    /// <summary>Només el que ha fet aquest usuari (i, per tant, només canvis i sessions).</summary>
    public long? UserId { get; init; }
    /// <summary>Un text que hi ha de ser (al text o al detall; sense distingir majúscules).</summary>
    public string? Text { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Registre unificat (D-167): les incidències, els canvis, les sessions i l'execució dels esdeveniments en una sola
/// llista, de més nou a més antic. Només llegeix: cada taula la continua escrivint el seu mòdul.
/// </summary>
public sealed class ActivityLog(Database database)
{
    public (IReadOnlyList<LogEntry> Entries, int Total) List(LogFilter filter)
    {
        bool Wants(LogSource source) => filter.Sources.Count == 0 || filter.Sources.Contains(source);
        bool onlyIncidents = filter.OpenOnly || filter.IncidentKinds.Count > 0;
        bool onlyChanges = filter.CommandPrefixes.Count > 0 || filter.UserId is not null;
        int minLevel = filter.MinSeverity is { } min ? (int)min : 0;
        const string Level = "CASE {0} WHEN 'GREU' THEN 2 WHEN 'AVIS' THEN 1 ELSE 0 END";

        var branches = new List<string>();
        string Where(List<string> conditions) => conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        void Common(List<string> c, string moment, string text, string detail)
        {
            if (!filter.OpenOnly && filter.From is not null) c.Add($"{moment} >= $from");
            if (!filter.OpenOnly && filter.To is not null) c.Add($"{moment} < $to");
            if (!string.IsNullOrWhiteSpace(filter.Text)) c.Add($"({text} LIKE $text ESCAPE '\\' OR coalesce({detail}, '') LIKE $text ESCAPE '\\')");
        }

        if (Wants(LogSource.Incident) && !onlyChanges)
        {
            var c = new List<string>();
            Common(c, "inici", "text", "detall");
            if (filter.OpenOnly) c.Add("fi IS NULL");
            if (filter.IncidentKinds.Count > 0) c.Add($"tipus IN ({string.Join(", ", filter.IncidentKinds.Select((_, i) => $"$k{i}"))})");
            if (minLevel > 0) c.Add($"{string.Format(Level, "gravetat")} >= {minLevel}");
            branches.Add("SELECT 0 AS font, id, inici AS moment, fi, tipus, gravetat, text, detall, recuperada, NULL AS usuari_id, '' AS usuari_nom " +
                         "FROM incidencia" + Where(c));
        }
        foreach (var (source, session) in new[] { (LogSource.Change, false), (LogSource.Session, true) })
        {
            if (!Wants(source) || onlyIncidents || minLevel > 0) continue;
            var c = new List<string> { session ? "ordre LIKE 'session.%'" : "ordre NOT LIKE 'session.%'" };
            Common(c, "moment", "resum", "detall");
            if (filter.UserId is not null) c.Add("usuari_id = $usuari");
            if (filter.CommandPrefixes.Count > 0)
                c.Add("(" + string.Join(" OR ", filter.CommandPrefixes.Select((_, i) => $"ordre LIKE $p{i} ESCAPE '\\'")) + ")");
            branches.Add($"SELECT {(int)source} AS font, id, moment, NULL AS fi, ordre AS tipus, 'INFO' AS gravetat, resum AS text, detall, " +
                         "0 AS recuperada, usuari_id, usuari_nom FROM registre_canvis" + Where(c));
        }
        if (Wants(LogSource.Event) && !onlyIncidents && !onlyChanges)
        {
            const string Severity = "CASE resultat WHEN 'OK' THEN 'INFO' WHEN 'ERROR' THEN 'GREU' ELSE 'AVIS' END";
            var c = new List<string>();
            Common(c, "coalesce(executat_a, programat_per)", "nom", "detall");
            if (minLevel > 0) c.Add($"{string.Format(Level, Severity)} >= {minLevel}");
            branches.Add($"SELECT {(int)LogSource.Event} AS font, id, coalesce(executat_a, programat_per) AS moment, NULL AS fi, resultat AS tipus, " +
                         $"{Severity} AS gravetat, nom AS text, detall, 0 AS recuperada, NULL AS usuari_id, '' AS usuari_nom FROM registre_execucio" + Where(c));
        }
        if (branches.Count == 0) return ([], 0);
        string union = string.Join(" UNION ALL ", branches);

        void Bind(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            if (filter.From is { } from) command.Parameters.AddWithValue("$from", DbTime.ToText(from));
            if (filter.To is { } to) command.Parameters.AddWithValue("$to", DbTime.ToText(to));
            if (!string.IsNullOrWhiteSpace(filter.Text)) command.Parameters.AddWithValue("$text", "%" + Escape(filter.Text.Trim()) + "%");
            if (filter.UserId is { } user) command.Parameters.AddWithValue("$usuari", user);
            for (int i = 0; i < filter.IncidentKinds.Count; i++) command.Parameters.AddWithValue($"$k{i}", filter.IncidentKinds[i]);
            for (int i = 0; i < filter.CommandPrefixes.Count; i++) command.Parameters.AddWithValue($"$p{i}", Escape(filter.CommandPrefixes[i]) + "%");
        }

        lock (database.Sync)
        {
            int total;
            using (var count = database.Command($"SELECT count(*) FROM ({union});"))
            {
                Bind(count);
                total = Convert.ToInt32(count.ExecuteScalar());
            }
            using var command = database.Command($"SELECT * FROM ({union}) ORDER BY moment DESC, font, id DESC LIMIT $limit OFFSET $offset;");
            Bind(command);
            command.Parameters.AddWithValue("$limit", Math.Max(1, filter.Limit));
            command.Parameters.AddWithValue("$offset", Math.Max(0, filter.Offset));
            using var r = command.ExecuteReader();
            var list = new List<LogEntry>();
            while (r.Read())
            {
                list.Add(new LogEntry
                {
                    Source = (LogSource)r.GetInt32(0),
                    Id = r.GetInt64(1),
                    At = DbTime.ParseInstant(r.GetString(2)),
                    EndedAt = r.IsDBNull(3) ? null : DbTime.ParseInstant(r.GetString(3)),
                    Kind = r.GetString(4),
                    Severity = r.GetString(5) switch { "GREU" => IncidentSeverity.Danger, "AVIS" => IncidentSeverity.Warning, _ => IncidentSeverity.Info },
                    Text = r.GetString(6),
                    Detail = r.IsDBNull(7) ? null : r.GetString(7),
                    Recovered = r.GetInt64(8) != 0,
                    UserId = r.IsDBNull(9) ? null : r.GetInt64(9),
                    UserName = r.GetString(10),
                });
            }
            return (list, total);
        }
    }

    /// <summary>Per a LIKE: els caràcters especials, tal com són.</summary>
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
