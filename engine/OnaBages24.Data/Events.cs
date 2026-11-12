using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OnaBages24.Data;

/// <summary>Què fa un esdeveniment a l'hora programada (docs/planificador.md, secció 2).</summary>
public enum EventMode
{
    /// <summary>TALLA: talla la pista que sona i el contingut comença a l'hora exacta.</summary>
    Cut,
    /// <summary>ENCUA: el contingut es posa com a següent a sonar; la pista que sona continua.</summary>
    Queue,
    /// <summary>SUPERPOSA: sona a sobre del que soni (senyals horaris), amb atenuació opcional.</summary>
    Overlay,
    /// <summary>SISTEMA: només accions sense àudio (canviar la rotació, executar un programa…).</summary>
    System,
}

/// <summary>Quins dies sona un esdeveniment (secció 12).</summary>
public enum EventRecurrence
{
    /// <summary>Uns dies de la setmana (<see cref="ScheduledEvent.DaysMask"/>).</summary>
    Weekly,
    /// <summary>Uns dies de la setmana, cada N setmanes comptant des de la data d'àncora.</summary>
    EveryNWeeks,
    /// <summary>El primer, segon… o últim dia de la setmana indicat de cada mes (p. ex. primer dissabte).</summary>
    MonthlyOrdinal,
    /// <summary>Uns dies del mes (1, 15, últim).</summary>
    DaysOfMonth,
    /// <summary>Només unes dates soltes (<see cref="ScheduledEvent.IncludedDates"/>).</summary>
    Dates,
}

/// <summary>Repetició dins del dia.</summary>
public enum EventRepeat
{
    /// <summary>Una sola vegada, a <see cref="ScheduledEvent.StartTime"/>.</summary>
    None,
    /// <summary>Cada hora (les de <see cref="ScheduledEvent.HoursMask"/>), al minut i segon de <see cref="ScheduledEvent.StartTime"/>.</summary>
    EveryHour,
    /// <summary>Cada N minuts a partir de <see cref="ScheduledEvent.StartTime"/>.</summary>
    EveryNMinutes,
}

/// <summary>Tipus d'acció d'un esdeveniment (secció 4). Els noms de la base de dades són els de l'especificació.</summary>
public enum EventActionType
{
    Track,        // PISTA
    Stream,       // STREAM
    Campaign,     // CAMPANYA
    Template,     // PLANTILLA
    Auxiliary,    // AUXILIAR
    Rotation,     // ROTACIO
    Sweepers,     // SWEEPERS
    AutoDj,       // AUTODJ
    Wait,         // ESPERA
    Stop,         // ATURA
    Input,        // ENTRADA
    Run,          // EXECUTA
    Play,         // REPRODUEIX
    Bag,          // BOSSA (fase 3, pas 8c; D-096): canvia la subcategoria d'una bossa amb nom
}

/// <summary>Resultat d'una execució, al registre (secció 3; D-057).</summary>
public enum ExecutionResult
{
    /// <summary>OK: s'ha executat.</summary>
    Ok,
    /// <summary>OMES_TARD: s'hi ha arribat més tard que la tolerància (p. ex. el motor no funcionava).</summary>
    SkippedLate,
    /// <summary>OMES_CONFLICTE: coincidia amb un altre de prioritat més alta.</summary>
    SkippedConflict,
    /// <summary>OMES_CANVI_HORA: l'hora no existeix aquell dia (pas a l'hora d'estiu, D-057).</summary>
    SkippedClockChange,
    /// <summary>NO_EMES: el contingut d'un ENCUA s'ha esborrat de la cua abans de sonar (D-021).</summary>
    NotPlayed,
    /// <summary>OMES_ATURAT: els esdeveniments estaven aturats a mà (D-073).</summary>
    SkippedPaused,
    /// <summary>ERROR: no s'ha pogut executar.</summary>
    Error,
}

