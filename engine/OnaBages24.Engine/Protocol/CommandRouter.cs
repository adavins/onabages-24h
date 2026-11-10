using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta (docs/protocol.md).
/// Cada ordre nova s'afegeix aquí, a docs/protocol.md i a les proves, en el mateix canvi.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>Clau de la configuració on es desa la sortida (JSON: <c>{ "id": …, "name": … }</c>).</summary>
    public const string OutputSettingKey = "sortida";

    /// <summary>Clau de la configuració on es desa l'AutoDJ (JSON: <c>{ "enabled": true, "rotationId": 1 }</c>).</summary>
    public const string AutoDjSettingKey = "autodj";

    /// <summary>Clau de la configuració on es desa l'exportació del títol (vegeu <see cref="NowPlayingSettings.ToJson"/>).</summary>
    public const string NowPlayingSettingKey = "nowplaying";

    private readonly EngineState _state;
    private readonly Action _requestShutdown;
    private readonly Library? _library;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly QueuePlayer? _queue;
    private readonly OutputManager? _outputs;
    private readonly AutoDj? _autoDj;
    private readonly Rotations? _rotations;
    private readonly NowPlaying? _nowPlaying;
    private readonly Events? _events;
    private readonly Scheduler? _scheduler;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Atura el motor (s'executa després de respondre).</param>
    /// <param name="library">Biblioteca (les proves que no en necessiten hi passen null).</param>
    /// <param name="listDevices">Dispositius d'àudio. Per defecte, els reals de Windows; les proves en passen una llista inventada.</param>
    /// <param name="queue">Cua d'emissió (null a les proves que no en necessiten).</param>
    /// <param name="outputs">Sortida d'emissió (igual).</param>
    /// <param name="autoDj">AutoDJ (igual).</param>
    /// <param name="rotations">Rotacions (igual).</param>
    /// <param name="nowPlaying">Exportació del títol per a BUTT (igual).</param>
    /// <param name="events">Esdeveniments del planificador (igual).</param>
    /// <param name="scheduler">Planificador: se li diu que torni a llegir els esdeveniments quan canvien (igual).</param>
    public CommandRouter(EngineState state, Action requestShutdown, Library? library = null, Func<IReadOnlyList<AudioDevice>>? listDevices = null,
        QueuePlayer? queue = null, OutputManager? outputs = null, AutoDj? autoDj = null, Rotations? rotations = null,
        NowPlaying? nowPlaying = null, Events? events = null, Scheduler? scheduler = null)
    {
        _state = state;
        _requestShutdown = requestShutdown;
        _library = library;
        _listDevices = listDevices ?? DeviceList.GetAll;
        _queue = queue;
        _outputs = outputs;
        _autoDj = autoDj;
        _rotations = rotations;
        _nowPlaying = nowPlaying;
        _events = events;
        _scheduler = scheduler;
    }

    public JsonObject Handle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
            // Els camps es llegeixen la primera vegada que es fan servir: es força ara perquè
            // un camp repetit (p. ex. dos "id") doni bad_json aquí i no un error més endavant.
            if (node is JsonObject obj) _ = obj.Count;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Messages.Error(null, "bad_json", "El missatge no és JSON vàlid (o té camps repetits).");
        }

        if (node is not JsonObject msg)
            return Messages.Error(null, "bad_json", "El missatge ha de ser un objecte JSON.");

        JsonNode? id = msg["id"];

        if (ReadInt(msg["v"]) != Messages.ProtocolVersion)
            return Messages.Error(id, "protocol_version", $"Versió de protocol no compatible (s'esperava {Messages.ProtocolVersion}).");

        string? cmd = ReadString(msg["cmd"]);
        if (string.IsNullOrWhiteSpace(cmd))
            return Messages.Error(id, "missing_cmd", "Falta el camp 'cmd'.");

        try
        {
            JsonObject? result = cmd switch
            {
                "engine.hello" => Hello(),
                "engine.ping" => Ping(),
                "engine.shutdown" => Shutdown(),
                "state.get" => StateGet(),
                "devices.list" => DevicesList(msg),
                "output.get" => new JsonObject { ["output"] = QueueJson.Output(Outputs()) },
                "output.set" => OutputSet(msg),
                "queue.get" => new JsonObject { ["queue"] = QueueJson.Queue(Queue()) },
                "queue.insert" => QueueInsert(msg),
                "queue.remove" => QueueRemove(msg),
                "player.next" => PlayerNext(msg),
                "library.list" => LibraryList(msg),
                "autodj.get" => new JsonObject { ["autodj"] = QueueJson.AutoDj(AutoDjOrError()) },
                "autodj.set" => AutoDjSet(msg),
                "rotations.list" => RotationsList(),
                "history.list" => HistoryList(msg),
                "nowplaying.get" => new JsonObject { ["nowPlaying"] = QueueJson.NowPlaying(NowPlayingOrError()) },
                "nowplaying.set" => NowPlayingSet(msg),
                "scheduler.list" => SchedulerList(),
                "scheduler.get" => SchedulerGet(msg),
                "scheduler.save" => SchedulerSave(msg),
                "scheduler.delete" => SchedulerDelete(msg),
                "scheduler.occurrences" => SchedulerOccurrences(msg),
                "scheduler.log" => SchedulerLog(msg),
                _ => null,
            };

            return result is null
                ? Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}")
                : Messages.Ok(id, result);
        }
        catch (BadParamsException ex)
        {
            return Messages.Error(id, "bad_params", ex.Message);
        }
        catch (DatabaseException ex)
        {
            return Messages.Error(id, "database_error", ex.Message);
        }
        catch (Exception ex)
        {
            // Un error en una ordre mai no ha d'aturar el motor.
            return Messages.Error(id, "internal", ex.Message);
        }
    }

    private static JsonObject Hello() => new()
    {
        ["name"] = EngineInfo.Name,
        ["version"] = EngineInfo.Version,
        ["protocol"] = Messages.ProtocolVersion,
    };

    private static JsonObject Ping() => new()
    {
        ["pong"] = true,
        ["time"] = DateTimeOffset.UtcNow.ToString("O"),
    };

    private JsonObject Shutdown()
    {
        _requestShutdown();
        return new JsonObject { ["stopping"] = true };
    }

    private JsonObject StateGet() => new()
    {
        ["engine"] = EngineStatus(_state),
        ["library"] = new JsonObject
        {
            ["tracks"] = _library?.CountTracks() ?? 0,
        },
    };

    /// <summary>Estat general del motor (també a l'esdeveniment <c>engine.status</c>).</summary>
    public static JsonObject EngineStatus(EngineState state) => new()
    {
        ["version"] = EngineInfo.Version,
        ["uptime"] = Math.Round(state.Uptime.TotalSeconds, 1),
        ["clients"] = state.Clients,
    };

    private JsonObject DevicesList(JsonObject msg)
    {
        string kind = ReadString(msg["kind"]) ?? "all";
        if (kind is not ("output" or "input" or "all"))
            throw new BadParamsException("El camp 'kind' ha de ser \"output\", \"input\" o \"all\".");
        bool includeInactive = msg["includeInactive"] is JsonValue v && v.TryGetValue(out bool b) && b;

        var list = new JsonArray();
        foreach (var device in _listDevices())
        {
            if (kind == "output" && device.Kind != DeviceKind.Output) continue;
            if (kind == "input" && device.Kind != DeviceKind.Input) continue;
            if (!includeInactive && device.Status != DeviceStatus.Active) continue;
            list.Add(DeviceJson.Device(device));
        }
        return new JsonObject { ["devices"] = list };
    }

    // --- Sortida (D-038) ---

    private OutputManager Outputs() => _outputs ?? throw new BadParamsException("El motor no té sortida d'àudio.");

    private JsonObject OutputSet(JsonObject msg)
    {
        var outputs = Outputs();
        string deviceId = RequireString(msg, "deviceId");
        var device = _listDevices().FirstOrDefault(d => d.Id == deviceId && d.Kind == DeviceKind.Output)
            ?? throw new BadParamsException("No existeix aquesta sortida d'àudio.");
        var configured = new ConfiguredOutput(device.Id, device.Name);
        _library?.SetSetting(OutputSettingKey, JsonSerializer.Serialize(new { id = configured.Id, name = configured.Name }));
        outputs.Configure(configured);
        return new JsonObject { ["output"] = QueueJson.Output(outputs) };
    }

    // --- Cua ---

    private QueuePlayer Queue() => _queue ?? throw new BadParamsException("El motor no té cua d'emissió.");

    /// <summary>
    /// Afegeix pistes a la cua: <c>items</c> és una llista de <c>{ "trackId": 12 }</c> (de la biblioteca) o
    /// <c>{ "path": "D:\…" }</c> (un fitxer que no hi és); <c>position</c> és <c>"end"</c> (per defecte), <c>"next"</c> o un índex.
    /// </summary>
    private JsonObject QueueInsert(JsonObject msg)
    {
        var queue = Queue();
        var items = new List<QueueItem>();

        // Per a les proves d'escolta (fase 1): pistes d'una carpeta de la biblioteca, barrejades
        if (ReadString(msg["folder"]) is { Length: > 0 } folder)
        {
            var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
            int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 10, 1, 500);
            var tracks = library.TracksInFolder(folder).Where(t => t.FileState == FileState.Ok).ToList();
            if (tracks.Count == 0) throw new BadParamsException("No hi ha cap pista disponible en aquesta carpeta de la biblioteca.");
            items.AddRange(tracks.OrderBy(_ => Random.Shared.Next()).Take(limit).Select(t => QueueItem.FromTrack(t, QueueOrigin.Manual)));
        }
        else if (msg["items"] is not JsonArray { Count: > 0 })
        {
            throw new BadParamsException("Falta el camp 'items' (llista de pistes) o 'folder'.");
        }

        foreach (var node in msg["items"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry) throw new BadParamsException("Cada element d'items ha de ser un objecte.");
            if (entry["trackId"] is JsonValue idValue && idValue.TryGetValue(out long trackId))
            {
                var track = _library?.GetTrack(trackId) ?? throw new BadParamsException($"No existeix la pista {trackId}.");
                items.Add(QueueItem.FromTrack(track, QueueOrigin.Manual));
            }
            else if (ReadString(entry["path"]) is { Length: > 0 } path)
            {
                items.Add(_library?.FindByPath(path) is { } known
                    ? QueueItem.FromTrack(known, QueueOrigin.Manual)
                    : new QueueItem { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path), Origin = QueueOrigin.Manual });
            }
            else throw new BadParamsException("Cada element d'items necessita 'trackId' o 'path'.");
        }

        switch (msg["position"])
        {
            case null:
            case JsonValue v when v.TryGetValue(out string? p) && p == "end":
                queue.Enqueue(items.ToArray());
                break;
            case JsonValue v when v.TryGetValue(out string? p) && p == "next":
                queue.Insert(0, items.ToArray());
                break;
            case JsonValue v when v.TryGetValue(out int index) && index >= 0:
                queue.Insert(index, items.ToArray());
                break;
            default:
                throw new BadParamsException("El camp 'position' ha de ser \"end\", \"next\" o un índex (0 = la primera que sonarà).");
        }
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    private JsonObject QueueRemove(JsonObject msg)
    {
        var queue = Queue();
        if (!Guid.TryParse(RequireString(msg, "itemId"), out var itemId) || !queue.Remove(itemId))
            throw new BadParamsException("Aquest element no és a la cua o ja ha començat a sonar.");
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    private JsonObject PlayerNext(JsonObject msg)
    {
        var queue = Queue();
        int fadeMs = ReadInt(msg["fadeMs"]) ?? 1000;
        if (fadeMs is < 0 or > 10_000) throw new BadParamsException("El camp 'fadeMs' ha de ser entre 0 i 10.000.");
        queue.Skip(TimeSpan.FromMilliseconds(fadeMs));
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    // --- Biblioteca ---

    /// <summary>Pistes d'una carpeta (i subcarpetes), com a molt <c>limit</c> (per defecte 100). De moment, per a les proves.</summary>
    private JsonObject LibraryList(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        string folder = RequireString(msg, "folder");
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 1000);
        var tracks = library.TracksInFolder(folder).Take(limit);
        return new JsonObject { ["tracks"] = new JsonArray(tracks.Select(t => (JsonNode)QueueJson.Track(t)).ToArray()) };
    }

    // --- AutoDJ i rotacions ---

    private AutoDj AutoDjOrError() => _autoDj ?? throw new BadParamsException("El motor no té AutoDJ.");

    /// <summary>
    /// Activa o desactiva l'AutoDJ (<c>enabled</c>) i canvia la rotació (<c>rotationId</c>). Els camps que no s'envien
    /// es queden com estan. Es desa a la configuració, perquè en tornar a arrencar continuï igual.
    /// </summary>
    private JsonObject AutoDjSet(JsonObject msg)
    {
        var autoDj = AutoDjOrError();
        bool enabled = autoDj.Enabled;
        if (msg["enabled"] is not null)
        {
            if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out enabled))
                throw new BadParamsException("El camp 'enabled' ha de ser true o false.");
        }
        long? rotationId = autoDj.Rotation?.Id;
        if (msg["rotationId"] is not null)
        {
            if (msg["rotationId"] is not JsonValue v || !v.TryGetValue(out long id))
                throw new BadParamsException("El camp 'rotationId' ha de ser un número.");
            rotationId = id;
        }
        if (!autoDj.Configure(enabled, rotationId))
            throw new BadParamsException($"No existeix la rotació {rotationId}.");
        // Només es desa la rotació: en arrencar, el motor sempre activa l'AutoDJ (D-053)
        _library?.SetSetting(AutoDjSettingKey, JsonSerializer.Serialize(new { rotationId = autoDj.Rotation?.Id }));
        return new JsonObject { ["autodj"] = QueueJson.AutoDj(autoDj) };
    }

    private JsonObject RotationsList()
    {
        var rotations = _rotations ?? throw new BadParamsException("El motor no té rotacions.");
        return new JsonObject { ["rotations"] = new JsonArray(rotations.List().Select(r => (JsonNode)QueueJson.Rotation(r)).ToArray()) };
    }

    // --- Historial i títol per a BUTT (fase 1, pas 9) ---

    /// <summary>
    /// Cerca a l'historial, les més recents primer. <c>from</c> i <c>to</c>: una hora ISO 8601 amb zona
    /// (<c>2026-11-10T12:00:00Z</c>) o un dia (<c>2026-11-10</c>, hora local; <c>to</c> inclou tot el dia).
    /// <c>search</c>: text de l'artista o del títol. <c>limit</c>: per defecte 100, com a molt 2.000.
    /// </summary>
    private JsonObject HistoryList(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var from = ReadTime(msg, "from", endOfDay: false);
        var to = ReadTime(msg, "to", endOfDay: true);
        if (from is not null && to is not null && from >= to)
            throw new BadParamsException("El camp 'from' ha de ser anterior a 'to'.");
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 2000);
        // Se'n demana una de més per saber si n'hi ha més que les que es retornen
        var entries = library.SearchHistory(from, to, ReadString(msg["search"]), limit + 1);
        return new JsonObject
        {
            ["entries"] = new JsonArray(entries.Take(limit).Select(e => (JsonNode)QueueJson.History(e)).ToArray()),
            ["more"] = entries.Count > limit,
        };
    }

    /// <summary>
    /// Llegeix una hora o un dia. Un dia és l'hora local de les 00:00; si <paramref name="endOfDay"/>, la del dia següent
    /// (perquè «fins al dia 10» inclogui tot el dia 10). Les dates es compten en dies de calendari (canvis d'horari).
    /// </summary>
    private static DateTimeOffset? ReadTime(JsonObject msg, string field, bool endOfDay)
    {
        if (msg[field] is null) return null;
        string text = ReadString(msg[field]) ?? throw new BadParamsException($"El camp '{field}' ha de ser un text.");
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
        {
            if (endOfDay) day = day.AddDays(1);
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
            return new DateTimeOffset(local);
        }
        if (text.Contains('T') && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var instant)
            && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > text.IndexOf('T')))
            return instant;
        throw new BadParamsException($"El camp '{field}' ha de ser un dia (2026-11-10) o una hora amb zona (2026-11-10T12:00:00Z).");
    }

    private NowPlaying NowPlayingOrError() => _nowPlaying ?? throw new BadParamsException("El motor no exporta el títol.");

    /// <summary>
    /// Canvia l'exportació del títol: <c>enabled</c>, <c>path</c> (ruta completa; <c>null</c> o <c>""</c> = la per defecte),
    /// <c>template</c> (amb <c>$artist$</c> i <c>$title$</c>) i <c>types</c> (tipus de pista que canvien el títol).
    /// Els camps que no s'envien es queden com estan. Es desa a la configuració.
    /// </summary>
    private JsonObject NowPlayingSet(JsonObject msg)
    {
        var nowPlaying = NowPlayingOrError();
        var settings = nowPlaying.Settings;

        if (msg["enabled"] is not null)
        {
            if (msg["enabled"] is not JsonValue v || !v.TryGetValue(out bool enabled))
                throw new BadParamsException("El camp 'enabled' ha de ser true o false.");
            settings = settings with { Enabled = enabled };
        }
        if (msg.ContainsKey("path"))
        {
            string? path = msg["path"] is null ? null : ReadString(msg["path"]) ?? throw new BadParamsException("El camp 'path' ha de ser un text.");
            if (string.IsNullOrWhiteSpace(path)) path = null;
            else if (!System.IO.Path.IsPathFullyQualified(path) || path.EndsWith('\\') || path.EndsWith('/'))
                throw new BadParamsException("El camp 'path' ha de ser la ruta completa d'un fitxer (p. ex. C:\\RadioDJ\\butt\\NowPlaying.txt).");
            settings = settings with { Path = path };
        }
        if (msg["template"] is not null)
        {
            string template = ReadString(msg["template"]) ?? "";
            if (!template.Contains("$artist$", StringComparison.OrdinalIgnoreCase) && !template.Contains("$title$", StringComparison.OrdinalIgnoreCase))
                throw new BadParamsException("La plantilla ha de tenir $artist$ o $title$.");
            settings = settings with { Template = template };
        }
        if (msg["types"] is not null)
        {
            if (msg["types"] is not JsonArray list) throw new BadParamsException("El camp 'types' ha de ser una llista.");
            try
            {
                settings = settings with { Types = list.Select(t => NowPlayingSettings.ParseType(ReadString(t))).Distinct().ToArray() };
            }
            catch (FormatException ex)
            {
                throw new BadParamsException(ex.Message);
            }
        }

        nowPlaying.Configure(settings);
        _library?.SetSetting(NowPlayingSettingKey, settings.ToJson());
        return new JsonObject { ["nowPlaying"] = QueueJson.NowPlaying(nowPlaying) };
    }

    // --- Planificador (fase 2, pas 3) ---

    private Events EventsOrError() => _events ?? throw new BadParamsException("El motor no té planificador.");

    private TimeZoneInfo Zone => _scheduler?.Zone ?? TimeZoneInfo.Local;

    private JsonObject SchedulerList() => new()
    {
        ["events"] = new JsonArray(EventsOrError().List().Select(e => (JsonNode)EventJson.Event(e)).ToArray()),
    };

    private JsonObject SchedulerGet(JsonObject msg)
    {
        long id = RequireId(msg);
        var ev = EventsOrError().Get(id) ?? throw new BadParamsException($"No existeix l'esdeveniment {id}.");
        return new JsonObject { ["event"] = EventJson.Event(ev) };
    }

    /// <summary>
    /// Desa un esdeveniment (<c>event</c>, docs/protocol.md 4.7): sense <c>id</c> (o 0), un de nou; amb <c>id</c>, en substitueix
    /// un. Retorna l'esdeveniment desat. El planificador el té en compte de seguida.
    /// </summary>
    private JsonObject SchedulerSave(JsonObject msg)
    {
        var events = EventsOrError();
        if (msg["event"] is not JsonObject json) throw new BadParamsException("Falta el camp 'event' (un objecte).");
        long id;
        try
        {
            id = events.Save(EventJson.FromJson(json));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new BadParamsException(ex.Message);
        }
        _scheduler?.Reload();
        return new JsonObject { ["event"] = EventJson.Event(events.Get(id)!) };
    }

    private JsonObject SchedulerDelete(JsonObject msg)
    {
        long id = RequireId(msg);
        if (!EventsOrError().Delete(id)) throw new BadParamsException($"No existeix l'esdeveniment {id}.");
        _scheduler?.Reload();
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>
    /// Quan sonaran els esdeveniments actius entre <c>from</c> i <c>to</c> (per defecte, d'ara a 24 hores). Amb <c>eventId</c>,
    /// només aquest esdeveniment, encara que estigui desactivat. <c>limit</c>: per defecte 200, com a molt 2.000.
    /// </summary>
    private JsonObject SchedulerOccurrences(JsonObject msg)
    {
        var events = EventsOrError();
        var from = ReadTime(msg, "from", endOfDay: false) ?? DateTimeOffset.Now;
        var to = ReadTime(msg, "to", endOfDay: true) ?? from.AddDays(1);
        if (from >= to) throw new BadParamsException("El camp 'from' ha de ser anterior a 'to'.");
        if (to - from > TimeSpan.FromDays(400)) throw new BadParamsException("Com a molt, un any i un mes.");
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 200, 1, 2000);

        IEnumerable<ScheduledEvent> selected;
        if (msg["eventId"] is not null)
        {
            long id = RequireId(msg);
            selected = [events.Get(id) ?? throw new BadParamsException($"No existeix l'esdeveniment {id}.")];
        }
        else
        {
            selected = events.List().Where(e => e.Enabled && !e.IsTemplate);
        }

        var list = selected.SelectMany(e => Recurrences.Between(e, from, to, Zone))
            .OrderBy(o => o.At).ThenByDescending(o => o.Event.Priority)
            .Take(limit + 1).ToList();
        return new JsonObject
        {
            ["occurrences"] = new JsonArray(list.Take(limit).Select(o => (JsonNode)EventJson.Occurrence(o)).ToArray()),
            ["more"] = list.Count > limit,
        };
    }

    /// <summary>Registre d'execució entre <c>from</c> i <c>to</c> (per defecte, avui), per hora prevista. Amb <c>eventId</c>, només d'aquest esdeveniment.</summary>
    private JsonObject SchedulerLog(JsonObject msg)
    {
        var events = EventsOrError();
        var today = new DateTimeOffset(DateTime.Today);
        var from = ReadTime(msg, "from", endOfDay: false) ?? today;
        var to = ReadTime(msg, "to", endOfDay: true) ?? from.AddDays(1);
        if (from >= to) throw new BadParamsException("El camp 'from' ha de ser anterior a 'to'.");
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 500, 1, 5000);
        long? id = msg["eventId"] is null ? null : RequireId(msg);
        var records = events.Executions(from, to, id, limit + 1);
        return new JsonObject
        {
            ["entries"] = new JsonArray(records.Take(limit).Select(r => (JsonNode)EventJson.Execution(r)).ToArray()),
            ["more"] = records.Count > limit,
        };
    }

    private static long RequireId(JsonObject msg) =>
        msg["eventId"] is JsonValue v && v.TryGetValue(out long id) ? id : throw new BadParamsException("Falta el camp 'eventId' (un nombre).");

    private static string RequireString(JsonObject msg, string field)
    {
        string? value = ReadString(msg[field]);
        if (string.IsNullOrWhiteSpace(value))
            throw new BadParamsException($"Falta el camp '{field}'.");
        return value;
    }

    /// <summary>Error en els paràmetres d'una ordre: es respon amb el codi <c>bad_params</c>.</summary>
    private sealed class BadParamsException(string message) : Exception(message);

    private static int? ReadInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out int i) ? i : null;

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;
}
