using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Com es reparteixen els passis entre les falques vàlides d'una campanya (docs/planificador.md, secció 10).</summary>
public enum CampaignSelection
{
    /// <summary>ALTERNA: a parts iguals.</summary>
    Alternate,
    /// <summary>PES: segons el pes de cada falca (p. ex. A el doble que B).</summary>
    Weight,
    /// <summary>SEQUENCIA: sempre en ordre (A, B, C, A, B, C…), per a falques que són capítols.</summary>
    Sequence,
}

/// <summary>Estat d'una campanya (D-122).</summary>
public enum CampaignState
{
    /// <summary>ESBORRANY: es pot programar al calendari, però no sona.</summary>
    Draft,
    /// <summary>ACTIVA: sona.</summary>
    Active,
    /// <summary>ACABADA: ja no sona. Una activa passa a acabada sola quan passa la data de fi.</summary>
    Finished,
}

/// <summary>Una falca d'una campanya, amb les seves restriccions (taula <c>campanya_falca</c>).</summary>
public sealed record CampaignSpot
{
    public long TrackId { get; init; }
    /// <summary>Null = la data d'inici de la campanya.</summary>
    public DateOnly? StartDate { get; init; }
    /// <summary>Null = la data de fi de la campanya.</summary>
    public DateOnly? EndDate { get; init; }
    /// <summary>Dies de la setmana en què pot sonar: bit0 = dilluns … bit6 = diumenge.</summary>
    public int DaysMask { get; init; } = Weekdays.All;
    /// <summary>Per al repartiment PES.</summary>
    public int Weight { get; init; } = 1;
}

/// <summary>Una campanya de falques (taula <c>campanya</c>), amb les falques en ordre.</summary>
public sealed record Campaign
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Advertiser { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public CampaignSelection Selection { get; init; } = CampaignSelection.Alternate;
    /// <summary>L'estat desat. Per saber si sona, <see cref="EffectiveState"/>.</summary>
    public CampaignState State { get; init; } = CampaignState.Draft;
    /// <summary>Passis contractats en total (opcional; només per avisar).</summary>
    public int? ContractedTotal { get; init; }
    /// <summary>Passis contractats per dia (opcional; només per avisar).</summary>
    public int? ContractedPerDay { get; init; }
    public string? Notes { get; init; }
    /// <summary>Quan es va desar per última vegada (el posa <see cref="Campaigns.Save"/>).</summary>
    public DateTimeOffset Modified { get; init; }
    public IReadOnlyList<CampaignSpot> Spots { get; init; } = [];

    /// <summary>L'estat que compta avui: una campanya activa amb la data de fi passada ja està acabada.</summary>
    public CampaignState EffectiveState(DateOnly today) =>
        State == CampaignState.Active && EndDate < today ? CampaignState.Finished : State;
}

/// <summary>
/// Un passi del pla d'una campanya (taula <c>campanya_passi</c>, D-029): quina falca toca en una emissió d'un esdeveniment.
/// <paramref name="TrackId"/> null = aquell dia no hi ha cap falca vàlida (el passi queda buit).
/// </summary>
public sealed record CampaignPass(long EventId, DateTimeOffset At, long? TrackId);

