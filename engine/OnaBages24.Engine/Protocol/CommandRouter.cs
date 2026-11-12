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
    private readonly AuxPlayers? _aux;
    private readonly SweeperOverlay? _sweepers;
    private readonly OutputProcessor? _processor;

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
    /// <param name="aux">Reproductors auxiliars (igual).</param>
    public CommandRouter(EngineState state, Action requestShutdown, Library? library = null, Func<IReadOnlyList<AudioDevice>>? listDevices = null,
        QueuePlayer? queue = null, OutputManager? outputs = null, AutoDj? autoDj = null, Rotations? rotations = null,
        NowPlaying? nowPlaying = null, Events? events = null, Scheduler? scheduler = null, AuxPlayers? aux = null,
        SweeperOverlay? sweepers = null, OutputProcessor? processor = null)
    {
        _sweepers = sweepers;
        _processor = processor;
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
        _aux = aux;
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
                "queue.move" => QueueMove(msg),
                "queue.regenerate" => QueueRegenerate(),
                "player.playItem" => PlayerPlayItem(msg),
                "player.next" => PlayerNext(msg),
                "aux.get" => new JsonObject { ["aux"] = QueueJson.Aux(Aux()), ["loaded"] = QueueJson.AuxLoaded(Aux()) },
                "aux.load" => AuxLoad(msg),
                "aux.unload" => AuxUnload(msg),
                "notes.get" => NotesGet(),
                "ducking.get" => DuckingGet(),
                "ducking.set" => DuckingSet(msg),
                "processor.get" => new JsonObject { ["processor"] = QueueJson.Processor(ProcessorOrError()) },
                "processor.set" => ProcessorSet(msg),
                "notes.set" => NotesSet(msg),
                "ui.get" => UiGet(),
                "ui.set" => UiSet(msg),
                "aux.play" => AuxPlay(msg),
                "aux.stop" => AuxStop(msg),
                "library.list" => LibraryList(msg),
                "library.search" => LibrarySearch(msg),
                "library.filters" => LibraryFilters(),
                "library.get" => LibraryGet(msg),
                "library.update" => LibraryUpdate(msg),
                "library.relink" => LibraryRelink(msg),
                "library.delete" => LibraryDelete(msg),
                "category.save" => CategorySave(msg),
                "subcategory.save" => SubcategorySave(msg),
                "autodj.get" => new JsonObject { ["autodj"] = QueueJson.AutoDj(AutoDjOrError()) },
                "autodj.set" => AutoDjSet(msg),
                "sweepers.set" => SweepersSet(msg),
                "rotations.list" => RotationsList(),
                "history.list" => HistoryList(msg),
                "nowplaying.get" => new JsonObject { ["nowPlaying"] = QueueJson.NowPlaying(NowPlayingOrError()) },
                "nowplaying.set" => NowPlayingSet(msg),
                "scheduler.list" => SchedulerList(),
                "scheduler.get" => SchedulerGet(msg),
                "scheduler.save" => SchedulerSave(msg),
                "scheduler.preview" => SchedulerPreview(msg),
                "scheduler.delete" => SchedulerDelete(msg),
                "scheduler.occurrences" => SchedulerOccurrences(msg),
                "scheduler.log" => SchedulerLog(msg),
                "scheduler.status" => new JsonObject { ["paused"] = EventJson.Paused(SchedulerOrError()) },
                "scheduler.pause" => SchedulerPause(msg),
                "scheduler.resume" => SchedulerResume(),
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
        if (!Guid.TryParse(RequireString(msg, "itemId"), out var itemId) || queue.Take(itemId) is not { } taken)
            throw new BadParamsException("Aquest element no és a la cua o ja ha començat a sonar.");
        _scheduler?.RecordNotPlayed([taken], "treta a mà"); // si venia d'un esdeveniment: NO_EMES
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    /// <summary>«Regenera la cua» (D-034): treu les pistes de l'AutoDJ i les torna a triar amb la rotació actual.</summary>
    private JsonObject QueueRegenerate()
    {
        int removed = AutoDjOrError().Regenerate();
        return new JsonObject { ["removed"] = removed, ["queue"] = QueueJson.Queue(Queue()) };
    }

    /// <summary>Mou una pista de la cua una posició amunt o avall (fase 3, pas 2a): <c>itemId</c> i <c>delta</c> (−1 o +1).</summary>
    private JsonObject QueueMove(JsonObject msg)
    {
        var queue = Queue();
        if (!Guid.TryParse(RequireString(msg, "itemId"), out var itemId)) throw new BadParamsException("El camp 'itemId' no és vàlid.");
        int delta = ReadInt(msg["delta"]) ?? 0;
        if (delta is not (-1 or 1)) throw new BadParamsException("El camp 'delta' ha de ser −1 (amunt) o 1 (avall).");
        if (queue.Move(itemId, delta) is { } problem) throw new BadParamsException(problem);
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    /// <summary>
    /// «Reprodueix ara» (fase 3, pas 2a; D-073): <c>itemId</c> i <c>fadeMs</c> (per defecte 1.000). La que sona baixa amb un
    /// fos, la triada comença de seguida i les d'entremig es treuen (NO_EMES si venien d'un esdeveniment).
    /// </summary>
    private JsonObject PlayerPlayItem(JsonObject msg)
    {
        var queue = Queue();
        if (!Guid.TryParse(RequireString(msg, "itemId"), out var itemId)) throw new BadParamsException("El camp 'itemId' no és vàlid.");
        int fadeMs = ReadInt(msg["fadeMs"]) ?? 1000;
        if (fadeMs is < 0 or > 10_000) throw new BadParamsException("El camp 'fadeMs' ha de ser entre 0 i 10.000.");
        var skipped = queue.PlayNow(itemId, TimeSpan.FromMilliseconds(fadeMs))
            ?? throw new BadParamsException("Ara no es pot: la pista ja no és a la cua o hi ha un tall programat d'aquí a uns segons.");
        _scheduler?.RecordNotPlayed(skipped, "saltades amb «Reprodueix ara»");
        return new JsonObject
        {
            ["skipped"] = new JsonArray(skipped.Select(i => (JsonNode)QueueJson.Item(i)).ToArray()),
            ["queue"] = QueueJson.Queue(queue),
        };
    }

    private JsonObject PlayerNext(JsonObject msg)
    {
        var queue = Queue();
        int fadeMs = ReadInt(msg["fadeMs"]) ?? 1000;
        if (fadeMs is < 0 or > 10_000) throw new BadParamsException("El camp 'fadeMs' ha de ser entre 0 i 10.000.");
        queue.Skip(TimeSpan.FromMilliseconds(fadeMs));
        return new JsonObject { ["queue"] = QueueJson.Queue(queue) };
    }

    /// <summary>
    /// Cerca a tota la biblioteca (fase 2, pas 8b; fase 3, pas 3a): <c>text</c> (paraules que han de sortir a l'artista, al
    /// títol, a l'àlbum o a la ruta, sense accents ni majúscules), <c>categoryId</c>, <c>subcategoryId</c>, <c>tagId</c>,
    /// <c>offset</c> (per anar carregant) i <c>limit</c> (per defecte 50, com a molt 200).
    /// </summary>
    private JsonObject LibrarySearch(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 50, 1, 200);
        long? Id(string field) => msg[field] is JsonValue v && v.TryGetValue(out long id) ? id : null;
        var tracks = library.Search(new TrackQuery
        {
            Text = ReadString(msg["text"]) ?? "",
            CategoryId = Id("categoryId"),
            SubcategoryId = Id("subcategoryId"),
            TagId = Id("tagId"),
            State = ReadString(msg["state"]) switch
            {
                null or "all" => TrackStateFilter.All,
                "missing" => TrackStateFilter.Missing,
                "retired" => TrackStateFilter.Retired,
                "disabled" => TrackStateFilter.Disabled,
                "unclassified" => TrackStateFilter.Unclassified,
                var other => throw new BadParamsException($"El camp 'state' no és vàlid: «{other}» (all, missing, retired, disabled o unclassified)."),
            },
            Offset = Math.Max(0, ReadInt(msg["offset"]) ?? 0),
            Limit = limit + 1,
        });
        return new JsonObject
        {
            ["tracks"] = new JsonArray(tracks.Take(limit).Select(t => (JsonNode)QueueJson.Track(t)).ToArray()),
            ["more"] = tracks.Count > limit,
        };
    }

    /// <summary>Les opcions dels filtres de la cerca (fase 3, pas 3a): categories amb subcategories i etiquetes, amb comptes.</summary>
    private JsonObject LibraryFilters()
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var (categories, tags) = library.SearchFilters();
        JsonObject Option(FilterOption o) => new() { ["id"] = o.Id, ["name"] = o.Name, ["count"] = o.Count };
        return new JsonObject
        {
            ["categories"] = new JsonArray(categories.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["name"] = c.Name, ["count"] = c.Count,
                ["subcategories"] = new JsonArray(c.Subcategories.Select(s => (JsonNode)Option(s)).ToArray()),
            }).ToArray()),
            ["tags"] = new JsonArray(tags.Select(t => (JsonNode)Option(t)).ToArray()),
            ["states"] = library.StateCounts() is var s
                ? new JsonObject { ["all"] = s.All, ["missing"] = s.Missing, ["retired"] = s.Retired, ["disabled"] = s.Disabled, ["unclassified"] = s.Unclassified }
                : null,
        };
    }

    // --- Gestor de pistes (fase 3, pas 7a) ---

    /// <summary>
    /// Quan canvia la biblioteca des d'una ordre: l'id de la pista (editada, canviada de fitxer o esborrada), o null si han
    /// canviat les categories. Les interfícies tornen a demanar el que mostren (<c>library.changed</c>).
    /// </summary>
    public event Action<long?>? LibraryChanged;

    private Library LibraryOrError() => _library ?? throw new BadParamsException("El motor no té biblioteca.");

    private static long RequiredId(JsonObject msg, string field = "id") =>
        msg[field] is JsonValue v && v.TryGetValue(out long id) ? id : throw new BadParamsException($"Falta '{field}' (un número).");

    private Track TrackOrError(Library library, long id) =>
        library.GetTrack(id) ?? throw new BadParamsException($"No existeix la pista {id}.");

    /// <summary>La fitxa sencera d'una pista, amb les etiquetes, la subcategoria i els esdeveniments que la fan servir.</summary>
    private JsonObject TrackDetail(Library library, Track track) => new()
    {
        ["track"] = QueueJson.TrackDetail(track, library.GetTags(track.Id),
            track.SubcategoryId is { } sub ? library.GetSubcategory(sub) : null, library.EventsUsingTrack(track.Id)),
    };

    private JsonObject LibraryGet(JsonObject msg)
    {
        var library = LibraryOrError();
        return TrackDetail(library, TrackOrError(library, RequiredId(msg, "trackId")));
    }

    /// <summary>
    /// Canvia els camps de la fitxa que s'envien (els altres es queden com estan): <c>artist</c>, <c>title</c>, <c>album</c>,
    /// <c>year</c>, <c>type</c>, <c>subcategoryId</c> (null = cap), <c>tags</c> (llista de noms; substitueix les d'ara),
    /// <c>variableDuration</c>, <c>validFrom</c> i <c>validUntil</c> ('AAAA-MM-DD' o null), <c>enabled</c> i <c>notes</c>.
    /// </summary>
    private JsonObject LibraryUpdate(JsonObject msg)
    {
        var library = LibraryOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));

        string Text(string field, string current, int max = 500)
        {
            if (msg[field] is null) return current;
            string value = (ReadString(msg[field]) ?? throw new BadParamsException($"El camp '{field}' ha de ser un text.")).Trim();
            return value.Length <= max ? value : throw new BadParamsException($"El camp '{field}' és massa llarg (com a molt {max} caràcters).");
        }
        bool Bool(string field, bool current) =>
            msg[field] is null ? current
            : msg[field] is JsonValue v && v.TryGetValue(out bool b) ? b : throw new BadParamsException($"El camp '{field}' ha de ser true o false.");
        DateOnly? Date(string field, DateOnly? current)
        {
            if (!msg.ContainsKey(field)) return current;
            if (msg[field] is null) return null;
            return DateOnly.TryParseExact(ReadString(msg[field]), "yyyy-MM-dd", out var d)
                ? d : throw new BadParamsException($"El camp '{field}' ha de ser una data 'AAAA-MM-DD' o null.");
        }

        long? subcategoryId = track.SubcategoryId;
        if (msg.ContainsKey("subcategoryId"))
        {
            if (msg["subcategoryId"] is null) subcategoryId = null;
            else
            {
                long sub = RequiredId(msg, "subcategoryId");
                if (library.GetSubcategory(sub) is null) throw new BadParamsException($"No existeix la subcategoria {sub}.");
                subcategoryId = sub;
            }
        }
        var type = track.Type;
        if (msg["type"] is not null)
        {
            try { type = NowPlayingSettings.ParseType(ReadString(msg["type"])); }
            catch (FormatException ex) { throw new BadParamsException(ex.Message); }
        }
        List<string>? tags = null;
        if (msg["tags"] is not null)
        {
            if (msg["tags"] is not JsonArray list || list.Any(t => ReadString(t) is null))
                throw new BadParamsException("El camp 'tags' ha de ser una llista de textos.");
            tags = list.Select(t => ReadString(t)!.Trim()).Where(t => t.Length > 0).ToList();
            if (tags.Any(t => t.Length > 100)) throw new BadParamsException("Una etiqueta pot tenir com a molt 100 caràcters.");
        }

        string? notes = track.Notes;
        if (msg.ContainsKey("notes"))
        {
            notes = msg["notes"] is null ? null : Text("notes", "", 5000);
            if (notes is { Length: 0 }) notes = null;
        }

        var updated = track with
        {
            Artist = Text("artist", track.Artist),
            Title = Text("title", track.Title),
            Album = Text("album", track.Album),
            Year = Text("year", track.Year, 20),
            Type = type,
            SubcategoryId = subcategoryId,
            VariableDuration = Bool("variableDuration", track.VariableDuration),
            ValidFrom = Date("validFrom", track.ValidFrom),
            ValidUntil = Date("validUntil", track.ValidUntil),
            Enabled = Bool("enabled", track.Enabled),
            Notes = notes,
        };
        if (updated.ValidFrom is { } from && updated.ValidUntil is { } until && until < from)
            throw new BadParamsException("La data de fi no pot ser anterior a la d'inici.");

        library.UpdateTrack(updated);
        if (tags is not null) library.SetTags(track.Id, tags);
        LibraryChanged?.Invoke(track.Id);
        return TrackDetail(library, TrackOrError(library, track.Id));
    }

    /// <summary>
    /// Torna a apuntar una pista a un altre fitxer (<c>path</c>, ruta completa d'un fitxer d'àudio que existeixi): per a les
    /// pistes amb el fitxer que falta o quan s'ha mogut. Es conserven la fitxa i l'historial; es torna a llegir la durada i,
    /// si els punts no són manuals, l'analitzador els torna a calcular en segon pla.
    /// </summary>
    private JsonObject LibraryRelink(JsonObject msg)
    {
        var library = LibraryOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        string path =(ReadString(msg["path"]) ?? "").Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) throw new BadParamsException("Falta 'path' (la ruta completa del fitxer).");
        if (!LibraryTools.FolderImporter.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new BadParamsException($"No és un fitxer d'àudio que es pugui fer servir ({string.Join(", ", LibraryTools.FolderImporter.AudioExtensions)}).");
        if (!File.Exists(path)) throw new BadParamsException($"No es troba el fitxer: {path}");

        TimeSpan duration;
        try { duration = TrackInfo.ReadMetadata(path).Duration; }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException($"No es pot llegir el fitxer: {ex.Message}");
        }
        var info = new FileInfo(path);
        library.UpdateTrack(track with
        {
            Path = path,
            FileState = FileState.Ok,
            DurationMs = (long)duration.TotalMilliseconds,
            FileSize = info.Length,
            FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            // Els punts d'ara es fan servir fins que l'analitzador els torni a calcular (els manuals no es toquen)
            CueSource = track.CueSource == CueSource.Manual ? CueSource.Manual : null,
        });
        LibraryChanged?.Invoke(track.Id);
        return TrackDetail(library, TrackOrError(library, track.Id));
    }

    /// <summary>Esborra una pista de la biblioteca (el fitxer no es toca). No es pot si la fa servir algun esdeveniment.</summary>
    private JsonObject LibraryDelete(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "trackId");
        TrackOrError(library, id);
        library.DeleteTrack(id);
        LibraryChanged?.Invoke(id);
        return new JsonObject { ["deleted"] = id };
    }

    private static string RequiredName(JsonObject msg)
    {
        string name = (ReadString(msg["name"]) ?? "").Trim();
        if (name.Length == 0) throw new BadParamsException("Falta 'name' (el nom).");
        if (name.Length > 100) throw new BadParamsException("El nom pot tenir com a molt 100 caràcters.");
        return name;
    }

    /// <summary>Crea una categoria (sense <c>categoryId</c>) o en canvia el nom (amb <c>categoryId</c>). <c>name</c>.</summary>
    private JsonObject CategorySave(JsonObject msg)
    {
        var library = LibraryOrError();
        string name = RequiredName(msg);
        long id;
        if (msg["categoryId"] is null) id = library.CreateCategory(name);
        else library.RenameCategory(id = RequiredId(msg, "categoryId"), name);
        LibraryChanged?.Invoke(null);
        return new JsonObject { ["id"] = id, ["name"] = name };
    }

    /// <summary>
    /// Crea una subcategoria (sense <c>subcategoryId</c>, amb <c>categoryId</c>) o en canvia el nom (amb <c>subcategoryId</c>). <c>name</c>.
    /// </summary>
    private JsonObject SubcategorySave(JsonObject msg)
    {
        var library = LibraryOrError();
        string name = RequiredName(msg);
        long id;
        if (msg["subcategoryId"] is null) id = library.CreateSubcategory(RequiredId(msg, "categoryId"), name);
        else library.RenameSubcategory(id = RequiredId(msg, "subcategoryId"), name);
        LibraryChanged?.Invoke(null);
        var sub = library.GetSubcategory(id)!;
        return new JsonObject { ["id"] = sub.Id, ["name"] = sub.Name, ["categoryId"] = sub.CategoryId, ["categoryName"] = sub.CategoryName };
    }

    // --- Reproductors auxiliars (fase 2, pas 5) ---

    private AuxPlayers Aux() => _aux ?? throw new BadParamsException("El motor no té reproductors auxiliars.");

    /// <summary>Número de reproductor (<c>player</c>, per defecte 1).</summary>
    private static int ReadPlayer(JsonObject msg)
    {
        int player = msg["player"] is null ? 1 : ReadInt(msg["player"]) ?? 0;
        if (!AuxPlayers.IsValidPlayer(player))
            throw new BadParamsException($"El camp 'player' ha de ser un número de l'1 al {AuxPlayers.Count}.");
        return player;
    }

    /// <summary>
    /// Fa sonar un fitxer per un reproductor auxiliar, per sobre del que sona: <c>trackId</c> (de la biblioteca) o
    /// <c>path</c>; <c>player</c> (1–3, per defecte 1); <c>duckingDb</c> (atenuació de la música, de −60 a 0; per defecte, cap).
    /// </summary>
    private JsonObject AuxPlay(JsonObject msg)
    {
        var aux = Aux();
        int player = ReadPlayer(msg);
        var item = ReadAuxItem(msg);

        // Si no en diu cap, la de la configuració (D-078); 0 = sense atenuació
        double? ducking = aux.DefaultDuckingDb;
        if (msg["duckingDb"] is not null)
        {
            if (msg["duckingDb"] is not JsonValue v || !v.TryGetValue(out double db) || db is < -60 or > 0)
                throw new BadParamsException("El camp 'duckingDb' ha de ser un nombre entre −60 i 0 (dB).");
            ducking = db;
        }

        // Sense pista ni fitxer: la carregada al reproductor (fase 3, pas 4a)
        // Cada vegada és un element nou (amb un id nou), perquè els avisos d'inici i final no es confonguin
        item ??= (aux.Loaded(player) ?? throw new BadParamsException($"El reproductor {player} no té cap pista carregada."))
            with { Id = Guid.NewGuid() };
        if (!aux.Play(player, item, ducking))
            throw new BadParamsException("No queden veus lliures al mesclador: no es pot fer sonar ara.");
        return new JsonObject { ["player"] = player, ["item"] = QueueJson.Item(item) };
    }

    /// <summary>La pista d'una ordre dels auxiliars: <c>trackId</c> (de la biblioteca) o <c>path</c>; null si no n'hi ha cap.</summary>
    private QueueItem? ReadAuxItem(JsonObject msg)
    {
        if (msg["trackId"] is JsonValue idValue && idValue.TryGetValue(out long trackId))
        {
            var track = _library?.GetTrack(trackId) ?? throw new BadParamsException($"No existeix la pista {trackId}.");
            return QueueItem.FromTrack(track, QueueOrigin.Manual);
        }
        if (ReadString(msg["path"]) is { Length: > 0 } path)
        {
            return _library?.FindByPath(path) is { } known
                ? QueueItem.FromTrack(known, QueueOrigin.Manual)
                : new QueueItem { Path = path, Title = System.IO.Path.GetFileNameWithoutExtension(path), Origin = QueueOrigin.Manual };
        }
        return null;
    }

    /// <summary>
    /// Carrega una pista a un reproductor auxiliar sense fer-la sonar (fase 3, pas 4a): <c>player</c> i <c>trackId</c> o
    /// <c>path</c>. Si el fitxer falta, <c>bad_params</c> (no sonaria).
    /// </summary>
    private JsonObject AuxLoad(JsonObject msg)
    {
        var aux = Aux();
        int player = ReadPlayer(msg);
        var item = ReadAuxItem(msg) ?? throw new BadParamsException("Falta 'trackId' o 'path'.");
        if (!System.IO.File.Exists(item.Path))
            throw new BadParamsException($"El fitxer no hi és: {item.Path}");
        aux.Load(player, item);
        return new JsonObject { ["loaded"] = QueueJson.AuxLoaded(aux) };
    }

    /// <summary>Treu la pista carregada d'un reproductor auxiliar (el que hi soni continua sonant).</summary>
    private JsonObject AuxUnload(JsonObject msg)
    {
        var aux = Aux();
        aux.Load(ReadPlayer(msg), null);
        return new JsonObject { ["loaded"] = QueueJson.AuxLoaded(aux) };
    }

    // --- Atenuació per defecte (D-078) ---

    /// <summary>Clau de la configuració: <c>{ "auxiliars": -10, "sweepers": -8 }</c>.</summary>
    public const string DuckingSettingKey = "atenuacio";

    /// <summary>Atenuació per defecte dels sweepers del trepitjador (fase 3, pas 5; provisional, es decidirà escoltant-ho).</summary>
    public const double StandardSweepersDuckingDb = SweeperOverlay.StandardDuckingDb;

    /// <summary>Les atenuacions per defecte desades (o les estàndard, si no n'hi ha o no es poden llegir).</summary>
    public static (double Aux, double Sweepers) ReadDucking(Library? library)
    {
        try
        {
            if (library?.GetSetting(DuckingSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
                return (saved["auxiliars"]?.GetValue<double>() ?? AuxPlayers.StandardDuckingDb,
                        saved["sweepers"]?.GetValue<double>() ?? StandardSweepersDuckingDb);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return (AuxPlayers.StandardDuckingDb, StandardSweepersDuckingDb);
    }

    private JsonObject DuckingGet()
    {
        var (auxDb, sweepersDb) = ReadDucking(_library);
        return new JsonObject { ["auxiliary"] = auxDb, ["sweepers"] = sweepersDb };
    }

    /// <summary><c>auxiliary</c> i <c>sweepers</c> (de −60 a 0 dB; 0 = sense atenuació), tots dos opcionals.</summary>
    private JsonObject DuckingSet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var (auxDb, sweepersDb) = ReadDucking(library);
        static double Read(JsonNode? node, string name, double current)
        {
            if (node is null) return current;
            if (node is not JsonValue v || !v.TryGetValue(out double db) || db is < -60 or > 0)
                throw new BadParamsException($"El camp '{name}' ha de ser un nombre entre −60 i 0 (dB).");
            return db;
        }
        auxDb = Read(msg["auxiliary"], "auxiliary", auxDb);
        sweepersDb = Read(msg["sweepers"], "sweepers", sweepersDb);
        library.SetSetting(DuckingSettingKey, JsonSerializer.Serialize(new { auxiliars = auxDb, sweepers = sweepersDb }));
        if (_aux is not null) _aux.DefaultDuckingDb = auxDb;
        if (_sweepers is not null) _sweepers.DuckingDb = sweepersDb;
        return new JsonObject { ["auxiliary"] = auxDb, ["sweepers"] = sweepersDb };
    }

    // --- Processador de sortida (fase 3, pas 6; D-086) ---

    private OutputProcessor ProcessorOrError() => _processor ?? throw new BadParamsException("El motor no té processador de sortida.");

    /// <summary>
    /// <c>preset</c> (un dels de <c>presets</c>) i <c>volumeDb</c> (dins del marge), tots dos opcionals. Es desa i s'avisa
    /// amb <c>processor.changed</c> (ho fa qui escolta <see cref="OutputProcessor.Changed"/>).
    /// </summary>
    private JsonObject ProcessorSet(JsonObject msg)
    {
        var processor = ProcessorOrError();
        string preset = processor.Preset.Id;
        double volumeDb = processor.VolumeDb;
        if (msg["preset"] is not null)
        {
            preset = ReadString(msg["preset"]) ?? "";
            if (OutputProcessor.FindPreset(preset) is null)
                throw new BadParamsException($"El camp 'preset' ha de ser un d'aquests: {string.Join(", ", OutputProcessor.Presets.Select(p => p.Id))}.");
        }
        if (msg["volumeDb"] is not null)
        {
            if (msg["volumeDb"] is not JsonValue v || !v.TryGetValue(out volumeDb) || !double.IsFinite(volumeDb)
                || volumeDb < OutputProcessor.MinVolumeDb || volumeDb > OutputProcessor.MaxVolumeDb)
                throw new BadParamsException($"El camp 'volumeDb' ha de ser un nombre entre {OutputProcessor.MinVolumeDb} i {OutputProcessor.MaxVolumeDb} (dB).");
            volumeDb = Math.Round(volumeDb, 1);
        }
        processor.Configure(preset, volumeDb);
        return new JsonObject { ["processor"] = QueueJson.Processor(processor) };
    }

    // --- Notes (fase 3, pas 4a) ---

    /// <summary>Clau de la configuració on es desa el bloc de notes compartit.</summary>
    public const string NotesSettingKey = "notes";

    /// <summary>Llargada màxima de les notes (és un bloc de notes, no un document).</summary>
    public const int NotesMaxLength = 20_000;

    /// <summary>Quan es desen unes notes noves: el text (per avisar les altres interfícies).</summary>
    public event Action<string>? NotesChanged;

    private JsonObject NotesGet() => new() { ["text"] = (_library ?? throw new BadParamsException("El motor no té biblioteca.")).GetSetting(NotesSettingKey) ?? "" };

    private JsonObject NotesSet(JsonObject msg)
    {
        if (msg["text"] is not JsonValue v || !v.TryGetValue(out string? text))
            throw new BadParamsException("Falta 'text' (pot ser buit).");
        if (text.Length > NotesMaxLength)
            throw new BadParamsException($"Les notes poden tenir com a molt {NotesMaxLength:N0} caràcters.");
        (_library ?? throw new BadParamsException("El motor no té biblioteca.")).SetSetting(NotesSettingKey, text);
        NotesChanged?.Invoke(text);
        return new JsonObject { ["text"] = text };
    }

    // --- Preferències de la interfície (fase 3): zoom i requadres plegats ---

    /// <summary>Clau de la configuració: <c>{ "zoom": 1, "collapsed": { "aux": true } }</c>.</summary>
    public const string UiSettingKey = "interficie";

    /// <summary>Les preferències desades, o les per defecte (zoom 1, cap requadre plegat).</summary>
    private (double Zoom, JsonObject Collapsed) ReadUi()
    {
        try
        {
            if (_library?.GetSetting(UiSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
                return (saved["zoom"]?.GetValue<double>() ?? 1, saved["collapsed"] as JsonObject ?? []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return (1, []);
    }

    private JsonObject UiGet()
    {
        var (zoom, collapsed) = ReadUi();
        return new JsonObject { ["zoom"] = zoom, ["collapsed"] = collapsed.DeepClone() };
    }

    /// <summary>
    /// <c>zoom</c> (de 0,5 a 1,5) i <c>collapsed</c> (nom del requadre → plegat o no; els que no s'envien es queden
    /// com estan), tots dos opcionals.
    /// </summary>
    private JsonObject UiSet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var (zoom, collapsed) = ReadUi();
        if (msg["zoom"] is { } zoomNode)
        {
            if (zoomNode is not JsonValue v || !v.TryGetValue(out double z) || z is < 0.5 or > 1.5)
                throw new BadParamsException("El camp 'zoom' ha de ser un nombre entre 0,5 i 1,5.");
            zoom = z;
        }
        if (msg["collapsed"] is { } collapsedNode)
        {
            if (collapsedNode is not JsonObject changes)
                throw new BadParamsException("El camp 'collapsed' ha de ser un objecte (nom del requadre → true o false).");
            foreach (var (name, value) in changes)
            {
                if (name.Length is 0 or > 40 || value is not JsonValue b || !b.TryGetValue(out bool isCollapsed))
                    throw new BadParamsException("El camp 'collapsed' ha de ser un objecte (nom del requadre → true o false).");
                collapsed[name] = isCollapsed;
            }
            if (collapsed.Count > 50) throw new BadParamsException("Massa requadres a 'collapsed'.");
        }
        library.SetSetting(UiSettingKey, new JsonObject { ["zoom"] = zoom, ["collapsed"] = collapsed.DeepClone() }.ToJsonString());
        return new JsonObject { ["zoom"] = zoom, ["collapsed"] = collapsed.DeepClone() };
    }

    /// <summary>Atura un reproductor auxiliar (<c>player</c>, per defecte 1) amb un fos (<c>fadeMs</c>, per defecte 500).</summary>
    private JsonObject AuxStop(JsonObject msg)
    {
        var aux = Aux();
        int player = ReadPlayer(msg);
        int fadeMs = ReadInt(msg["fadeMs"]) ?? 500;
        if (fadeMs is < 0 or > 10_000) throw new BadParamsException("El camp 'fadeMs' ha de ser entre 0 i 10.000.");
        bool stopped = aux.Stop(player, TimeSpan.FromMilliseconds(fadeMs));
        return new JsonObject { ["stopped"] = stopped, ["aux"] = QueueJson.Aux(aux) };
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
        // Si s'atura: quant temps (D-073). Per defecte 2 h, com a molt 12 h; després es torna a activar sol
        TimeSpan? pause = null;
        if (msg["minutes"] is not null)
        {
            if (ReadInt(msg["minutes"]) is not { } minutes || minutes is < 1 or > 720)
                throw new BadParamsException("El camp 'minutes' ha de ser entre 1 i 720 (12 hores).");
            pause = TimeSpan.FromMinutes(minutes);
        }
        if (!autoDj.Configure(enabled, rotationId, "a mà", pause))
            throw new BadParamsException($"No existeix la rotació {rotationId}.");
        // Només es desa la rotació: en arrencar, el motor sempre activa l'AutoDJ (D-053)
        _library?.SetSetting(AutoDjSettingKey, JsonSerializer.Serialize(new { rotationId = autoDj.Rotation?.Id }));
        return new JsonObject { ["autodj"] = QueueJson.AutoDj(autoDj) };
    }

    /// <summary>
    /// Categoria de sweepers del trepitjador (fase 3, pas 5): <c>subcategoryId</c> (un número, o null perquè no n'hi hagi
    /// cap). Es desa sola (<see cref="AutoDj.SweepersChanged"/>). Retorna l'estat de l'AutoDJ, que la porta a <c>sweepers</c>.
    /// </summary>
    private JsonObject SweepersSet(JsonObject msg)
    {
        var autoDj = AutoDjOrError();
        if (!msg.ContainsKey("subcategoryId"))
            throw new BadParamsException("Falta 'subcategoryId' (un número, o null perquè no n'hi hagi cap).");
        long? subcategoryId = null;
        if (msg["subcategoryId"] is not null)
        {
            if (msg["subcategoryId"] is not JsonValue v || !v.TryGetValue(out long id))
                throw new BadParamsException("El camp 'subcategoryId' ha de ser un número o null.");
            subcategoryId = id;
        }
        if (!autoDj.SetSweepers(subcategoryId, "a mà"))
            throw new BadParamsException($"No existeix la subcategoria {subcategoryId}.");
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

    private Scheduler SchedulerOrError() => _scheduler ?? throw new BadParamsException("El motor no té planificador.");

    /// <summary>
    /// «Atura els esdeveniments» (fase 3, pas 2c; D-073): <c>minutes</c> (1 a 720) o <c>until</c> (una hora ISO, com a molt
    /// d'aquí a 12 h). Mai indefinidament.
    /// </summary>
    private JsonObject SchedulerPause(JsonObject msg)
    {
        var scheduler = SchedulerOrError();
        var now = DateTimeOffset.Now;
        DateTimeOffset until;
        if (msg["minutes"] is not null)
        {
            if (ReadInt(msg["minutes"]) is not { } minutes || minutes is < 1 or > 720)
                throw new BadParamsException("El camp 'minutes' ha de ser entre 1 i 720 (12 hores).");
            until = now.AddMinutes(minutes);
        }
        else if (ReadString(msg["until"]) is { } text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out var parsed))
        {
            if (parsed <= now) throw new BadParamsException("L'hora ha de ser més tard que ara.");
            if (parsed > now + Scheduler.MaxPause) throw new BadParamsException("Com a molt, 12 hores.");
            until = parsed;
        }
        else throw new BadParamsException("Falta 'minutes' (1 a 720) o 'until' (una hora ISO).");
        scheduler.Pause(until, "a mà");
        return new JsonObject { ["paused"] = EventJson.Paused(scheduler) };
    }

    private JsonObject SchedulerResume()
    {
        var scheduler = SchedulerOrError();
        scheduler.Resume();
        return new JsonObject { ["paused"] = EventJson.Paused(scheduler) };
    }

    private JsonObject SchedulerList()
    {
        var describer = Describer(); // un de sol per a tots: les rotacions es llegeixen una vegada
        return new JsonObject
        {
            ["events"] = new JsonArray(EventsOrError().List().Select(e => (JsonNode)EventJson.Event(e, describer)).ToArray()),
        };
    }

    private JsonObject SchedulerGet(JsonObject msg)
    {
        long id = RequireId(msg);
        var ev = EventsOrError().Get(id) ?? throw new BadParamsException($"No existeix l'esdeveniment {id}.");
        return new JsonObject { ["event"] = EventJson.Event(ev, Describer()) };
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
        var saved = events.Get(id)!;
        return new JsonObject { ["event"] = EventJson.Event(saved, Describer()), ["warnings"] = Warnings(saved) };
    }

    /// <summary>
    /// Les pròximes emissions d'un esdeveniment que encara no s'ha desat (fase 2, pas 8b): l'editor les mostra mentre
    /// s'edita. No desa res. Si l'esdeveniment no és coherent, <c>bad_params</c> amb el motiu.
    /// </summary>
    private JsonObject SchedulerPreview(JsonObject msg)
    {
        if (msg["event"] is not JsonObject json) throw new BadParamsException("Falta el camp 'event' (un objecte).");
        ScheduledEvent ev;
        try
        {
            ev = EventJson.FromJson(json);
            Events.Check(ev);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new BadParamsException(ex.Message);
        }
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 10, 1, 100);
        var now = DateTimeOffset.Now;
        var list = Recurrences.Between(ev, now, now.AddDays(400), Zone).Take(limit).ToList();
        return new JsonObject
        {
            ["occurrences"] = new JsonArray(list.Select(o => (JsonNode)EventJson.Occurrence(o)).ToArray()),
            ["warnings"] = Warnings(ev),
        };
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
            // includeDisabled (fase 2, pas 8a): també els desactivats, per veure'ls a la graella (p. ex. els importats de RadioDJ)
            bool includeDisabled = msg["includeDisabled"] is JsonValue v && v.TryGetValue(out bool b) && b;
            selected = events.List().Where(e => (e.Enabled || includeDisabled) && !e.IsTemplate);
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

    /// <summary>Avisos de l'editor (fase 2, pas 8c): què passarà els pròxims dies amb aquest esdeveniment (docs/protocol.md, 4.7).</summary>
    private JsonArray Warnings(ScheduledEvent ev)
    {
        var cache = new Dictionary<long, TrackFacts?>();
        TrackFacts? Facts(long id)
        {
            if (cache.TryGetValue(id, out var known)) return known;
            var track = _library?.GetTrack(id);
            var facts = track is null ? null : new TrackFacts(
                string.IsNullOrWhiteSpace(track.Title) ? System.IO.Path.GetFileNameWithoutExtension(track.Path) : track.Title,
                track.EndMs is { } end ? end - (track.StartMs ?? 0) : track.DurationMs,
                track.FileState == FileState.Missing);
            return cache[id] = facts;
        }
        var warnings = EventWarnings.For(ev, EventsOrError().List(), Facts, Zone, DateTimeOffset.Now);
        return new JsonArray(warnings.Select(w => (JsonNode)new JsonObject
        {
            ["text"] = w.Text,
            ["dates"] = new JsonArray(w.Dates.Select(d => (JsonNode)DbTime.ToText(d)).ToArray()),
        }).ToArray());
    }

    /// <summary>Per descriure les accions dels esdeveniments (títols de les pistes, rotacions…).</summary>
    private ActionDescriber Describer() => new(_library, _rotations);

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
