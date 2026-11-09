using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Protocol;

/// <summary>
/// Rep el text d'una ordre, la valida i en retorna la resposta (docs/protocol.md).
/// Cada ordre nova s'afegeix aquí, a docs/protocol.md i a les proves, en el mateix canvi.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>Clau de la configuració on es desa la sortida (JSON: <c>{ "id": …, "name": … }</c>).</summary>
    public const string OutputSettingKey = "sortida";

    private readonly EngineState _state;
    private readonly Action _requestShutdown;
    private readonly Library? _library;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly QueuePlayer? _queue;
    private readonly OutputManager? _outputs;

    /// <param name="state">Estat del motor.</param>
    /// <param name="requestShutdown">Atura el motor (s'executa després de respondre).</param>
    /// <param name="library">Biblioteca (les proves que no en necessiten hi passen null).</param>
    /// <param name="listDevices">Dispositius d'àudio. Per defecte, els reals de Windows; les proves en passen una llista inventada.</param>
    /// <param name="queue">Cua d'emissió (null a les proves que no en necessiten).</param>
    /// <param name="outputs">Sortida d'emissió (igual).</param>
    public CommandRouter(EngineState state, Action requestShutdown, Library? library = null, Func<IReadOnlyList<AudioDevice>>? listDevices = null,
        QueuePlayer? queue = null, OutputManager? outputs = null)
    {
        _state = state;
        _requestShutdown = requestShutdown;
        _library = library;
        _listDevices = listDevices ?? DeviceList.GetAll;
        _queue = queue;
        _outputs = outputs;
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
