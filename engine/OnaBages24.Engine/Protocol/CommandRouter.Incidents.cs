using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Supervision;

namespace OnaBages24.Engine.Protocol;

// Incidències (fase 5, pas 1; D-156): la llista, per a la barra d'avisos (les obertes) i la finestra «Incidències».
public sealed partial class CommandRouter
{
    private IncidentStore? _incidentStore;
    private IncidentTracker? _incidents;

    /// <summary>Com es diu la gravetat al protocol (els mateixos noms que els colors de la interfície).</summary>
    internal static string SeverityText(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Danger => "danger",
        IncidentSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>Una incidència al protocol (docs/protocol.md, 4.13).</summary>
    internal static JsonObject IncidentJson(Incident incident) => new()
    {
        ["id"] = incident.Id,
        ["kind"] = incident.Kind,
        ["severity"] = SeverityText(incident.Severity),
        ["text"] = incident.Text,
        ["detail"] = incident.Detail,
        ["startedAt"] = incident.StartedAt.ToString("O"),
        ["endedAt"] = incident.EndedAt?.ToString("O"),
        ["open"] = incident.IsOpen,
        ["recovered"] = incident.Recovered,
    };

    /// <summary>
    /// <c>incidents.list</c>: amb <c>open: true</c>, les obertes (de la memòria del motor, les més greus primer); si no, les
    /// de la base de dades amb els filtres, les més recents primer i per pàgines.
    /// </summary>
    private JsonObject IncidentsList(JsonObject msg)
    {
        bool onlyOpen = msg["open"] is JsonValue ov && ov.TryGetValue(out bool o) && o;
        if (onlyOpen && _incidents is { } tracker)
        {
            var open = tracker.Open;
            return new JsonObject
            {
                ["incidents"] = new JsonArray(open.Select(i => (JsonNode)IncidentJson(i)).ToArray()),
                ["total"] = open.Count,
            };
        }

        var store = _incidentStore ?? throw new BadParamsException("El motor no té incidències.");
        var from = ReadTime(msg, "from", endOfDay: false);
        var to = ReadTime(msg, "to", endOfDay: true);
        if (from is not null && to is not null && from >= to)
            throw new BadParamsException("El camp 'from' ha de ser anterior a 'to'.");
        List<string> kinds = [];
        if (msg["kinds"] is not null)
        {
            if (msg["kinds"] is not JsonArray list) throw new BadParamsException("El camp 'kinds' ha de ser una llista.");
            kinds = list.Select(n => ReadString(n) ?? throw new BadParamsException("El camp 'kinds' ha de ser una llista de textos.")).ToList();
        }
        bool? openFilter = msg["open"] is JsonValue fv && fv.TryGetValue(out bool f) ? f : null;
        var (incidents, total) = store.List(new IncidentFilter
        {
            Open = openFilter,
            From = from,
            To = to,
            Kinds = kinds,
            Offset = Math.Max(0, ReadInt(msg["offset"]) ?? 0),
            Limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 500),
        });
        return new JsonObject
        {
            ["incidents"] = new JsonArray(incidents.Select(i => (JsonNode)IncidentJson(i)).ToArray()),
            ["total"] = total,
        };
    }
}