/// <summary>Una acció d'un esdeveniment. L'ordre és la posició a la llista de l'esdeveniment.</summary>
public sealed record EventAction
{
    public long Id { get; init; }
    public EventActionType Type { get; init; }
    /// <summary>Paràmetres, com a objecte JSON (p. ex. <c>{"songId":123}</c>).</summary>
    public string Params { get; init; } = "{}";
    /// <summary>Null = tots els dies de l'esdeveniment; si no, només aquests (bit0 = dilluns, D-028).</summary>
    public int? DaysMask { get; init; }
}

/// <summary>Un esdeveniment programat (taula <c>esdeveniment</c>, amb les accions i les dates).</summary>
public sealed record ScheduledEvent
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public EventMode Mode { get; init; }
    /// <summary>Hora local d'inici (precisió de segon).</summary>
    public TimeOnly StartTime { get; init; }

    public EventRecurrence Recurrence { get; init; } = EventRecurrence.Weekly;
    /// <summary>Dies de la setmana: bit0 = dilluns … bit6 = diumenge (<see cref="Weekdays"/>).</summary>
    public int DaysMask { get; init; } = Weekdays.All;
    /// <summary>CADA_N_SETMANES: cada quantes setmanes.</summary>
    public int? IntervalWeeks { get; init; }
    /// <summary>CADA_N_SETMANES: un dia en què segur que toca.</summary>
    public DateOnly? AnchorDate { get; init; }
    /// <summary>MENSUAL_ORDINAL: 1 = primer… 5 = cinquè; −1 = últim.</summary>
    public IReadOnlyList<int> Ordinals { get; init; } = [];
    /// <summary>DIES_MES: 1…31; −1 = últim dia del mes.</summary>
    public IReadOnlyList<int> MonthDays { get; init; } = [];
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    public EventRepeat Repeat { get; init; } = EventRepeat.None;
    /// <summary>CADA_N_MIN: cada quants minuts.</summary>
    public int? IntervalMinutes { get; init; }
    /// <summary>CADA_HORA: bit0 = 00 h … bit23 = 23 h; null = totes les hores.</summary>
    public int? HoursMask { get; init; }

    public int Priority { get; init; } = 50;
    public int ToleranceSeconds { get; init; } = 60;
    public int? FadeMs { get; init; }
    public int? MarginSeconds { get; init; }
    public double? DuckingDb { get; init; }
    /// <summary>Durada màxima prevista del contingut, en segons (D-028).</summary>
    public int? MaxDurationSeconds { get; init; }
    public string? Color { get; init; }
    public long? CategoryId { get; init; }
    /// <summary>Esborra la cua abans d'inserir el contingut (D-022).</summary>
    public bool ClearQueue { get; init; }
    /// <summary>Bloc de falques (D-029, fase 4).</summary>
    public bool CommercialBlock { get; init; }
    /// <summary>Plantilla: no es dispara mai sola, es fa servir amb l'acció PLANTILLA.</summary>
    public bool IsTemplate { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Notes { get; init; }
    /// <summary>Quan es va desar per última vegada (el posa <see cref="Events.Save"/>).</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>Accions, en ordre d'execució i emissió.</summary>
    public IReadOnlyList<EventAction> Actions { get; init; } = [];
    /// <summary>Dates soltes en què sona (recurrència DATES).</summary>
    public IReadOnlyList<DateOnly> IncludedDates { get; init; } = [];
    /// <summary>Excepcions: dates en què no sona encara que toqui.</summary>
    public IReadOnlyList<DateOnly> ExcludedDates { get; init; } = [];
}

/// <summary>Màscara de dies de la setmana: bit0 = dilluns … bit6 = diumenge.</summary>
public static class Weekdays
{
    public const int All = 0b111_1111;
    public const int MondayToFriday = 0b001_1111;

    /// <summary>El bit d'aquest dia de la setmana.</summary>
    public static int Bit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    public static bool Contains(int mask, DayOfWeek day) => (mask & Bit(day)) != 0;
}

/// <summary>Una categoria d'esdeveniments (per pintar-los i filtrar-los a la graella).</summary>
public sealed record EventCategory(long Id, string Name, string? Color);

