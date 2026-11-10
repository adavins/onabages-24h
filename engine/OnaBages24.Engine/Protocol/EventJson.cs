using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

/// <summary>
/// Esdeveniments del planificador en JSON (docs/protocol.md, 4.7). Els valors de mode, recurrència, repetició, tipus
/// d'acció i resultat són els de l'especificació i la base de dades (<c>TALLA</c>, <c>SETMANAL</c>, <c>PISTA</c>…).
/// </summary>
public static class EventJson
{
    public static JsonObject Event(ScheduledEvent ev) => new()
    {
        ["id"] = ev.Id,
        ["name"] = ev.Name,
        ["mode"] = Events.ModeText(ev.Mode),
        ["startTime"] = ev.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ["recurrence"] = Events.RecurrenceText(ev.Recurrence),
        ["daysMask"] = ev.DaysMask,
        ["intervalWeeks"] = ev.IntervalWeeks,
        ["anchorDate"] = ev.AnchorDate is { } a ? DbTime.ToText(a) : null,
        ["ordinals"] = new JsonArray(ev.Ordinals.Select(o => (JsonNode)o).ToArray()),
        ["monthDays"] = new JsonArray(ev.MonthDays.Select(d => (JsonNode)d).ToArray()),
        ["startDate"] = ev.StartDate is { } s ? DbTime.ToText(s) : null,
        ["endDate"] = ev.EndDate is { } e ? DbTime.ToText(e) : null,
        ["repeat"] = Events.RepeatText(ev.Repeat),
        ["intervalMinutes"] = ev.IntervalMinutes,
        ["hoursMask"] = ev.HoursMask,
        ["priority"] = ev.Priority,
        ["toleranceSeconds"] = ev.ToleranceSeconds,
        ["fadeMs"] = ev.FadeMs,
        ["marginSeconds"] = ev.MarginSeconds,
        ["duckingDb"] = ev.DuckingDb,
        ["maxDurationSeconds"] = ev.MaxDurationSeconds,
        ["color"] = ev.Color,
        ["categoryId"] = ev.CategoryId,
        ["clearQueue"] = ev.ClearQueue,
        ["commercialBlock"] = ev.CommercialBlock,
        ["isTemplate"] = ev.IsTemplate,
        ["enabled"] = ev.Enabled,
        ["notes"] = ev.Notes,
        ["modified"] = ev.Modified.ToString("O", CultureInfo.InvariantCulture),
        ["actions"] = new JsonArray(ev.Actions.Select(a => (JsonNode)new JsonObject
        {
            ["type"] = Events.ActionTypeText(a.Type),
            ["params"] = JsonNode.Parse(a.Params),
            ["daysMask"] = a.DaysMask,
        }).ToArray()),
        ["includedDates"] = new JsonArray(ev.IncludedDates.Select(d => (JsonNode)DbTime.ToText(d)).ToArray()),
        ["excludedDates"] = new JsonArray(ev.ExcludedDates.Select(d => (JsonNode)DbTime.ToText(d)).ToArray()),
    };

    /// <summary>
    /// Llegeix un esdeveniment que envia la interfície. Els camps que no hi són prenen el valor per defecte (com un
    /// esdeveniment nou). Si un camp no és del tipus que toca, <see cref="FormatException"/> amb el motiu en català.
    /// La coherència (camps que necessita cada recurrència, etc.) la comprova <see cref="Events.Save"/>.
    /// </summary>
    public static ScheduledEvent FromJson(JsonObject o)
    {
        var defaults = new ScheduledEvent();
        return new ScheduledEvent
        {
            Id = Long(o, "id") ?? 0,
            Name = String(o, "name") ?? "",
            Mode = Enum(o, "mode", Events.ParseMode) ?? throw new FormatException("Falta el camp 'mode' (TALLA, ENCUA, SUPERPOSA o SISTEMA)."),
            StartTime = Time(o, "startTime") ?? throw new FormatException("Falta el camp 'startTime' (HH:MM:SS)."),
            Recurrence = Enum(o, "recurrence", Events.ParseRecurrence) ?? defaults.Recurrence,
            DaysMask = Int(o, "daysMask") ?? defaults.DaysMask,
            IntervalWeeks = Int(o, "intervalWeeks"),
            AnchorDate = Date(o, "anchorDate"),
            Ordinals = IntList(o, "ordinals"),
            MonthDays = IntList(o, "monthDays"),
            StartDate = Date(o, "startDate"),
            EndDate = Date(o, "endDate"),
            Repeat = Enum(o, "repeat", Events.ParseRepeat) ?? defaults.Repeat,
            IntervalMinutes = Int(o, "intervalMinutes"),
            HoursMask = Int(o, "hoursMask"),
            Priority = Int(o, "priority") ?? defaults.Priority,
            ToleranceSeconds = Int(o, "toleranceSeconds") ?? defaults.ToleranceSeconds,
            FadeMs = Int(o, "fadeMs"),
            MarginSeconds = Int(o, "marginSeconds"),
            DuckingDb = Double(o, "duckingDb"),
            MaxDurationSeconds = Int(o, "maxDurationSeconds"),
            Color = String(o, "color"),
            CategoryId = Long(o, "categoryId"),
            ClearQueue = Bool(o, "clearQueue") ?? false,
            CommercialBlock = Bool(o, "commercialBlock") ?? false,
            IsTemplate = Bool(o, "isTemplate") ?? false,
            Enabled = Bool(o, "enabled") ?? true,
            Notes = String(o, "notes"),
            Actions = Actions(o),
            IncludedDates = DateList(o, "includedDates"),
            ExcludedDates = DateList(o, "excludedDates"),
        };
    }