/// <summary>Campanyes de falques (taules <c>campanya</c>, <c>campanya_falca</c> i <c>campanya_passi</c>, migració 007; fase 4, pas 3).</summary>
public sealed class Campaigns(Database database)
{
    /// <summary>El pla d'una campanya, en ordre d'hora (opcionalment, només entre <paramref name="from"/> inclòs i <paramref name="to"/> exclòs).</summary>
    public IReadOnlyList<CampaignPass> Plan(long campaignId, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT esdeveniment_id, programat_per, pista_id FROM campanya_passi WHERE campanya_id = $id " +
                "AND ($from IS NULL OR programat_per >= $from) AND ($to IS NULL OR programat_per < $to) ORDER BY programat_per, esdeveniment_id;");
            command.Parameters.AddWithValue("$id", campaignId);
            command.Parameters.AddWithValue("$from", from is { } f ? DbTime.ToText(f) : DBNull.Value);
            command.Parameters.AddWithValue("$to", to is { } t ? DbTime.ToText(t) : DBNull.Value);
            using var r = command.ExecuteReader();
            var list = new List<CampaignPass>();
            while (r.Read()) list.Add(new CampaignPass(r.GetInt64(0), DbTime.ParseInstant(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2)));
            return list;
        }
    }

    /// <summary>El passi previst d'una campanya en una emissió concreta d'un esdeveniment, o null si no és al pla.</summary>
    public CampaignPass? GetPass(long campaignId, long eventId, DateTimeOffset at)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT pista_id FROM campanya_passi WHERE campanya_id = $c AND esdeveniment_id = $e AND programat_per = $at;");
            command.Parameters.AddWithValue("$c", campaignId);
            command.Parameters.AddWithValue("$e", eventId);
            command.Parameters.AddWithValue("$at", DbTime.ToText(at));
            using var r = command.ExecuteReader();
            return r.Read() ? new CampaignPass(eventId, at, r.IsDBNull(0) ? null : r.GetInt64(0)) : null;
        }
    }

    /// <summary>
    /// Substitueix el pla a partir de <paramref name="from"/> (inclòs) per aquests passis; els d'abans no es toquen (D-029). Tot
    /// sencer o gens.
    /// </summary>
    public void ReplacePlanFrom(long campaignId, DateTimeOffset from, IEnumerable<CampaignPass> passes)
    {
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            using (var delete = database.Command("DELETE FROM campanya_passi WHERE campanya_id = $id AND programat_per >= $from;", transaction))
            {
                delete.Parameters.AddWithValue("$id", campaignId);
                delete.Parameters.AddWithValue("$from", DbTime.ToText(from));
                delete.ExecuteNonQuery();
            }
            using var insert = database.Command(
                "INSERT OR REPLACE INTO campanya_passi (campanya_id, esdeveniment_id, programat_per, pista_id) VALUES ($c, $e, $at, $p);", transaction);
            var c = insert.Parameters.Add("$c", SqliteType.Integer);
            var e = insert.Parameters.Add("$e", SqliteType.Integer);
            var at = insert.Parameters.Add("$at", SqliteType.Text);
            var p = insert.Parameters.Add("$p", SqliteType.Integer);
            foreach (var pass in passes)
            {
                if (pass.At < from) continue;
                c.Value = campaignId;
                e.Value = pass.EventId;
                at.Value = DbTime.ToText(pass.At);
                p.Value = pass.TrackId is { } id ? id : DBNull.Value;
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>Pes màxim d'una falca.</summary>
    public const int MaxWeight = 10;

    /// <summary>Totes les campanyes, de la que comença més tard a la que comença abans (i, si són iguals, per nom).</summary>
    public IReadOnlyList<Campaign> List()
    {
        lock (database.Sync)
        {
            var list = new List<Campaign>();
            using (var command = database.Command(
                "SELECT id, nom, anunciant, data_inici, data_fi, seleccio, estat, passis_contractats_total, passis_contractats_dia, notes, modificat " +
                "FROM campanya ORDER BY data_inici DESC, nom COLLATE NOCASE, id;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) list.Add(Read(r));
            }
            return list.Select(c => c with { Spots = Spots(c.Id) }).ToList();
        }
    }

    public Campaign? Get(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT id, nom, anunciant, data_inici, data_fi, seleccio, estat, passis_contractats_total, passis_contractats_dia, notes, modificat " +
                "FROM campanya WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            using var r = command.ExecuteReader();
            return r.Read() ? Read(r) with { Spots = Spots(id) } : null;
        }
    }

    /// <summary>
    /// Desa una campanya: nova si <see cref="Campaign.Id"/> és 0; si no, la substitueix (amb les falques). Tot sencer o gens.
    /// Retorna l'id.
    /// </summary>
    /// <exception cref="DatabaseException">Si les dades no són vàlides (el missatge diu per què) o la campanya no existeix.</exception>
    public long Save(Campaign campaign, DateTimeOffset now)
    {
        Validate(campaign);
        lock (database.Sync)
        {
            foreach (var spot in campaign.Spots)
            {
                using var check = database.Command("SELECT count(*) FROM pista WHERE id = $id;");
                check.Parameters.AddWithValue("$id", spot.TrackId);
                if (Convert.ToInt64(check.ExecuteScalar()) == 0) throw new DatabaseException($"No existeix la pista {spot.TrackId}.");
            }

            using var transaction = database.BeginTransaction();
            long id = campaign.Id;
            using (var command = database.Command(id == 0
                ? "INSERT INTO campanya (nom, anunciant, data_inici, data_fi, seleccio, estat, passis_contractats_total, passis_contractats_dia, notes, modificat) " +
                  "VALUES ($nom, $anunciant, $inici, $fi, $seleccio, $estat, $total, $dia, $notes, $modificat) RETURNING id;"
                : "UPDATE campanya SET nom = $nom, anunciant = $anunciant, data_inici = $inici, data_fi = $fi, seleccio = $seleccio, estat = $estat, " +
                  "passis_contractats_total = $total, passis_contractats_dia = $dia, notes = $notes, modificat = $modificat WHERE id = $id RETURNING id;",
                transaction))
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$nom", campaign.Name.Trim());
                command.Parameters.AddWithValue("$anunciant", Blank(campaign.Advertiser));
                command.Parameters.AddWithValue("$inici", DbTime.ToText(campaign.StartDate));
                command.Parameters.AddWithValue("$fi", DbTime.ToText(campaign.EndDate));
                command.Parameters.AddWithValue("$seleccio", SelectionText(campaign.Selection));
                command.Parameters.AddWithValue("$estat", StateText(campaign.State));
                command.Parameters.AddWithValue("$total", (object?)campaign.ContractedTotal ?? DBNull.Value);
                command.Parameters.AddWithValue("$dia", (object?)campaign.ContractedPerDay ?? DBNull.Value);
                command.Parameters.AddWithValue("$notes", Blank(campaign.Notes));
                command.Parameters.AddWithValue("$modificat", DbTime.ToText(now));
                object? result = command.ExecuteScalar();
                if (result is null) throw new DatabaseException($"No existeix la campanya {id}.");
                id = Convert.ToInt64(result);
            }

            using (var delete = database.Command("DELETE FROM campanya_falca WHERE campanya_id = $id;", transaction))
            {
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            int order = 0;
            foreach (var spot in campaign.Spots)
            {
                using var insert = database.Command(
                    "INSERT INTO campanya_falca (campanya_id, pista_id, ordre, data_inici, data_fi, dies_mask, pes) " +
                    "VALUES ($campanya, $pista, $ordre, $inici, $fi, $dies, $pes);", transaction);
                insert.Parameters.AddWithValue("$campanya", id);
                insert.Parameters.AddWithValue("$pista", spot.TrackId);
                insert.Parameters.AddWithValue("$ordre", order++);
                insert.Parameters.AddWithValue("$inici", spot.StartDate is { } s ? DbTime.ToText(s) : DBNull.Value);
                insert.Parameters.AddWithValue("$fi", spot.EndDate is { } e ? DbTime.ToText(e) : DBNull.Value);
                insert.Parameters.AddWithValue("$dies", spot.DaysMask);
                insert.Parameters.AddWithValue("$pes", spot.Weight);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return id;
        }
    }

    /// <summary>
    /// Esborra una campanya (amb les falques i el pla). No es pot si algun esdeveniment la fa servir o si ja ha sonat (l'historial
    /// la necessita per al certificat d'emissió, D-011).
    /// </summary>
    /// <exception cref="DatabaseException">Si no existeix o no es pot esborrar (el missatge diu per què).</exception>
    public void Delete(long id)
    {
        lock (database.Sync)
        {
            var uses = EventsUsingCampaign(id);
            if (uses.Count > 0)
                throw new DatabaseException($"No es pot esborrar: la fa servir {(uses.Count == 1 ? "l'esdeveniment" : "els esdeveniments")} " +
                                            string.Join(", ", uses.Select(u => $"«{u.EventName}»")) + ". Treu-la'n primer.");
            int played = PlayCount(id);
            if (played > 0)
                throw new DatabaseException($"No es pot esborrar: ja ha sonat {played} {(played == 1 ? "vegada" : "vegades")} i l'historial la necessita " +
                                            "per al certificat d'emissió. Posa-la com a acabada.");
            using var command = database.Command("DELETE FROM campanya WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            if (command.ExecuteNonQuery() == 0) throw new DatabaseException($"No existeix la campanya {id}.");
        }
    }

    /// <summary>Els esdeveniments que fan sonar una campanya (acció CAMPANYA), per ordre de nom.</summary>
    public IReadOnlyList<TrackUse> EventsUsingCampaign(long campaignId)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT DISTINCT e.id, e.nom FROM accio a JOIN esdeveniment e ON e.id = a.esdeveniment_id " +
                "WHERE a.tipus = 'CAMPANYA' AND json_valid(a.params) AND json_extract(a.params, '$.campanyaId') = $id " +
                "ORDER BY e.nom COLLATE NOCASE, e.id;");
            command.Parameters.AddWithValue("$id", campaignId);
            using var r = command.ExecuteReader();
            var list = new List<TrackUse>();
            while (r.Read()) list.Add(new TrackUse(r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Quantes vegades ha sonat una falca d'aquesta campanya (historial).</summary>
    public int PlayCount(long campaignId)
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT count(*) FROM historial WHERE campanya_id = $id;");
            command.Parameters.AddWithValue("$id", campaignId);
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    /// <summary>
    /// Comprova les dades d'una campanya abans de desar-la. Les dates d'una falca han de ser dins de les de la campanya.
    /// </summary>
    /// <exception cref="DatabaseException">Amb el motiu, en català.</exception>
    public static void Validate(Campaign c)
    {
        string name = c.Name.Trim();
        if (name.Length == 0) throw new DatabaseException("Falta el nom de la campanya.");
        if (name.Length > 100) throw new DatabaseException("El nom pot tenir com a molt 100 caràcters.");
        if (c.Advertiser is { Length: > 100 }) throw new DatabaseException("L'anunciant pot tenir com a molt 100 caràcters.");
        if (c.Notes is { Length: > 2000 }) throw new DatabaseException("Les notes poden tenir com a molt 2.000 caràcters.");
        if (c.EndDate < c.StartDate) throw new DatabaseException("La data de fi de la campanya és anterior a la d'inici.");
        if (c.ContractedTotal is < 0 || c.ContractedPerDay is < 0) throw new DatabaseException("Els passis contractats no poden ser negatius.");
        if (c.Spots.Select(s => s.TrackId).Distinct().Count() != c.Spots.Count) throw new DatabaseException("Hi ha una falca repetida a la campanya.");

        int number = 0;
        foreach (var spot in c.Spots)
        {
            string which = $"La falca {++number}";
            if (spot.DaysMask is < 1 or > Weekdays.All) throw new DatabaseException($"{which} no té cap dia de la setmana.");
            if (spot.Weight is < 1 or > MaxWeight) throw new DatabaseException($"{which}: el pes ha de ser entre 1 i {MaxWeight}.");
            var start = spot.StartDate ?? c.StartDate;
            var end = spot.EndDate ?? c.EndDate;
            if (end < start) throw new DatabaseException($"{which}: la data de fi és anterior a la d'inici.");
            if (start < c.StartDate || end > c.EndDate) throw new DatabaseException($"{which}: les dates han de ser dins de les de la campanya.");
        }
    }

    public static string SelectionText(CampaignSelection selection) => selection switch
    {
        CampaignSelection.Weight => "PES",
        CampaignSelection.Sequence => "SEQUENCIA",
        _ => "ALTERNA",
    };

    public static CampaignSelection? ParseSelection(string? text) => text switch
    {
        "ALTERNA" => CampaignSelection.Alternate,
        "PES" => CampaignSelection.Weight,
        "SEQUENCIA" => CampaignSelection.Sequence,
        _ => null,
    };

    public static string StateText(CampaignState state) => state switch
    {
        CampaignState.Active => "ACTIVA",
        CampaignState.Finished => "ACABADA",
        _ => "ESBORRANY",
    };

    public static CampaignState? ParseState(string? text) => text switch
    {
        "ESBORRANY" => CampaignState.Draft,
        "ACTIVA" => CampaignState.Active,
        "ACABADA" => CampaignState.Finished,
        _ => null,
    };

    private IReadOnlyList<CampaignSpot> Spots(long campaignId)
    {
        using var command = database.Command(
            "SELECT pista_id, data_inici, data_fi, dies_mask, pes FROM campanya_falca WHERE campanya_id = $id ORDER BY ordre, pista_id;");
        command.Parameters.AddWithValue("$id", campaignId);
        using var r = command.ExecuteReader();
        var list = new List<CampaignSpot>();
        while (r.Read())
        {
            list.Add(new CampaignSpot
            {
                TrackId = r.GetInt64(0),
                StartDate = r.IsDBNull(1) ? null : DbTime.ParseDate(r.GetString(1)),
                EndDate = r.IsDBNull(2) ? null : DbTime.ParseDate(r.GetString(2)),
                DaysMask = r.GetInt32(3),
                Weight = r.GetInt32(4),
            });
        }
        return list;
    }

    private static Campaign Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Advertiser = r.IsDBNull(2) ? null : r.GetString(2),
        StartDate = DbTime.ParseDate(r.GetString(3)),
        EndDate = DbTime.ParseDate(r.GetString(4)),
        Selection = ParseSelection(r.GetString(5)) ?? CampaignSelection.Alternate,
        State = ParseState(r.GetString(6)) ?? CampaignState.Draft,
        ContractedTotal = r.IsDBNull(7) ? null : r.GetInt32(7),
        ContractedPerDay = r.IsDBNull(8) ? null : r.GetInt32(8),
        Notes = r.IsDBNull(9) ? null : r.GetString(9),
        Modified = DbTime.ParseInstant(r.GetString(10)),
    };

    private static object Blank(string? text) => string.IsNullOrWhiteSpace(text) ? DBNull.Value : text.Trim();
}