/// <summary>Una línia del registre d'execució.</summary>
public sealed record ExecutionRecord
{
    public long Id { get; init; }
    public long? EventId { get; init; }
    /// <summary>Nom de l'esdeveniment en aquell moment (el registre es conserva encara que s'esborri).</summary>
    public string Name { get; init; } = "";
    public DateTimeOffset ScheduledFor { get; init; }
    public DateTimeOffset? ExecutedAt { get; init; }
    public ExecutionResult Result { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Esdeveniments del planificador, les seves categories i el registre d'execució (migració 004; docs/planificador.md, secció 3).
/// </summary>
public sealed class Events(Database database)
{
    private const string Columns =
        "id, nom, mode, hora_inici, recurrencia, dies_mask, interval_setm, data_ancora, ordinals, dies_mes, data_inici, data_fi, " +
        "repeticio, interval_min, hores_mask, prioritat, tolerancia_seg, fade_ms, marge_seg, ducking_db, durada_max, color, " +
        "categoria_id, neteja_cua, bloc_falques, es_plantilla, actiu, notes, modificat";

    // --- Esdeveniments ---

    /// <summary>Tots els esdeveniments (també els desactivats i les plantilles), per hora d'inici i nom.</summary>
    public IReadOnlyList<ScheduledEvent> List()
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {Columns} FROM esdeveniment ORDER BY hora_inici, nom, id;");
            return ReadEvents(command);
        }
    }

