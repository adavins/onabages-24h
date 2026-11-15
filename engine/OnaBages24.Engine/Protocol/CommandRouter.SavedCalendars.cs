using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

// Calendaris desats i dates de la temporada (fase 4, pas 6a; D-145; docs/planificador.md, secció 10).
public sealed partial class CommandRouter
{
    /// <summary>Quan es desa o s'esborra un calendari desat (<c>savedCalendars.changed</c>).</summary>
    public event Action? SavedCalendarsChanged;

    /// <summary>Clau de la configuració amb les dates de la temporada: <c>{ "startDate", "endDate" }</c>.</summary>
    public const string SeasonSettingKey = "temporada";

    private SavedCalendars SavedCalendarsOrError() => _savedCalendars ?? throw new BadParamsException("El motor no té calendaris desats.");

    private static JsonObject SavedCalendarJson(SavedCalendar c) => new()
    {
        ["id"] = c.Id,
        ["name"] = c.Name,
        ["modified"] = c.Modified.ToUniversalTime().ToString("O"),
        ["cells"] = c.Slots.Sum(s => System.Numerics.BitOperations.PopCount((uint)s.DaysMask)),
        ["slots"] = new JsonArray(c.Slots.Select(s => (JsonNode)new JsonObject
        {
            ["time"] = s.Time.ToString("HH:mm:ss"),
            ["daysMask"] = s.DaysMask,
        }).ToArray()),
    };

    /// <summary>Tots els calendaris desats, per nom.</summary>
    private JsonObject SavedCalendarsList() => new()
    {
        ["calendars"] = new JsonArray(SavedCalendarsOrError().List().Select(c => (JsonNode)SavedCalendarJson(c)).ToArray()),
    };

    /// <summary>
    /// Desa com a calendari les caselles marcades ara al calendari d'una pista (<c>trackId</c>) o d'una campanya
    /// (<c>campaignId</c>), amb un nom (<c>name</c>). Amb <c>calendarId</c>, substitueix aquell calendari desat.
    /// </summary>
    private JsonObject SavedCalendarsSave(JsonObject msg)
    {
        var saved = SavedCalendarsOrError();
        var subject = CalendarSubjectOf(msg).Subject;
        var rows = TrackCalendar.Build(EventsOrError().List(), subject, Today());
        var slots = rows
            .Select(r => new SavedSlot(r.Time, Enumerable.Range(0, 7).Where(d => r.Days[d]?.Marked == true).Aggregate(0, (mask, d) => mask | (1 << d))))
            .Where(s => s.DaysMask != 0)
            .ToList();
        if (slots.Count == 0) throw new BadParamsException("No hi ha cap casella marcada: no hi ha res per desar.");
        long id = msg["calendarId"] is null ? 0 : RequiredId(msg, "calendarId");
        if (id != 0 && saved.Get(id) is null) throw new BadParamsException($"No existeix el calendari desat {id}.");
        id = saved.Save(new SavedCalendar { Id = id, Name = ReadString(msg["name"]) ?? "", Slots = slots }, CampaignClock());
        SavedCalendarsChanged?.Invoke();
        return new JsonObject { ["calendar"] = SavedCalendarJson(saved.Get(id)!) };
    }

