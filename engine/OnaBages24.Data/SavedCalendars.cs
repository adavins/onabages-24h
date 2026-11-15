using System.Globalization;

namespace OnaBages24.Data;

/// <summary>Una franja d'un calendari desat: l'hora i els dies de la setmana (bit0 = dilluns … bit6 = diumenge).</summary>
public sealed record SavedSlot(TimeOnly Time, int DaysMask);

/// <summary>Un calendari desat (taula <c>calendari_desat</c>): una distribució de franges amb nom (D-145).</summary>
public sealed record SavedCalendar
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Quan es va desar per última vegada (el posa <see cref="SavedCalendars.Save"/>).</summary>
    public DateTimeOffset Modified { get; init; }
    /// <summary>Les franges, per hora.</summary>
    public IReadOnlyList<SavedSlot> Slots { get; init; } = [];
}

/// <summary>
/// Calendaris desats (fase 4, pas 6a; D-145; docs/planificador.md, secció 10): desar, llegir i esborrar distribucions de
/// franges. Aplicar-les al calendari d'una pista o d'una campanya ho fa el motor.
/// </summary>
public sealed class SavedCalendars(Database database)
{
    private const string TimeFormat = "HH:mm:ss";

    /// <summary>Tots, per nom.</summary>
    public IReadOnlyList<SavedCalendar> List()
    {
        lock (database.Sync)
        {
            var list = new List<SavedCalendar>();
            using (var command = database.Command("SELECT id, nom, modificat FROM calendari_desat ORDER BY nom COLLATE NOCASE, id;"))
            using (var r = command.ExecuteReader())
            {
                while (r.Read()) list.Add(new SavedCalendar { Id = r.GetInt64(0), Name = r.GetString(1), Modified = DbTime.ParseInstant(r.GetString(2)) });
            }
            return list.Select(c => c with { Slots = Slots(c.Id) }).ToList();
        }
    }

    public SavedCalendar? Get(long id) => List().FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Desa un calendari: sense id (0), en crea un de nou; amb id, el substitueix sencer. El nom és obligatori i no es pot
    /// repetir (sense distingir majúscules); ha de tenir alguna franja. Retorna l'id.
    /// </summary>
    public long Save(SavedCalendar calendar, DateTimeOffset now)
    {
        string name = calendar.Name.Trim();
        if (name.Length == 0) throw new DatabaseException("El calendari ha de tenir nom.");
        if (name.Length > 100) throw new DatabaseException("El nom pot tenir com a molt 100 caràcters.");
        var slots = calendar.Slots
            .Where(s => (s.DaysMask & Weekdays.All) != 0)
            .GroupBy(s => s.Time)
            .Select(g => new SavedSlot(g.Key, g.Aggregate(0, (mask, s) => mask | s.DaysMask) & Weekdays.All))
            .OrderBy(s => s.Time)
            .ToList();
        if (slots.Count == 0) throw new DatabaseException("El calendari no té cap franja marcada.");

        lock (database.Sync)
        {
            using (var check = database.Command("SELECT count(*) FROM calendari_desat WHERE nom = $nom COLLATE NOCASE AND id <> $id;"))
            {
                check.Parameters.AddWithValue("$nom", name);
                check.Parameters.AddWithValue("$id", calendar.Id);
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) throw new DatabaseException($"Ja hi ha un calendari desat que es diu «{name}».");
            }

            using var transaction = database.BeginTransaction();
            long id = calendar.Id;
            using (var command = database.Command(id == 0
                ? "INSERT INTO calendari_desat (nom, modificat) VALUES ($nom, $modificat) RETURNING id;"
                : "UPDATE calendari_desat SET nom = $nom, modificat = $modificat WHERE id = $id RETURNING id;", transaction))
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$nom", name);
                command.Parameters.AddWithValue("$modificat", DbTime.ToText(now));
                object? result = command.ExecuteScalar();
                if (result is null) throw new DatabaseException($"No existeix el calendari desat {id}.");
                id = Convert.ToInt64(result);
            }
            using (var delete = database.Command("DELETE FROM calendari_desat_franja WHERE calendari_id = $id;", transaction))
            {
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }
            foreach (var slot in slots)
            {
                using var insert = database.Command(
                    "INSERT INTO calendari_desat_franja (calendari_id, hora, dies_mask) VALUES ($id, $hora, $dies);", transaction);
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$hora", slot.Time.ToString(TimeFormat, CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$dies", slot.DaysMask);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return id;
        }
    }

    /// <summary>Esborra un calendari desat. No toca els esdeveniments: el que ja s'hi havia aplicat es queda.</summary>
    public bool Delete(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM calendari_desat WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private List<SavedSlot> Slots(long id)
    {
        using var command = database.Command("SELECT hora, dies_mask FROM calendari_desat_franja WHERE calendari_id = $id ORDER BY hora;");
        command.Parameters.AddWithValue("$id", id);
        using var r = command.ExecuteReader();
        var list = new List<SavedSlot>();
        while (r.Read()) list.Add(new SavedSlot(TimeOnly.ParseExact(r.GetString(0), TimeFormat, CultureInfo.InvariantCulture), r.GetInt32(1)));
        return list;
    }
}