    public ScheduledEvent? Get(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {Columns} FROM esdeveniment WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            return ReadEvents(command).FirstOrDefault();
        }
    }

    /// <summary>
    /// Desa un esdeveniment amb les seves accions i dates (tot alhora). Amb <c>Id</c> = 0 en crea un de nou; si no,
    /// substitueix el que hi havia. Retorna l'id. Si les dades no són coherents, <see cref="ArgumentException"/> amb el motiu.
    /// </summary>
    public long Save(ScheduledEvent ev)
    {
        Validate(ev);
        lock (database.Sync)
        {
            using var transaction = database.BeginTransaction();
            long id = ev.Id;
            string sql = id == 0
                ? "INSERT INTO esdeveniment (nom, mode, hora_inici, recurrencia, dies_mask, interval_setm, data_ancora, ordinals, dies_mes, " +
                  "data_inici, data_fi, repeticio, interval_min, hores_mask, prioritat, tolerancia_seg, fade_ms, marge_seg, ducking_db, " +
                  "durada_max, color, categoria_id, neteja_cua, bloc_falques, es_plantilla, actiu, notes, modificat) VALUES " +
                  "($nom, $mode, $hora, $rec, $dies, $isetm, $ancora, $ord, $dmes, $dini, $dfi, $rep, $imin, $hores, $prio, $tol, $fade, " +
                  "$marge, $duck, $dmax, $color, $cat, $neteja, $bloc, $plant, $actiu, $notes, $mod); SELECT last_insert_rowid();"
                : "UPDATE esdeveniment SET nom = $nom, mode = $mode, hora_inici = $hora, recurrencia = $rec, dies_mask = $dies, " +
                  "interval_setm = $isetm, data_ancora = $ancora, ordinals = $ord, dies_mes = $dmes, data_inici = $dini, data_fi = $dfi, " +
                  "repeticio = $rep, interval_min = $imin, hores_mask = $hores, prioritat = $prio, tolerancia_seg = $tol, fade_ms = $fade, " +
                  "marge_seg = $marge, ducking_db = $duck, durada_max = $dmax, color = $color, categoria_id = $cat, neteja_cua = $neteja, " +
                  "bloc_falques = $bloc, es_plantilla = $plant, actiu = $actiu, notes = $notes, modificat = $mod WHERE id = $id; SELECT changes();";

            using (var command = database.Command(sql, transaction))
            {
                var p = command.Parameters;
                p.AddWithValue("$id", id);
                p.AddWithValue("$nom", ev.Name.Trim());
                p.AddWithValue("$mode", ModeText(ev.Mode));
                p.AddWithValue("$hora", ev.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                p.AddWithValue("$rec", RecurrenceText(ev.Recurrence));
                p.AddWithValue("$dies", ev.DaysMask);
                p.AddWithValue("$isetm", Db(ev.IntervalWeeks));
                p.AddWithValue("$ancora", ev.AnchorDate is { } a ? DbTime.ToText(a) : DBNull.Value);
                p.AddWithValue("$ord", ev.Ordinals.Count > 0 ? NumberListText(ev.Ordinals) : DBNull.Value);
                p.AddWithValue("$dmes", ev.MonthDays.Count > 0 ? NumberListText(ev.MonthDays) : DBNull.Value);
                p.AddWithValue("$dini", ev.StartDate is { } s ? DbTime.ToText(s) : DBNull.Value);
                p.AddWithValue("$dfi", ev.EndDate is { } e ? DbTime.ToText(e) : DBNull.Value);
                p.AddWithValue("$rep", RepeatText(ev.Repeat));
                p.AddWithValue("$imin", Db(ev.IntervalMinutes));
                p.AddWithValue("$hores", Db(ev.HoursMask));
                p.AddWithValue("$prio", ev.Priority);
                p.AddWithValue("$tol", ev.ToleranceSeconds);
                p.AddWithValue("$fade", Db(ev.FadeMs));
                p.AddWithValue("$marge", Db(ev.MarginSeconds));
                p.AddWithValue("$duck", ev.DuckingDb is { } d ? d : DBNull.Value);
                p.AddWithValue("$dmax", Db(ev.MaxDurationSeconds));
                p.AddWithValue("$color", (object?)ev.Color ?? DBNull.Value);
                p.AddWithValue("$cat", ev.CategoryId is { } c ? c : DBNull.Value);
                p.AddWithValue("$neteja", ev.ClearQueue ? 1 : 0);
                p.AddWithValue("$bloc", ev.CommercialBlock ? 1 : 0);
                p.AddWithValue("$plant", ev.IsTemplate ? 1 : 0);
                p.AddWithValue("$actiu", ev.Enabled ? 1 : 0);
                p.AddWithValue("$notes", (object?)ev.Notes ?? DBNull.Value);
                p.AddWithValue("$mod", DbTime.ToText(DateTimeOffset.UtcNow));
                long result = (long)command.ExecuteScalar()!;
                if (id == 0) id = result;
                else if (result == 0) throw new ArgumentException($"No existeix l'esdeveniment {id}.");
            }

            using (var clear = database.Command(
                "DELETE FROM accio WHERE esdeveniment_id = $id; DELETE FROM esdeveniment_data WHERE esdeveniment_id = $id;", transaction))
            {
                clear.Parameters.AddWithValue("$id", id);
                clear.ExecuteNonQuery();
            }

            int order = 0;
            foreach (var action in ev.Actions)
            {
                using var insert = database.Command(
                    "INSERT INTO accio (esdeveniment_id, ordre, tipus, params, dies_mask) VALUES ($e, $ordre, $tipus, $params, $dies);", transaction);
                insert.Parameters.AddWithValue("$e", id);
                insert.Parameters.AddWithValue("$ordre", order++);
                insert.Parameters.AddWithValue("$tipus", ActionTypeText(action.Type));
                insert.Parameters.AddWithValue("$params", action.Params);
                insert.Parameters.AddWithValue("$dies", Db(action.DaysMask));
                insert.ExecuteNonQuery();
            }

            foreach (var (date, kind) in ev.IncludedDates.Select(d => (d, "INCLOU")).Concat(ev.ExcludedDates.Select(d => (d, "EXCLOU"))))
            {
                using var insert = database.Command(
                    "INSERT OR REPLACE INTO esdeveniment_data (esdeveniment_id, data, tipus) VALUES ($e, $data, $tipus);", transaction);
                insert.Parameters.AddWithValue("$e", id);
                insert.Parameters.AddWithValue("$data", DbTime.ToText(date));
                insert.Parameters.AddWithValue("$tipus", kind);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return id;
        }
    }

    /// <summary>Esborra un esdeveniment (amb les accions i les dates). El registre d'execució es conserva. Retorna false si no existia.</summary>
    public bool Delete(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM esdeveniment WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // --- Categories ---

    public IReadOnlyList<EventCategory> Categories()
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT id, nom, color FROM categoria_esdeveniment ORDER BY nom;");
            using var r = command.ExecuteReader();
            var list = new List<EventCategory>();
            while (r.Read()) list.Add(new EventCategory(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
            return list;
        }
    }

    /// <summary>Crea una categoria o, si ja n'hi ha una amb aquest nom, en canvia el color. Retorna l'id.</summary>
    public long SaveCategory(string name, string? color)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("La categoria ha de tenir nom.");
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO categoria_esdeveniment (nom, color) VALUES ($nom, $color) ON CONFLICT(nom) DO UPDATE SET color = excluded.color; " +
                "SELECT id FROM categoria_esdeveniment WHERE nom = $nom;");
            command.Parameters.AddWithValue("$nom", name.Trim());
            command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>Esborra una categoria; els esdeveniments que la tenien queden sense categoria.</summary>
    public bool DeleteCategory(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM categoria_esdeveniment WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // --- Registre d'execució ---

    /// <summary>Afegeix una línia al registre d'execució. Retorna l'id.</summary>
    public long AddExecution(ExecutionRecord record)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO registre_execucio (esdeveniment_id, nom, programat_per, executat_a, resultat, detall) " +
                "VALUES ($e, $nom, $prog, $exec, $res, $detall); SELECT last_insert_rowid();");
            command.Parameters.AddWithValue("$e", record.EventId is { } e ? e : DBNull.Value);
            command.Parameters.AddWithValue("$nom", record.Name);
            command.Parameters.AddWithValue("$prog", DbTime.ToText(record.ScheduledFor));
            command.Parameters.AddWithValue("$exec", record.ExecutedAt is { } x ? DbTime.ToText(x) : DBNull.Value);
            command.Parameters.AddWithValue("$res", ResultText(record.Result));
            command.Parameters.AddWithValue("$detall", (object?)record.Detail ?? DBNull.Value);
            return (long)command.ExecuteScalar()!;
        }
    }

    /// <summary>
    /// Completa una línia del registre: quan ha sonat de veritat i el detall (p. ex. un TALLA, que s'anota en programar-lo
    /// i es completa quan la primera pista comença, amb el retard mesurat).
    /// </summary>
    public void UpdateExecution(long id, DateTimeOffset? executedAt, string? detail)
    {
        lock (database.Sync)
        {
            using var command = database.Command("UPDATE registre_execucio SET executat_a = $exec, detall = $detall WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$exec", executedAt is { } x ? DbTime.ToText(x) : DBNull.Value);
            command.Parameters.AddWithValue("$detall", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Línies del registre previstes entre <paramref name="from"/> (inclòs) i <paramref name="to"/> (exclòs), per hora prevista.
    /// Amb <paramref name="eventId"/>, només les d'aquest esdeveniment.
    /// </summary>
    public IReadOnlyList<ExecutionRecord> Executions(DateTimeOffset from, DateTimeOffset to, long? eventId = null, int limit = 1000)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT id, esdeveniment_id, nom, programat_per, executat_a, resultat, detall FROM registre_execucio " +
                "WHERE programat_per >= $from AND programat_per < $to AND ($e IS NULL OR esdeveniment_id = $e) " +
                "ORDER BY programat_per, id LIMIT $limit;");
            command.Parameters.AddWithValue("$from", DbTime.ToText(from));
            command.Parameters.AddWithValue("$to", DbTime.ToText(to));
            command.Parameters.AddWithValue("$e", eventId is { } e ? e : DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using var r = command.ExecuteReader();
            var list = new List<ExecutionRecord>();
            while (r.Read())
            {
                list.Add(new ExecutionRecord
                {
                    Id = r.GetInt64(0),
                    EventId = r.IsDBNull(1) ? null : r.GetInt64(1),
                    Name = r.GetString(2),
                    ScheduledFor = DbTime.ParseInstant(r.GetString(3)),
                    ExecutedAt = r.IsDBNull(4) ? null : DbTime.ParseInstant(r.GetString(4)),
                    Result = ParseResult(r.GetString(5)),
                    Detail = r.IsDBNull(6) ? null : r.GetString(6),
                });
            }
            return list;
        }
    }

    /// <summary>Ja hi ha alguna línia al registre per a aquesta emissió (esdeveniment i hora prevista)? Evita executar-la dues vegades.</summary>
    public bool HasExecution(long eventId, DateTimeOffset scheduledFor)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "SELECT EXISTS (SELECT 1 FROM registre_execucio WHERE esdeveniment_id = $e AND programat_per = $prog);");
            command.Parameters.AddWithValue("$e", eventId);
            command.Parameters.AddWithValue("$prog", DbTime.ToText(scheduledFor));
            return (long)command.ExecuteScalar()! == 1;
        }
    }

    // --- Validació ---

    /// <summary>Comprova que l'esdeveniment és coherent abans de desar-lo. Els missatges són per a l'operador.</summary>
    /// <summary>Comprova que l'esdeveniment és coherent, sense desar-lo (per a la previsualització de l'editor).</summary>
    /// <exception cref="ArgumentException">Amb el motiu en català.</exception>
    public static void Check(ScheduledEvent ev) => Validate(ev);

    private static void Validate(ScheduledEvent ev)
    {
        static void Require(bool ok, string message)
        {
            if (!ok) throw new ArgumentException(message);
        }

        Require(!string.IsNullOrWhiteSpace(ev.Name), "L'esdeveniment ha de tenir nom.");
        Require(ev.StartTime.Millisecond == 0 && ev.StartTime.Microsecond == 0, "L'hora d'inici és en segons sencers.");
        Require(ev.DaysMask is > 0 and <= Weekdays.All || ev.Recurrence is EventRecurrence.DaysOfMonth or EventRecurrence.Dates,
            "Cal triar algun dia de la setmana.");
        Require(ev.DaysMask is >= 0 and <= Weekdays.All, "Els dies de la setmana no són vàlids.");

        switch (ev.Recurrence)
        {
            case EventRecurrence.EveryNWeeks:
                Require(ev.IntervalWeeks is >= 1 and <= 52, "«Cada N setmanes» necessita un interval d'1 a 52 setmanes.");
                Require(ev.AnchorDate is not null, "«Cada N setmanes» necessita un dia de referència en què segur que toca.");
                break;
            case EventRecurrence.MonthlyOrdinal:
                Require(ev.Ordinals.Count > 0 && ev.Ordinals.All(o => o == -1 || o is >= 1 and <= 5),
                    "Cal triar quin dia del mes: primer, segon, tercer, quart, cinquè o últim.");
                break;
            case EventRecurrence.DaysOfMonth:
                Require(ev.MonthDays.Count > 0 && ev.MonthDays.All(d => d == -1 || d is >= 1 and <= 31),
                    "Cal triar els dies del mes (de l'1 al 31, o l'últim).");
                break;
            case EventRecurrence.Dates:
                Require(ev.IncludedDates.Count > 0, "Cal afegir alguna data.");
                break;
        }
        Require(ev.StartDate is null || ev.EndDate is null || ev.StartDate <= ev.EndDate,
            "La data de fi no pot ser anterior a la d'inici.");

        switch (ev.Repeat)
        {
            case EventRepeat.EveryHour:
                Require(ev.HoursMask is null or (> 0 and < (1 << 24)), "Cal triar alguna hora.");
                break;
            case EventRepeat.EveryNMinutes:
                Require(ev.IntervalMinutes is >= 1 and <= 720, "«Cada N minuts» necessita un interval d'1 a 720 minuts.");
                break;
        }

        Require(ev.ToleranceSeconds >= 0, "La tolerància no pot ser negativa.");
        Require(ev.FadeMs is null or >= 0, "El fos no pot ser negatiu.");
        Require(ev.MaxDurationSeconds is null or > 0, "La durada màxima ha de ser positiva.");

        foreach (var action in ev.Actions)
        {
            Require(action.DaysMask is null or (> 0 and <= Weekdays.All), "Els dies d'una acció no són vàlids.");
            bool isObject;
            try
            {
                using var json = JsonDocument.Parse(action.Params);
                isObject = json.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                isObject = false;
            }
            Require(isObject, $"Els paràmetres de l'acció {ActionTypeText(action.Type)} no són un objecte JSON vàlid.");
        }
    }

    // --- Lectura ---

    private List<ScheduledEvent> ReadEvents(SqliteCommand command)
    {
        var events = new List<ScheduledEvent>();
        using (var r = command.ExecuteReader())
        {
            while (r.Read())
            {
                events.Add(new ScheduledEvent
                {
                    Id = r.GetInt64(0),
                    Name = r.GetString(1),
                    Mode = ParseMode(r.GetString(2)),
                    StartTime = TimeOnly.ParseExact(r.GetString(3), "HH:mm:ss", CultureInfo.InvariantCulture),
                    Recurrence = ParseRecurrence(r.GetString(4)),
                    DaysMask = r.GetInt32(5),
                    IntervalWeeks = NullableInt(r, 6),
                    AnchorDate = r.IsDBNull(7) ? null : DbTime.ParseDate(r.GetString(7)),
                    Ordinals = r.IsDBNull(8) ? [] : ParseNumberList(r.GetString(8)),
                    MonthDays = r.IsDBNull(9) ? [] : ParseNumberList(r.GetString(9)),
                    StartDate = r.IsDBNull(10) ? null : DbTime.ParseDate(r.GetString(10)),
                    EndDate = r.IsDBNull(11) ? null : DbTime.ParseDate(r.GetString(11)),
                    Repeat = ParseRepeat(r.GetString(12)),
                    IntervalMinutes = NullableInt(r, 13),
                    HoursMask = NullableInt(r, 14),
                    Priority = r.GetInt32(15),
                    ToleranceSeconds = r.GetInt32(16),
                    FadeMs = NullableInt(r, 17),
                    MarginSeconds = NullableInt(r, 18),
                    DuckingDb = r.IsDBNull(19) ? null : r.GetDouble(19),
                    MaxDurationSeconds = NullableInt(r, 20),
                    Color = r.IsDBNull(21) ? null : r.GetString(21),
                    CategoryId = r.IsDBNull(22) ? null : r.GetInt64(22),
                    ClearQueue = r.GetInt64(23) == 1,
                    CommercialBlock = r.GetInt64(24) == 1,
                    IsTemplate = r.GetInt64(25) == 1,
                    Enabled = r.GetInt64(26) == 1,
                    Notes = r.IsDBNull(27) ? null : r.GetString(27),
                    Modified = DbTime.ParseInstant(r.GetString(28)),
                });
            }
        }
        return events.Select(ev => ev with { Actions = ReadActions(ev.Id) }).Select(ReadDates).ToList();
    }

    private List<EventAction> ReadActions(long eventId)
    {
        using var command = database.Command(
            "SELECT id, tipus, params, dies_mask FROM accio WHERE esdeveniment_id = $e ORDER BY ordre, id;");
        command.Parameters.AddWithValue("$e", eventId);
        using var r = command.ExecuteReader();
        var list = new List<EventAction>();
        while (r.Read())
        {
            list.Add(new EventAction
            {
                Id = r.GetInt64(0),
                Type = ParseActionType(r.GetString(1)),
                Params = r.GetString(2),
                DaysMask = NullableInt(r, 3),
            });
        }
        return list;
    }

    private ScheduledEvent ReadDates(ScheduledEvent ev)
    {
        using var command = database.Command(
            "SELECT data, tipus FROM esdeveniment_data WHERE esdeveniment_id = $e ORDER BY data;");
        command.Parameters.AddWithValue("$e", ev.Id);
        using var r = command.ExecuteReader();
        var included = new List<DateOnly>();
        var excluded = new List<DateOnly>();
        while (r.Read())
        {
            var date = DbTime.ParseDate(r.GetString(0));
            (r.GetString(1) == "INCLOU" ? included : excluded).Add(date);
        }
        return ev with { IncludedDates = included, ExcludedDates = excluded };
    }

    private static int? NullableInt(SqliteDataReader r, int column) => r.IsDBNull(column) ? null : r.GetInt32(column);

    private static object Db(int? value) => value is { } v ? v : DBNull.Value;

    // --- Noms de la base de dades (els de l'especificació) ---

    /// <summary>«1,3,last»: −1 és «last» (l'últim).</summary>
    internal static string NumberListText(IEnumerable<int> numbers) =>
        string.Join(",", numbers.Select(n => n == -1 ? "last" : n.ToString(CultureInfo.InvariantCulture)));

    internal static List<int> ParseNumberList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Equals("last", StringComparison.OrdinalIgnoreCase) ? -1 : int.Parse(s, CultureInfo.InvariantCulture))
            .ToList();

    private static readonly (EventMode Value, string Text)[] ModeNames =
        [(EventMode.Cut, "TALLA"), (EventMode.Queue, "ENCUA"), (EventMode.Overlay, "SUPERPOSA"), (EventMode.System, "SISTEMA")];

    private static readonly (EventRecurrence Value, string Text)[] RecurrenceNames =
    [
        (EventRecurrence.Weekly, "SETMANAL"), (EventRecurrence.EveryNWeeks, "CADA_N_SETMANES"),
        (EventRecurrence.MonthlyOrdinal, "MENSUAL_ORDINAL"), (EventRecurrence.DaysOfMonth, "DIES_MES"), (EventRecurrence.Dates, "DATES"),
    ];

    private static readonly (EventRepeat Value, string Text)[] RepeatNames =
        [(EventRepeat.None, "CAP"), (EventRepeat.EveryHour, "CADA_HORA"), (EventRepeat.EveryNMinutes, "CADA_N_MIN")];

    private static readonly (EventActionType Value, string Text)[] ActionTypeNames =
    [
        (EventActionType.Track, "PISTA"), (EventActionType.Stream, "STREAM"), (EventActionType.Campaign, "CAMPANYA"),
        (EventActionType.Template, "PLANTILLA"), (EventActionType.Auxiliary, "AUXILIAR"), (EventActionType.Rotation, "ROTACIO"),
        (EventActionType.Sweepers, "SWEEPERS"), (EventActionType.AutoDj, "AUTODJ"), (EventActionType.Wait, "ESPERA"),
        (EventActionType.Stop, "ATURA"), (EventActionType.Input, "ENTRADA"), (EventActionType.Run, "EXECUTA"),
        (EventActionType.Play, "REPRODUEIX"), (EventActionType.Bag, "BOSSA"),
    ];

    private static readonly (ExecutionResult Value, string Text)[] ResultNames =
    [
        (ExecutionResult.Ok, "OK"), (ExecutionResult.SkippedLate, "OMES_TARD"), (ExecutionResult.SkippedConflict, "OMES_CONFLICTE"),
        (ExecutionResult.SkippedClockChange, "OMES_CANVI_HORA"), (ExecutionResult.NotPlayed, "NO_EMES"), (ExecutionResult.SkippedPaused, "OMES_ATURAT"),
        (ExecutionResult.Error, "ERROR"),
    ];

    private static string ToText<T>((T Value, string Text)[] names, T value) where T : struct, Enum =>
        names.First(n => n.Value.Equals(value)).Text;

    private static T Parse<T>((T Value, string Text)[] names, string text, string what) where T : struct, Enum =>
        names.FirstOrDefault(n => n.Text == text) is { Text: not null } found
            ? found.Value
            : throw new DatabaseException($"Valor desconegut a la base de dades ({what}): «{text}»");

    public static string ModeText(EventMode mode) => ToText(ModeNames, mode);
    public static EventMode ParseMode(string text) => Parse(ModeNames, text, "mode");
    public static string RecurrenceText(EventRecurrence value) => ToText(RecurrenceNames, value);
    public static EventRecurrence ParseRecurrence(string text) => Parse(RecurrenceNames, text, "recurrència");
    public static string RepeatText(EventRepeat value) => ToText(RepeatNames, value);
    public static EventRepeat ParseRepeat(string text) => Parse(RepeatNames, text, "repetició");
    public static string ActionTypeText(EventActionType value) => ToText(ActionTypeNames, value);
    public static EventActionType ParseActionType(string text) => Parse(ActionTypeNames, text, "tipus d'acció");
    public static string ResultText(ExecutionResult value) => ToText(ResultNames, value);
    public static ExecutionResult ParseResult(string text) => Parse(ResultNames, text, "resultat");
}