    private JsonObject SavedCalendarsDelete(JsonObject msg)
    {
        long id = RequiredId(msg, "calendarId");
        if (!SavedCalendarsOrError().Delete(id)) throw new BadParamsException($"No existeix el calendari desat {id}.");
        SavedCalendarsChanged?.Invoke();
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>
    /// Aplica un calendari desat (<c>calendarId</c>) al calendari d'una pista o d'una campanya: marca les seves caselles, com si
    /// es cliquessin (si en una hora no hi ha esdeveniment, el bloc de falques). <c>mode</c>: <c>add</c> (per defecte,
    /// les que ja hi eren es queden) o <c>replace</c> (es desmarquen les que no són al calendari desat). Retorna el
    /// resum i el calendari com <c>calendar.get</c>.
    /// </summary>
    private JsonObject SavedCalendarsApply(JsonObject msg)
    {
        var library = LibraryOrError();
        var events = EventsOrError();
        long id = RequiredId(msg, "calendarId");
        var calendar = SavedCalendarsOrError().Get(id) ?? throw new BadParamsException($"No existeix el calendari desat {id}.");
        string mode = ReadString(msg["mode"]) ?? "add";
        if (mode is not ("add" or "replace")) throw new BadParamsException("El camp 'mode' ha de ser \"add\" o \"replace\".");
        var subject = CalendarSubjectOf(msg).Subject;
        var wanted = calendar.Slots
            .SelectMany(s => Enumerable.Range(0, 7).Where(d => (s.DaysMask & (1 << d)) != 0).Select(d => (s.Time, Day: d)))
            .ToHashSet();

        int removed = 0, marked = 0, already = 0;
        var failed = new JsonArray();
        if (mode == "replace")
        {
            var current = TrackCalendar.Build(events.List(), subject, Today())
                .SelectMany(r => Enumerable.Range(0, 7).Where(d => r.Days[d]?.Marked == true).Select(d => (r.Time, Day: d)))
                .Where(c => !wanted.Contains(c))
                .ToList();
            foreach (var (time, day) in current)
            {
                // Un esdeveniment que es repeteix es desmarca a totes les hores alhora: la casella pot ja no ser-hi
                if (TrackCalendar.CellAt(TrackCalendar.Build(events.List(), subject, Today()), time, day) is not { Marked: true }) continue;
                SetCalendarCell(library, events, subject, time, day, false);
                removed++;
            }
        }
        foreach (var (time, day) in wanted.OrderBy(c => c.Time).ThenBy(c => c.Day))
        {
            if (TrackCalendar.CellAt(TrackCalendar.Build(events.List(), subject, Today()), time, day) is { Marked: true })
            {
                already++;
                continue;
            }
            try
            {
                SetCalendarCell(library, events, subject, time, day, true);
                marked++;
            }
            catch (Exception ex) when (ex is BadParamsException or DatabaseException)
            {
                failed.Add(new JsonObject { ["time"] = time.ToString("HH:mm:ss"), ["day"] = day, ["reason"] = ex.Message });
            }
        }
        foreach (var empty in events.List().Where(TrackCalendar.IsEmptyBlock)) events.Delete(empty.Id);
        _scheduler?.Reload();

        var result = CalendarGet(msg);
        result["applied"] = new JsonObject { ["marked"] = marked, ["already"] = already, ["removed"] = removed, ["failed"] = failed };
        return result;
    }

    // --- Temporada ---

    /// <summary>Les dates de la temporada (per al botó «Temporada»), o null si no n'hi ha.</summary>
    private JsonObject SeasonGet()
    {
        JsonObject? saved = null;
        if (LibraryOrError().GetSetting(SeasonSettingKey) is { } json)
        {
            try { saved = JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { saved = null; }
        }
        return new JsonObject
        {
            ["season"] = new JsonObject
            {
                ["startDate"] = ReadString(saved?["startDate"]),
                ["endDate"] = ReadString(saved?["endDate"]),
            },
        };
    }

    /// <summary>Desa les dates de la temporada: <c>startDate</c> i <c>endDate</c> (AAAA-MM-DD), totes dues o cap (null).</summary>
    private JsonObject SeasonSet(JsonObject msg)
    {
        var start = OptionalDate(msg, "startDate");
        var end = OptionalDate(msg, "endDate");
        if (start is null != end is null) throw new BadParamsException("La temporada ha de tenir la data d'inici i la de fi (o cap de les dues).");
        if (start is { } s && end is { } e && e < s) throw new BadParamsException("La temporada s'acaba abans de començar.");
        LibraryOrError().SetSetting(SeasonSettingKey, new JsonObject
        {
            ["startDate"] = start?.ToString("yyyy-MM-dd"),
            ["endDate"] = end?.ToString("yyyy-MM-dd"),
        }.ToJsonString());
        return SeasonGet();
    }
}