    public static JsonObject Occurrence(Occurrence occurrence) => new()
    {
        ["eventId"] = occurrence.Event.Id,
        ["name"] = occurrence.Event.Name,
        ["mode"] = Events.ModeText(occurrence.Event.Mode),
        ["date"] = DbTime.ToText(occurrence.Date),
        ["time"] = occurrence.LocalTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        ["at"] = occurrence.At.ToString("O", CultureInfo.InvariantCulture),
        ["skippedClockChange"] = occurrence.SkippedClockChange,
    };

    public static JsonObject Execution(ExecutionRecord record) => new()
    {
        ["id"] = record.Id,
        ["eventId"] = record.EventId,
        ["name"] = record.Name,
        ["scheduledFor"] = record.ScheduledFor.ToString("O", CultureInfo.InvariantCulture),
        ["executedAt"] = record.ExecutedAt?.ToString("O", CultureInfo.InvariantCulture),
        ["result"] = Events.ResultText(record.Result),
        ["detail"] = record.Detail,
    };

    // --- Lectura dels camps ---

    private static IReadOnlyList<EventAction> Actions(JsonObject o)
    {
        if (o["actions"] is null) return [];
        if (o["actions"] is not JsonArray list) throw new FormatException("El camp 'actions' ha de ser una llista.");
        return list.Select((node, i) =>
        {
            if (node is not JsonObject a) throw new FormatException($"L'acció {i + 1} ha de ser un objecte.");
            var parameters = a["params"] ?? new JsonObject();
            if (parameters is not JsonObject) throw new FormatException($"Els paràmetres de l'acció {i + 1} han de ser un objecte.");
            return new EventAction
            {
                Type = Enum(a, "type", Events.ParseActionType) ?? throw new FormatException($"Falta el tipus de l'acció {i + 1}."),
                Params = parameters.ToJsonString(),
                DaysMask = Int(a, "daysMask"),
            };
        }).ToList();
    }

    private static T? Enum<T>(JsonObject o, string field, Func<string, T> parse) where T : struct
    {
        if (String(o, field) is not { } text) return null;
        try
        {
            return parse(text);
        }
        catch (DatabaseException)
        {
            throw new FormatException($"El camp '{field}' no admet el valor «{text}».");
        }
    }

    private static string? String(JsonObject o, string field) => o[field] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        _ => throw new FormatException($"El camp '{field}' ha de ser un text."),
    };

    private static int? Int(JsonObject o, string field) => o[field] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out int i) => i,
        _ => throw new FormatException($"El camp '{field}' ha de ser un nombre enter."),
    };

    private static long? Long(JsonObject o, string field) => o[field] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out long l) => l,
        _ => throw new FormatException($"El camp '{field}' ha de ser un nombre enter."),
    };

    private static double? Double(JsonObject o, string field) => o[field] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out double d) => d,
        _ => throw new FormatException($"El camp '{field}' ha de ser un nombre."),
    };

    private static bool? Bool(JsonObject o, string field) => o[field] switch
    {
        null => null,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>(),
        _ => throw new FormatException($"El camp '{field}' ha de ser true o false."),
    };

    private static TimeOnly? Time(JsonObject o, string field) =>
        String(o, field) is not { } text ? null
        : TimeOnly.TryParseExact(text, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t
        : throw new FormatException($"El camp '{field}' ha de ser una hora (HH:MM:SS).");

    private static DateOnly? Date(JsonObject o, string field) =>
        String(o, field) is not { } text ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
        : throw new FormatException($"El camp '{field}' ha de ser una data (AAAA-MM-DD).");

    private static List<int> IntList(JsonObject o, string field)
    {
        if (o[field] is null) return [];
        if (o[field] is not JsonArray list) throw new FormatException($"El camp '{field}' ha de ser una llista de nombres.");
        return list.Select(n => n is JsonValue v && v.TryGetValue(out int i) ? i
            : throw new FormatException($"El camp '{field}' ha de ser una llista de nombres (−1 = l'últim).")).ToList();
    }

    private static List<DateOnly> DateList(JsonObject o, string field)
    {
        if (o[field] is null) return [];
        if (o[field] is not JsonArray list) throw new FormatException($"El camp '{field}' ha de ser una llista de dates.");
        return list.Select(n => n is JsonValue v && v.TryGetValue(out string? s)
                && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw new FormatException($"El camp '{field}' ha de ser una llista de dates (AAAA-MM-DD).")).ToList();
    }
}
