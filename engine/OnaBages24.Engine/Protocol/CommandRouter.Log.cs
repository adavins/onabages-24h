using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Security;

namespace OnaBages24.Engine.Protocol;

// Registre unificat (D-167): les incidències, els canvis, les sessions i l'execució dels esdeveniments en una sola llista.
public sealed partial class CommandRouter
{
    private ActivityLog? _activity;

    private static string SourceCode(LogSource source) => source switch
    {
        LogSource.Change => "change",
        LogSource.Session => "session",
        LogSource.Event => "event",
        _ => "incident",
    };

    private static LogSource ParseSource(string? code) => code switch
    {
        "incident" => LogSource.Incident,
        "change" => LogSource.Change,
        "session" => LogSource.Session,
        "event" => LogSource.Event,
        _ => throw new BadParamsException("El camp 'sources' ha de ser una llista de 'incident', 'change', 'session' i 'event'."),
    };

    /// <summary>
    /// Si aquesta connexió pot veure els canvis i les sessions (D-164: només l'administrador). Sense context (les proves)
    /// o sense usuaris, sí.
    /// </summary>
    private bool CanSeeChanges(Session? session, ClientContext? client) =>
        client is null || _sessions is null || !_sessions.UsersExist || session?.Role == UserRole.Administrator;

    /// <summary>
    /// <c>log.list</c>: <c>sources</c>, <c>from</c>, <c>to</c>, <c>minSeverity</c> (<c>warning</c> o <c>danger</c>),
    /// <c>openOnly</c>, <c>kinds</c> (tipus d'incidència), <c>commands</c> (prefixos d'ordres), <c>userId</c>, <c>text</c>,
    /// <c>offset</c> i <c>limit</c> (fins a 500). Sense ser administrador, no hi surten els canvis ni les sessions.
    /// </summary>
    private JsonObject LogList(JsonObject msg, Session? session, ClientContext? client)
    {
        var log = _activity ?? throw new BadParamsException("El motor no té registre.");
        static List<string> Strings(JsonNode? node, string name) => node switch
        {
            null => [],
            JsonArray array => array.Select(n => ReadString(n) ?? throw new BadParamsException($"El camp '{name}' ha de ser una llista de textos.")).ToList(),
            _ => throw new BadParamsException($"El camp '{name}' ha de ser una llista."),
        };
        var sources = Strings(msg["sources"], "sources").Select(ParseSource).ToList();
        bool changesAllowed = CanSeeChanges(session, client);
        if (!changesAllowed)
        {
            if (sources.Count == 0) sources = [LogSource.Incident, LogSource.Event];
            sources.RemoveAll(s => s is LogSource.Change or LogSource.Session);
            if (sources.Count == 0) return new JsonObject { ["total"] = 0, ["entries"] = new JsonArray(), ["changesAllowed"] = false };
        }
        var filter = new LogFilter
        {
            Sources = sources,
            From = ReadTime(msg, "from", endOfDay: false),
            To = ReadTime(msg, "to", endOfDay: true),
            MinSeverity = ReadString(msg["minSeverity"]) switch
            {
                null or "info" => null,
                "warning" => IncidentSeverity.Warning,
                "danger" => IncidentSeverity.Danger,
                _ => throw new BadParamsException("El camp 'minSeverity' ha de ser 'info', 'warning' o 'danger'."),
            },
            OpenOnly = msg["openOnly"] is JsonValue o && o.TryGetValue(out bool open) && open,
            IncidentKinds = Strings(msg["kinds"], "kinds"),
            CommandPrefixes = Strings(msg["commands"], "commands"),
            UserId = msg["userId"] is JsonValue u && u.TryGetValue(out long userId) ? userId : null,
            Text = ReadString(msg["text"]),
            Offset = Math.Max(0, ReadInt(msg["offset"]) ?? 0),
            Limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 500),
        };
        var (entries, total) = log.List(filter);
        return new JsonObject
        {
            ["total"] = total,
            ["changesAllowed"] = changesAllowed,
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
            {
                ["source"] = SourceCode(e.Source),
                ["id"] = e.Id,
                ["at"] = e.At.ToString("O"),
                ["endedAt"] = e.EndedAt?.ToString("O"),
                ["open"] = e.IsOpen,
                ["kind"] = e.Kind,
                ["severity"] = SeverityText(e.Severity),
                ["text"] = e.Text,
                ["detail"] = e.Detail,
                ["recovered"] = e.Recovered,
                ["userId"] = e.UserId,
                ["user"] = e.UserName,
            }).ToArray()),
        };
    }
}
