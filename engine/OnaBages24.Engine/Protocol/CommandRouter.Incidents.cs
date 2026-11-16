using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Supervision;

namespace OnaBages24.Engine.Protocol;

// Incidències (fase 5, pas 1; D-156): la llista, per a la barra d'avisos (les obertes) i la finestra «Incidències».
public sealed partial class CommandRouter
{
    private IncidentStore? _incidentStore;
    private IncidentTracker? _incidents;
    private SilenceDetector? _silence;
    private OnaBages24.Engine.Playback.EmergencyTracks? _emergency;

    // --- Pistes d'emergència (fase 5, pas 4; D-159) ---

    public const string EmergencySettingKey = "emergencia";

    /// <summary>La carpeta per defecte: <c>emergencia</c> dins de la carpeta de dades.</summary>
    public static string DefaultEmergencyFolder => Path.Combine(RuntimeFile.DirectoryPath, "emergencia");

    /// <summary>La carpeta desada, o la per defecte.</summary>
    public static string ReadEmergencyFolder(Library? library)
    {
        try
        {
            if (library?.GetSetting(EmergencySettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved
                && saved["carpeta"]?.GetValue<string>() is { Length: > 0 } folder)
                return folder;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return DefaultEmergencyFolder;
    }

    /// <summary>Si és la carpeta per defecte i no hi és, es crea (buida: l'avís dirà que s'hi han de posar cançons).</summary>
    public static void EnsureDefaultEmergencyFolder(string folder)
    {
        if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(DefaultEmergencyFolder), StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private JsonObject EmergencyJson()
    {
        var tracks = _emergency ?? throw new BadParamsException("El motor no té pistes d'emergència.");
        return new JsonObject
        {
            ["folder"] = tracks.Folder,
            ["defaultFolder"] = DefaultEmergencyFolder,
            ["count"] = tracks.Count,
            ["problem"] = tracks.Problem,
        };
    }

    /// <summary><c>emergency.set</c>: <c>folder</c> (ruta completa; <c>null</c> o buida = la per defecte). Es torna a llegir de seguida.</summary>
    private JsonObject EmergencySet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var tracks = _emergency ?? throw new BadParamsException("El motor no té pistes d'emergència.");
        if (msg["folder"] is not null && msg["folder"] is not JsonValue)
            throw new BadParamsException("El camp 'folder' ha de ser un text o null.");
        string? folder = msg["folder"] is JsonValue v && v.TryGetValue(out string? text) ? text?.Trim() : null;
        if (!string.IsNullOrEmpty(folder) && !Path.IsPathFullyQualified(folder))
            throw new BadParamsException("La carpeta ha de ser una ruta completa (p. ex. D:\\Emergència).");
        library.SetSetting(EmergencySettingKey, System.Text.Json.JsonSerializer.Serialize(new { carpeta = string.IsNullOrEmpty(folder) ? null : folder }));
        string target = string.IsNullOrEmpty(folder) ? DefaultEmergencyFolder : folder;
        EnsureDefaultEmergencyFolder(target);
        tracks.SetFolder(target);
        return EmergencyJson();
    }

    // --- Silenci a la sortida (fase 5, pas 3; D-158) ---

    public const string SilenceSettingKey = "silenci";
    public const double MinSilenceThresholdDb = -70, MaxSilenceThresholdDb = -30;
    public const double MinSilenceSeconds = 5, MaxSilenceSeconds = 120;

    /// <summary>El llindar i els segons desats (o els per defecte, si no n'hi ha o no es poden llegir).</summary>
    public static (double ThresholdDb, double Seconds) ReadSilence(Library? library)
    {
        try
        {
            if (library?.GetSetting(SilenceSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
                return (Math.Clamp(saved["llindar"]?.GetValue<double>() ?? SilenceDetector.StandardThresholdDb, MinSilenceThresholdDb, MaxSilenceThresholdDb),
                        Math.Clamp(saved["segons"]?.GetValue<double>() ?? SilenceDetector.StandardSeconds, MinSilenceSeconds, MaxSilenceSeconds));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return (SilenceDetector.StandardThresholdDb, SilenceDetector.StandardSeconds);
    }

    private static JsonObject SilenceJson((double ThresholdDb, double Seconds) silence) => new()
    {
        ["thresholdDb"] = silence.ThresholdDb,
        ["seconds"] = silence.Seconds,
        ["standardThresholdDb"] = SilenceDetector.StandardThresholdDb,
        ["standardSeconds"] = SilenceDetector.StandardSeconds,
    };

    /// <summary><c>silence.set</c>: <c>thresholdDb</c> (de −70 a −30) i <c>seconds</c> (de 5 a 120), tots dos opcionals.</summary>
    private JsonObject SilenceSet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var (thresholdDb, seconds) = ReadSilence(library);
        static double Read(JsonNode? node, string name, double current, double min, double max, string unit)
        {
            if (node is null) return current;
            if (node is not JsonValue v || !v.TryGetValue(out double value) || value < min || value > max)
                throw new BadParamsException($"El camp '{name}' ha de ser un nombre entre {min} i {max} ({unit}).");
            return value;
        }
        thresholdDb = Read(msg["thresholdDb"], "thresholdDb", thresholdDb, MinSilenceThresholdDb, MaxSilenceThresholdDb, "dB");
        seconds = Read(msg["seconds"], "seconds", seconds, MinSilenceSeconds, MaxSilenceSeconds, "segons");
        library.SetSetting(SilenceSettingKey, System.Text.Json.JsonSerializer.Serialize(new { llindar = thresholdDb, segons = seconds }));
        if (_silence is not null)
        {
            _silence.ThresholdDb = thresholdDb;
            _silence.Seconds = seconds;
        }
        return SilenceJson((thresholdDb, seconds));
    }

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
