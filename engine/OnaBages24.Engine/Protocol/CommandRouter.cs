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
    private readonly LibraryTools.LibraryImporter? _importer;
    private readonly LibraryTools.FolderWatcher? _watcher;

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
        SweeperOverlay? sweepers = null, OutputProcessor? processor = null, LibraryTools.LibraryImporter? importer = null,
        LibraryTools.FolderWatcher? watcher = null)
    {
        _importer = importer;
        _watcher = watcher;
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
                "player.pause" => PlayerResult(Queue().PauseCurrent()),
                "player.resume" => PlayerResult(Queue().ResumePaused()),
                "player.seek" => PlayerSeek(msg),
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
                "subcategory.empty" => SubcategoryEmpty(msg),
                "folders.get" => FoldersGet(msg),
                "folders.set" => FoldersSet(msg),
                "folders.status" => new JsonObject { ["folders"] = QueueJson.Folders(WatcherOrError().Status) },
                "folders.scan" => FoldersScan(),
                "folders.log" => FoldersLog(msg),
                "library.waveform" => LibraryWaveform(msg),
                "library.setCues" => LibrarySetCues(msg),
                "library.recalculateCues" => LibraryRecalculateCues(msg),
                "library.missingFolders" => LibraryMissingFolders(),
                "library.rerootPreview" => LibraryReroot(msg, apply: false),
                "library.reroot" => LibraryReroot(msg, apply: true),
                "folders.renewPreview" => FoldersRenewPreview(msg),
                "folders.renew" => FoldersRenew(msg),
                "library.import" => LibraryImport(msg),
                "library.importStatus" => new JsonObject { ["import"] = ImporterOrError().Status is { } s ? QueueJson.Import(s) : null },
                "library.importCancel" => new JsonObject { ["cancelled"] = ImporterOrError().Cancel() },
                "autodj.get" => new JsonObject { ["autodj"] = QueueJson.AutoDj(AutoDjOrError()) },
                "autodj.set" => AutoDjSet(msg),
                "sweepers.set" => SweepersSet(msg),
                "rotations.list" => RotationsList(),
                "rotations.save" => RotationsSave(msg),
                "rotations.delete" => RotationsDelete(msg),
                "rotations.preview" => RotationsPreview(msg),
                "bags.list" => BagsList(),
                "bags.save" => BagsSave(msg),
                "bags.set" => BagsSet(msg),
                "bags.delete" => BagsDelete(msg),
                "separation.get" => SeparationJson(AutoDjOrError().Separation),
                "separation.set" => SeparationSet(msg),
                "calendar.get" => CalendarGet(msg),
                "calendar.set" => CalendarSet(msg),
                "reserve.get" => ReserveJson(AutoDjOrError().Reserve),
                "reserve.set" => ReserveSet(msg),
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

    // --- Pausa i salt dins de la pista (D-103) ---

    /// <summary>La cua, o l'error amb el motiu si l'ordre no s'ha pogut fer.</summary>
    private JsonObject PlayerResult(string? problem) =>
        problem is null ? new JsonObject { ["queue"] = QueueJson.Queue(Queue()) } : throw new BadParamsException(problem);

    /// <summary>Va a un altre punt de la pista que sona (o de la que està en pausa): <c>itemId</c> i <c>positionMs</c> (des del punt d'inici).</summary>
    private JsonObject PlayerSeek(JsonObject msg)
    {
        if (!Guid.TryParse(RequireString(msg, "itemId"), out var itemId)) throw new BadParamsException("El camp 'itemId' no és vàlid.");
        if (msg["positionMs"] is not JsonValue v || !v.TryGetValue(out long ms) || ms < 0)
            throw new BadParamsException("Falta 'positionMs' (un nombre de mil·lisegons, 0 o més).");
        return PlayerResult(Queue().Seek(itemId, TimeSpan.FromMilliseconds(ms)));
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

    /// <summary>
    /// Buida una subcategoria (fase 3, pas 7b; D-089): esborra de la biblioteca les seves pistes que no fa servir cap
    /// esdeveniment; les altres es queden com eren. Amb <c>dryRun: true</c> no fa res i només diu què faria (per confirmar-ho): quantes
    /// pistes hi ha, quantes fan servir esdeveniments i quines rotacions hi tenen algun pas.
    /// </summary>
    private JsonObject SubcategoryEmpty(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "subcategoryId");
        var sub = library.GetSubcategory(id) ?? throw new BadParamsException($"No existeix la subcategoria {id}.");
        if (_importer?.Status is { Running: true }) throw new BadParamsException("S'està important una carpeta: espera que acabi.");
        bool dryRun = msg["dryRun"] is JsonValue v && v.TryGetValue(out bool d) && d;

        var tracks = library.TracksInSubcategory(id);
        int used = tracks.Count(t => library.EventsUsingTrack(t.Id).Count > 0);
        var result = new JsonObject
        {
            ["subcategoryId"] = id,
            ["name"] = $"{sub.CategoryName} / {sub.Name}",
            ["tracks"] = tracks.Count,
            ["usedByEvents"] = used,
            ["rotations"] = new JsonArray(library.RotationsUsingSubcategory(id).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        };
        if (dryRun) return result;

        var (removed, kept) = LibraryTools.FolderImporter.RemoveOthers(library, id, []);
        result["removed"] = removed;
        result["keptForEvents"] = kept;
        LibraryChanged?.Invoke(null);
        return result;
    }

    // --- Editor de punts (fase 3, pas 7e; D-093) ---

    /// <summary>
    /// Forma d'ona d'una pista: <c>trackId</c>, <c>fromMs</c> i <c>toMs</c> (el tros; per defecte, tota la pista) i
    /// <c>buckets</c> (quants trossos; per defecte 800, com a molt 2.000). Els pics van de 0 a 255 en una escala de dB
    /// (de −60 a 0 dB). Només es descodifica el tros demanat.
    /// </summary>
    private JsonObject LibraryWaveform(JsonObject msg)
    {
        var library = LibraryOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        if (track.FileState == FileState.Missing) throw new BadParamsException("El fitxer d'aquesta pista falta: no se'n pot veure la forma d'ona.");
        long duration = track.DurationMs ?? 0;
        long from = Math.Max(0, msg["fromMs"] is JsonValue f && f.TryGetValue(out long fv) ? fv : 0);
        long to = msg["toMs"] is JsonValue t && t.TryGetValue(out long tv) ? tv : duration;
        if (duration > 0) to = Math.Min(to, duration);
        if (to <= from) throw new BadParamsException("El tros és buit ('toMs' ha de ser més gran que 'fromMs').");
        int buckets = Math.Clamp(ReadInt(msg["buckets"]) ?? 800, 1, LibraryTools.CueWaveform.MaxBuckets);
        byte[] peaks;
        try { peaks = LibraryTools.CueWaveform.Compute(track.Path, TimeSpan.FromMilliseconds(from), TimeSpan.FromMilliseconds(to), buckets); }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException($"No es pot llegir el fitxer: {ex.Message}");
        }
        return new JsonObject
        {
            ["trackId"] = track.Id,
            ["fromMs"] = from,
            ["toMs"] = to,
            ["floorDb"] = LibraryTools.CueWaveform.FloorDb,
            ["peaks"] = new JsonArray(peaks.Select(p => (JsonNode)JsonValue.Create((int)p)).ToArray()),
        };
    }

    /// <summary>
    /// Desa els punts editats a mà: <c>trackId</c>, <c>startMs</c>, <c>mixMs</c>, <c>endMs</c> (0 ≤ inici ≤ mescla ≤ final ≤
    /// durada). Queden com a «editats a mà»: l'analitzador no els tornarà a tocar (D-042).
    /// </summary>
    private JsonObject LibrarySetCues(JsonObject msg)
    {
        var library = LibraryOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        long Ms(string field) => msg[field] is JsonValue v && v.TryGetValue(out long ms)
            ? ms : throw new BadParamsException($"Falta '{field}' (mil·lisegons).");
        long start = Ms("startMs"), mix = Ms("mixMs"), end = Ms("endMs");
        if (start < 0 || mix < start || end < mix)
            throw new BadParamsException("Els punts han d'anar en ordre: inici ≤ mescla ≤ final.");
        if (track.DurationMs is { } duration && end > duration)
            throw new BadParamsException($"El final no pot passar de la durada de la pista ({duration} ms).");
        library.UpdateTrack(track with { StartMs = start, MixMs = mix, EndMs = end, CueSource = CueSource.Manual });
        LibraryChanged?.Invoke(track.Id);
        return TrackDetail(library, TrackOrError(library, track.Id));
    }

    /// <summary>
    /// Torna a calcular els punts amb l'analitzador: <c>trackId</c>. També si eren manuals (se substitueixen: és el que es
    /// demana en prémer «Recalcula»).
    /// </summary>
    private JsonObject LibraryRecalculateCues(JsonObject msg)
    {
        var library = LibraryOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        TrackCuePoints? cues;
        try { cues = CueAnalyzer.AnalyzeFile(track.Path); }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException($"No es pot llegir el fitxer: {ex.Message}");
        }
        static long? Ms(TimeSpan? t) => t is { } v ? (long)Math.Round(v.TotalMilliseconds) : null;
        library.UpdateTrack(track with
        {
            StartMs = Ms(cues?.Start), MixMs = Ms(cues?.Mix), EndMs = Ms(cues?.End), CueSource = CueSource.Analyzer,
        });
        LibraryChanged?.Invoke(track.Id);
        return TrackDetail(library, TrackOrError(library, track.Id));
    }

    // --- Canvi d'arrel (fase 3, pas 7d; D-092) ---

    /// <summary>Les carpetes on hi ha pistes amb el fitxer que falta, amb quantes (les 40 primeres), per triar l'arrel.</summary>
    private JsonObject LibraryMissingFolders() => new()
    {
        ["folders"] = new JsonArray(LibraryTools.PathReroot.MissingFolders(LibraryOrError(), 40)
            .Select(f => (JsonNode)new JsonObject { ["folder"] = f.Folder, ["count"] = f.Count }).ToArray()),
    };

    /// <summary>
    /// Canvi d'arrel: <c>from</c> (el principi de les rutes d'ara) i <c>to</c> (el nou; una carpeta que existeixi). Sense
    /// <paramref name="apply"/>, només diu què passaria (amb 10 exemples). Amb <paramref name="apply"/> ho fa, en una sola
    /// transacció; <c>onlyFound</c> (per defecte true): només les pistes que trobaran el fitxer.
    /// </summary>
    private JsonObject LibraryReroot(JsonObject msg, bool apply)
    {
        var library = LibraryOrError();
        string from = (ReadString(msg["from"]) ?? "").Trim();
        string to = (ReadString(msg["to"]) ?? "").Trim();
        if (from.Length == 0) throw new BadParamsException("Falta l'arrel d'ara (el principi de les rutes que es canvien).");
        if (!Path.IsPathFullyQualified(from)) throw new BadParamsException($"L'arrel d'ara ha de ser una ruta completa, amb la lletra de la unitat (p. ex. O:\\musica): «{from}».");
        if (to.Length == 0) throw new BadParamsException("Falta l'arrel nova.");
        if (!Path.IsPathFullyQualified(to)) throw new BadParamsException($"L'arrel nova ha de ser una ruta completa, amb la lletra de la unitat (p. ex. D:\\musica): «{to}».");
        if (!Directory.Exists(to)) throw new BadParamsException($"No es troba la carpeta nova: {to}");
        if (string.Equals(LibraryTools.PathReroot.Folder(from), LibraryTools.PathReroot.Folder(to), StringComparison.OrdinalIgnoreCase))
            throw new BadParamsException("L'arrel nova és la mateixa que la d'ara.");

        var plan = LibraryTools.PathReroot.Plan(library, from, to);
        var result = new JsonObject
        {
            ["from"] = plan.From,
            ["to"] = plan.To,
            ["tracks"] = plan.Items.Count,
            ["found"] = plan.Found,
            ["notFound"] = plan.NotFound,
            ["conflicts"] = plan.Conflicts,
            ["examples"] = new JsonArray(plan.Items.Take(10).Select(i => (JsonNode)new JsonObject
            {
                ["from"] = i.Track.Path, ["to"] = i.NewPath, ["exists"] = i.Exists, ["conflict"] = i.Conflict,
            }).ToArray()),
        };
        if (!apply) return result;

        bool onlyFound = msg["onlyFound"] is not JsonValue v || !v.TryGetValue(out bool o) || o;
        var done = LibraryTools.PathReroot.Apply(library, plan, onlyFound);
        result["changed"] = done.Changed;
        result["changedFound"] = done.Found;
        result["skipped"] = done.Skipped;
        result["foldersMoved"] = done.Folders;
        LibraryChanged?.Invoke(null);
        _watcher?.Poke();
        return result;
    }

    // --- Carpetes vigilades (fase 3, pas 7c; D-090) ---

    private LibraryTools.FolderWatcher WatcherOrError() => _watcher ?? throw new BadParamsException("El motor no vigila carpetes.");

    /// <summary>La carpeta vigilada d'una subcategoria i com queden les pistes noves: <c>subcategoryId</c>.</summary>
    private JsonObject FoldersGet(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "subcategoryId");
        var settings = library.GetFolderSettings(id) ?? throw new BadParamsException($"No existeix la subcategoria {id}.");
        return new JsonObject { ["folder"] = QueueJson.FolderSettings(settings) };
    }

    /// <summary>
    /// Desa la carpeta vigilada d'una subcategoria: <c>subcategoryId</c>, <c>folder</c> (ruta completa d'una carpeta que
    /// existeixi, o null per deixar de vigilar-la), <c>renewable</c> i, per a les pistes noves, <c>type</c>, <c>tags</c> i
    /// <c>variableDuration</c>. Es revisa de seguida.
    /// </summary>
    private JsonObject FoldersSet(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "subcategoryId");
        if (library.GetFolderSettings(id) is null) throw new BadParamsException($"No existeix la subcategoria {id}.");
        string? folder = ReadString(msg["folder"])?.Trim();
        if (string.IsNullOrEmpty(folder)) folder = null;
        if (folder is not null) folder = CheckWatchableFolder(library, id, folder);
        var type = TrackType.Music;
        if (msg["type"] is not null)
        {
            try { type = NowPlayingSettings.ParseType(ReadString(msg["type"])); }
            catch (FormatException ex) { throw new BadParamsException(ex.Message); }
        }
        List<string> tags = [];
        if (msg["tags"] is not null)
        {
            if (msg["tags"] is not JsonArray list || list.Any(t => ReadString(t) is null))
                throw new BadParamsException("El camp 'tags' ha de ser una llista de textos.");
            tags = list.Select(t => ReadString(t)!.Trim()).Where(t => t.Length is > 0 and <= 100).ToList();
        }
        bool renewable = msg["renewable"] is JsonValue r && r.TryGetValue(out bool rv) && rv;
        bool variable = msg["variableDuration"] is JsonValue v && v.TryGetValue(out bool vv) && vv;

        library.SetFolderSettings(id, folder, renewable, type, tags, variable);
        _watcher?.Poke();
        LibraryChanged?.Invoke(null);
        return new JsonObject { ["folder"] = QueueJson.FolderSettings(library.GetFolderSettings(id)!) };
    }

    /// <summary>
    /// Comprova que la carpeta es pot vigilar des de la subcategoria <paramref name="subcategoryId"/>: ruta completa, que existeixi,
    /// que no sigui una unitat sencera i que no se solapi amb la d'una altra subcategoria. Retorna la ruta sense «\» al final.
    /// </summary>
    private static string CheckWatchableFolder(Library library, long subcategoryId, string folder)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new BadParamsException("La carpeta ha de ser una ruta completa (p. ex. O:\\musica\\BOSSA 9).");
        if (!Directory.Exists(folder)) throw new BadParamsException($"No es troba la carpeta: {folder}");
        folder = folder.TrimEnd('\\', '/');
        if (folder.Length == 2 && folder[1] == ':') throw new BadParamsException("No es pot vigilar una unitat sencera: tria una carpeta.");
        string normalized = folder + "\\";
        foreach (var other in library.WatchedFolders().Where(f => f.SubcategoryId != subcategoryId))
        {
            string otherFolder = other.Folder!.TrimEnd('\\', '/') + "\\";
            if (normalized.StartsWith(otherFolder, StringComparison.OrdinalIgnoreCase) || otherFolder.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                throw new BadParamsException($"Aquesta carpeta ja la vigila «{other.Name}» (o hi és a dins, o la conté): {other.Folder}");
        }
        return folder;
    }

    /// <summary>
    /// «Renova la bossa» (D-091), sense canviar res: què passaria amb la carpeta nova. <c>subcategoryId</c>, <c>folder</c>.
    /// Retorna quantes es mantenen, entren i surten (i les que surten però fan servir esdeveniments), amb els noms (com a
    /// molt 300 de cada).
    /// </summary>
    private JsonObject FoldersRenewPreview(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "subcategoryId");
        var settings = library.GetFolderSettings(id) ?? throw new BadParamsException($"No existeix la subcategoria {id}.");
        string folder = CheckWatchableFolder(library, id, (ReadString(msg["folder"]) ?? "").Trim());
        List<string> files;
        try { files = LibraryTools.FolderImporter.ListAudioFiles(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new BadParamsException($"No es pot llegir la carpeta: {ex.Message}"); }
        var plan = LibraryTools.FolderImporter.PlanRenewal(library, id, files);
        static JsonArray Names(IEnumerable<string> names) => new(names.Take(300).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        static string Label(Track t) => string.IsNullOrEmpty(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";
        return new JsonObject
        {
            ["subcategoryId"] = id,
            ["name"] = settings.Name,
            ["folder"] = folder,
            ["files"] = files.Count,
            ["keep"] = plan.Keep.Count,
            ["enter"] = plan.Enter.Count,
            ["leave"] = plan.Leave.Count,
            ["leaveInEvents"] = plan.LeaveInEvents.Count,
            ["elsewhere"] = plan.ElsewhereCount,
            ["keepNames"] = Names(plan.Keep.Select(k => Label(k.Track))),
            ["enterNames"] = Names(plan.Enter.Select(p => Path.GetFileNameWithoutExtension(p) ?? p)),
            ["leaveNames"] = Names(plan.Leave.Select(Label)),
            ["leaveInEventsNames"] = Names(plan.LeaveInEvents.Select(Label)),
        };
    }

    /// <summary>
    /// «Renova la bossa» (D-091): en segon pla, com una importació (el progrés arriba amb <c>library.importProgress</c>).
    /// <c>subcategoryId</c>, <c>folder</c>, <c>watch</c> (true = a partir d'ara la subcategoria vigila aquesta carpeta).
    /// Les cançons noves es queden com diu la configuració de la carpeta de la subcategoria (tipus, etiquetes…).
    /// </summary>
    private JsonObject FoldersRenew(JsonObject msg)
    {
        var library = LibraryOrError();
        var importer = ImporterOrError();
        long id = RequiredId(msg, "subcategoryId");
        var settings = library.GetFolderSettings(id) ?? throw new BadParamsException($"No existeix la subcategoria {id}.");
        string folder = CheckWatchableFolder(library, id, (ReadString(msg["folder"]) ?? "").Trim());
        bool watch = msg["watch"] is JsonValue v && v.TryGetValue(out bool w) && w;
        try
        {
            var status = importer.Start(folder, new LibraryTools.ImportDefaults(id, settings.DefaultType, settings.DefaultTags,
                settings.DefaultVariableDuration, EmptySubcategory: true, Renew: true, WatchAfter: watch), settings.Name);
            return new JsonObject { ["import"] = QueueJson.Import(status) };
        }
        catch (InvalidOperationException ex)
        {
            throw new BadParamsException(ex.Message);
        }
    }

    private JsonObject FoldersScan()
    {
        WatcherOrError().Poke();
        return new JsonObject { ["scanning"] = true };
    }

    /// <summary>El registre de les carpetes vigilades, els més recents primer: <c>subcategoryId</c> (opcional) i <c>limit</c> (per defecte 100, com a molt 1.000).</summary>
    private JsonObject FoldersLog(JsonObject msg)
    {
        var library = LibraryOrError();
        long? sub = msg["subcategoryId"] is JsonValue v && v.TryGetValue(out long id) ? id : null;
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 1000);
        return new JsonObject { ["entries"] = new JsonArray(library.FolderLog(limit, sub).Select(e => (JsonNode)QueueJson.FolderLog(e)).ToArray()) };
    }

    // --- Importació de carpetes (fase 3, pas 7b) ---

    private LibraryTools.LibraryImporter ImporterOrError() => _importer ?? throw new BadParamsException("El motor no pot importar carpetes.");

    /// <summary>
    /// Comença a importar una carpeta en segon pla: <c>folder</c> (ruta completa) o, en lloc d'una carpeta, uns fitxers
    /// solts, <c>files</c> (llista de rutes completes; no buiden la subcategoria), <c>subcategoryId</c> i, per a les pistes
    /// noves, <c>type</c> (per defecte <c>music</c>), <c>tags</c> i <c>variableDuration</c>. El progrés arriba amb
    /// <c>library.importProgress</c>.
    /// </summary>
    private JsonObject LibraryImport(JsonObject msg)
    {
        var library = LibraryOrError();
        var importer = ImporterOrError();

        // Fitxers solts (en lloc d'una carpeta): «files», una llista de rutes completes de fitxers d'àudio que existeixin
        List<string>? files = null;
        string folder = "";
        if (msg["files"] is not null)
        {
            if (msg["files"] is not JsonArray list || list.Count == 0 || list.Any(f => ReadString(f) is null))
                throw new BadParamsException("El camp 'files' ha de ser una llista de rutes de fitxers.");
            files = list.Select(f => ReadString(f)!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string file in files)
            {
                if (!Path.IsPathFullyQualified(file)) throw new BadParamsException($"Cal la ruta completa del fitxer: {file}");
                if (!File.Exists(file)) throw new BadParamsException($"No es troba el fitxer: {file}");
                if (!LibraryTools.FolderImporter.AudioExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    throw new BadParamsException($"No és un fitxer d'àudio que es pugui importar: {Path.GetFileName(file)}");
            }
        }
        else
        {
            folder = (ReadString(msg["folder"]) ?? "").Trim();
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) throw new BadParamsException("Falta 'folder' (la ruta completa de la carpeta) o 'files'.");
            if (!Directory.Exists(folder)) throw new BadParamsException($"No es troba la carpeta: {folder}");
        }
        long subcategoryId = RequiredId(msg, "subcategoryId");
        var subcategory = library.GetSubcategory(subcategoryId) ?? throw new BadParamsException($"No existeix la subcategoria {subcategoryId}.");
        var type = TrackType.Music;
        if (msg["type"] is not null)
        {
            try { type = NowPlayingSettings.ParseType(ReadString(msg["type"])); }
            catch (FormatException ex) { throw new BadParamsException(ex.Message); }
        }
        List<string> tags = [];
        if (msg["tags"] is not null)
        {
            if (msg["tags"] is not JsonArray list || list.Any(t => ReadString(t) is null))
                throw new BadParamsException("El camp 'tags' ha de ser una llista de textos.");
            tags = list.Select(t => ReadString(t)!.Trim()).Where(t => t.Length is > 0 and <= 100).ToList();
        }
        bool variable = msg["variableDuration"] is JsonValue v && v.TryGetValue(out bool b) && b;
        bool empty = msg["emptySubcategory"] is JsonValue e && e.TryGetValue(out bool emptyValue) && emptyValue;

        try
        {
            var defaults = new LibraryTools.ImportDefaults(subcategoryId, type, tags, variable, empty);
            string name = $"{subcategory.CategoryName} / {subcategory.Name}";
            var status = files is null ? importer.Start(folder, defaults, name) : importer.StartFiles(files, defaults, name);
            return new JsonObject { ["import"] = QueueJson.Import(status) };
        }
        catch (InvalidOperationException ex)
        {
            throw new BadParamsException(ex.Message);
        }
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

    // --- Editor de rotacions (fase 3, pas 8a; D-094) ---

    /// <summary>Quan es desa o s'esborra una rotació: les interfícies tornen a demanar les rotacions (<c>rotations.changed</c>).</summary>
    public event Action? RotationsChanged;

    /// <summary>
    /// Crea una rotació (sense <c>rotationId</c>) o la substitueix (amb <c>rotationId</c>): <c>name</c> i <c>steps</c>, una
    /// llista en ordre de <c>{ subcategoryId }</c> o <c>{ categoryId }</c> (qualsevol subcategoria de la categoria), amb
    /// <c>tagId</c>, <c>selection</c> (<c>leastRecent</c>, <c>leastPlayed</c>, <c>random</c>), <c>repeatRule</c> i
    /// <c>sweeper</c> opcionals. Si és l'activa, l'AutoDJ agafa els passos nous de seguida.
    /// </summary>
    private JsonObject RotationsSave(JsonObject msg)
    {
        var rotations = _rotations ?? throw new BadParamsException("El motor no té rotacions.");
        var library = LibraryOrError();
        string name = RequiredName(msg);
        if (msg["steps"] is not JsonArray list || list.Count == 0) throw new BadParamsException("Una rotació ha de tenir com a mínim un pas ('steps').");
        if (list.Count > 200) throw new BadParamsException("Una rotació pot tenir com a molt 200 passos.");
        var (categories, tags) = library.SearchFilters();
        var steps = new List<RotationStep>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject s) throw new BadParamsException($"El pas {i + 1} no és vàlid.");
            long? sub = s["subcategoryId"] is JsonValue sv && sv.TryGetValue(out long subId) ? subId : null;
            long? cat = s["categoryId"] is JsonValue cv && cv.TryGetValue(out long catId) ? catId : null;
            long? tag = s["tagId"] is JsonValue tv && tv.TryGetValue(out long tagId) ? tagId : null;
            long? bag = s["bagId"] is JsonValue bv && bv.TryGetValue(out long bagId) ? bagId : null;
            if (bag is { } ba)
            {
                if (rotations.GetBag(ba) is null) throw new BadParamsException($"Pas {i + 1}: no existeix la bossa {ba}.");
                sub = null;
                cat = null;
            }
            if (sub is { } su && library.GetSubcategory(su) is null) throw new BadParamsException($"Pas {i + 1}: no existeix la subcategoria {su}.");
            if (bag is null && sub is null && cat is null) throw new BadParamsException($"Pas {i + 1}: falta d'on surt la pista (una bossa, una subcategoria o una categoria).");
            if (sub is null && cat is { } ca && categories.All(c => c.Id != ca)) throw new BadParamsException($"Pas {i + 1}: no existeix la categoria {ca}.");
            if (tag is { } ta && tags.All(t => t.Id != ta)) throw new BadParamsException($"Pas {i + 1}: no existeix l'etiqueta {ta}.");
            var selection = ReadString(s["selection"]) switch
            {
                null or "leastRecent" => RotationSelection.LeastRecent,
                "leastPlayed" => RotationSelection.LeastPlayed,
                "random" => RotationSelection.Random,
                var other => throw new BadParamsException($"Pas {i + 1}: la selecció «{other}» no existeix (leastRecent, leastPlayed o random)."),
            };
            bool Flag(string field, bool fallback) => s[field] is JsonValue fv && fv.TryGetValue(out bool b) ? b : fallback;
            steps.Add(new RotationStep
            {
                BagId = bag,
                SubcategoryId = sub,
                CategoryId = sub is null ? cat : null,
                TagId = tag,
                Selection = selection,
                RepeatRule = Flag("repeatRule", true),
                Sweeper = Flag("sweeper", false),
            });
        }

        long id;
        if (msg["rotationId"] is null) id = rotations.Create(name, steps);
        else rotations.Update(id = RequiredId(msg, "rotationId"), name, steps);
        _autoDj?.RotationsEdited();
        RotationsChanged?.Invoke();
        return new JsonObject { ["rotation"] = QueueJson.Rotation(rotations.Get(id)!) };
    }

    // --- Bosses amb nom (fase 3, pas 8c; D-096) ---

    private Rotations RotationsOrError() => _rotations ?? throw new BadParamsException("El motor no té rotacions.");

    private JsonObject BagsList() => new()
    {
        ["bags"] = new JsonArray(RotationsOrError().Bags().Select(b => (JsonNode)QueueJson.Bag(b)).ToArray()),
    };

    /// <summary>
    /// Crea una bossa (sense <c>bagId</c>: <c>name</c> i <c>subcategoryId</c>, que pot ser null) o en canvia el nom (amb
    /// <c>bagId</c> i <c>name</c>). Per canviar-ne la subcategoria, <c>bags.set</c>.
    /// </summary>
    private JsonObject BagsSave(JsonObject msg)
    {
        var rotations = RotationsOrError();
        string name = RequiredName(msg);
        long id;
        if (msg["bagId"] is null)
        {
            long? sub = msg["subcategoryId"] is JsonValue v && v.TryGetValue(out long s) ? s : null;
            if (sub is { } su && LibraryOrError().GetSubcategory(su) is null) throw new BadParamsException($"No existeix la subcategoria {su}.");
            id = rotations.CreateBag(name, sub, "a mà", DateTimeOffset.UtcNow);
        }
        else rotations.RenameBag(id = RequiredId(msg, "bagId"), name);
        _autoDj?.BagsEdited();
        if (_autoDj is null) RotationsChanged?.Invoke(); // amb AutoDJ, avisa ell (BagsChanged)
        return new JsonObject { ["bag"] = QueueJson.Bag(rotations.GetBag(id)!) };
    }

    /// <summary>
    /// Canvia la subcategoria d'una bossa: <c>bagId</c>, <c>subcategoryId</c> (null = cap: els passos que hi apunten se
    /// salten). Els passos triaran d'aquesta subcategoria a partir de la pròxima cançó.
    /// </summary>
    private JsonObject BagsSet(JsonObject msg)
    {
        var rotations = RotationsOrError();
        long id = RequiredId(msg, "bagId");
        if (rotations.GetBag(id) is null) throw new BadParamsException($"No existeix la bossa {id}.");
        if (!msg.ContainsKey("subcategoryId")) throw new BadParamsException("Falta 'subcategoryId' (un número o null).");
        long? sub = msg["subcategoryId"] is null ? null
            : msg["subcategoryId"] is JsonValue v && v.TryGetValue(out long s) ? s : throw new BadParamsException("El camp 'subcategoryId' ha de ser un número o null.");
        if (_autoDj is { } autoDj)
        {
            if (!autoDj.SetBag(id, sub, "a mà")) throw new BadParamsException($"No existeix la subcategoria {sub}.");
        }
        else
        {
            if (sub is { } su && LibraryOrError().GetSubcategory(su) is null) throw new BadParamsException($"No existeix la subcategoria {su}.");
            rotations.SetBagSubcategory(id, sub, "a mà", DateTimeOffset.UtcNow);
        }
        if (_autoDj is null) RotationsChanged?.Invoke(); // amb AutoDJ, avisa ell (BagsChanged)
        return new JsonObject { ["bag"] = QueueJson.Bag(rotations.GetBag(id)!) };
    }

    /// <summary>Esborra una bossa: <c>bagId</c>. No es pot si hi apunta algun pas de rotació o alguna acció BOSSA.</summary>
    private JsonObject BagsDelete(JsonObject msg)
    {
        var rotations = RotationsOrError();
        long id = RequiredId(msg, "bagId");
        var bag = rotations.GetBag(id) ?? throw new BadParamsException($"No existeix la bossa {id}.");
        if (bag.Steps > 0)
        {
            var names = rotations.List().Where(r => r.Steps.Any(s => s.BagId == id)).Select(r => $"«{r.Name}»");
            throw new BadParamsException($"No es pot esborrar: hi apunten passos de {string.Join(", ", names)}. Canvia'ls primer.");
        }
        var events = rotations.EventsUsingBag(id);
        if (events.Count > 0)
            throw new BadParamsException($"No es pot esborrar: la canvia{(events.Count == 1 ? " l'esdeveniment" : "n els esdeveniments")} " +
                                         string.Join(", ", events.Select(e => $"«{e.Name}»")) + ".");
        rotations.DeleteBag(id);
        _autoDj?.BagsEdited();
        if (_autoDj is null) RotationsChanged?.Invoke(); // amb AutoDJ, avisa ell (BagsChanged)
        return new JsonObject { ["deleted"] = id };
    }

    // --- Separació real i previsualització (fase 3, pas 8b; D-095) ---

    /// <summary>Clau de la configuració amb la separació real: <c>{ "trackMinutes": 360, "artistMinutes": 60 }</c>.</summary>
    public const string SeparationSettingKey = "separacio";

    /// <summary>La separació desada (o la per defecte, si no n'hi ha o no és vàlida).</summary>
    public static SeparationRules ReadSeparation(Library? library)
    {
        try
        {
            if (library?.GetSetting(SeparationSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
            {
                double track = saved["trackMinutes"]?.GetValue<double>() ?? SeparationRules.Default.Track.TotalMinutes;
                double artist = saved["artistMinutes"]?.GetValue<double>() ?? SeparationRules.Default.Artist.TotalMinutes;
                double max = SeparationRules.Max.TotalMinutes;
                if (track is >= 0 && track <= max && artist >= 0 && artist <= max)
                    return new SeparationRules(TimeSpan.FromMinutes(track), TimeSpan.FromMinutes(artist));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        return SeparationRules.Default;
    }

    private static JsonObject SeparationJson(SeparationRules s) => new()
    {
        ["trackMinutes"] = (int)Math.Round(s.Track.TotalMinutes),
        ["artistMinutes"] = (int)Math.Round(s.Artist.TotalMinutes),
    };

    /// <summary>Canvia la separació real: <c>trackMinutes</c> i <c>artistMinutes</c> (de 0 a 10.080, una setmana), tots dos opcionals.</summary>
    private JsonObject SeparationSet(JsonObject msg)
    {
        var autoDj = AutoDjOrError();
        var current = autoDj.Separation;
        int max = (int)SeparationRules.Max.TotalMinutes;
        TimeSpan Read(string field, TimeSpan fallback)
        {
            if (msg[field] is null) return fallback;
            if (msg[field] is not JsonValue v || !v.TryGetValue(out int minutes) || minutes < 0 || minutes > max)
                throw new BadParamsException($"El camp '{field}' ha de ser un nombre de minuts entre 0 i {max:N0}.");
            return TimeSpan.FromMinutes(minutes);
        }
        var rules = new SeparationRules(Read("trackMinutes", current.Track), Read("artistMinutes", current.Artist));
        autoDj.Separation = rules;
        var result = SeparationJson(rules);
        _library?.SetSetting(SeparationSettingKey, result.ToJsonString());
        return result;
    }

    // --- Calendari d'una pista (fase 4, pas 2; D-104) ---

    /// <summary>
    /// El calendari d'una pista: <c>trackId</c> i, opcionals, <c>from</c> i <c>to</c> (dies, per als recomptes). Sense dates,
    /// el període és el de la pista (les seves dates) o, si no en té, les 4 setmanes que comencen avui.
    /// </summary>
    private JsonObject CalendarGet(JsonObject msg)
    {
        var library = LibraryOrError();
        var events = EventsOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        var today = DateOnly.FromDateTime(DateTime.Today);
        DateOnly? Day(string field)
        {
            if (ReadString(msg[field]) is not { } text) return null;
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d : throw new BadParamsException($"El camp '{field}' ha de ser una data AAAA-MM-DD.");
        }
        var from = Day("from") ?? track.ValidFrom ?? today;
        var to = Day("to") ?? track.ValidUntil ?? from.AddDays(27);
        if (to < from) throw new BadParamsException("El període s'acaba abans de començar (mira les dates de la pista).");
        if (to.DayNumber - from.DayNumber > 400) to = from.AddDays(400);

        var all = events.List();
        var rows = TrackCalendar.Build(all, track.Id, today);
        var counts = TrackCalendar.Count(all, track, from, to, today);
        JsonNode? Cell(CalendarCell? c) => c is null ? null : new JsonObject
        {
            ["eventId"] = c.Event.Id,
            ["name"] = c.Event.Name,
            ["mode"] = Events.ModeText(c.Event.Mode),
            ["enabled"] = c.Event.Enabled,
            ["commercialBlock"] = c.Event.CommercialBlock,
            ["repeats"] = c.Event.Repeat != EventRepeat.None, // marcar-la la posa a totes les hores de l'esdeveniment
            ["nonWeekly"] = c.Event.Recurrence != EventRecurrence.Weekly, // no sona totes les setmanes
            // Dates soltes: quines (d'avui en endavant), per dir-ho a la casella
            ["dates"] = new JsonArray(TrackCalendar.UpcomingDates(c.Event, today).Select(d => (JsonNode)JsonValue.Create(d.ToString("yyyy-MM-dd"))!).ToArray()),
            ["marked"] = c.Marked,
            ["busyBy"] = c.BusyBy,
            ["others"] = new JsonArray(c.Others.Select(o => (JsonNode)JsonValue.Create(o)!).ToArray()),
        };
        var trackJson = QueueJson.Track(track);
        trackJson["validFrom"] = track.ValidFrom?.ToString("yyyy-MM-dd");
        trackJson["validUntil"] = track.ValidUntil?.ToString("yyyy-MM-dd");
        return new JsonObject
        {
            ["track"] = trackJson,
            ["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["time"] = r.Time.ToString("HH:mm:ss"),
                ["days"] = new JsonArray(r.Days.Select(Cell).ToArray()),
            }).ToArray()),
            ["counts"] = new JsonObject
            {
                ["from"] = counts.From.ToString("yyyy-MM-dd"),
                ["to"] = counts.To.ToString("yyyy-MM-dd"),
                ["periodFromTrack"] = Day("from") is null && track.ValidFrom is not null && track.ValidUntil is not null,
                ["total"] = counts.Total,
                ["perWeekday"] = new JsonArray(counts.PerWeekday.Select(n => (JsonNode)n).ToArray()),
                ["weeks"] = new JsonArray(counts.Weeks.Select(w => (JsonNode)new JsonObject { ["from"] = w.WeekStart.ToString("yyyy-MM-dd"), ["count"] = w.Count }).ToArray()),
                ["months"] = new JsonArray(counts.Months.Select(m => (JsonNode)new JsonObject { ["month"] = $"{m.Year:0000}-{m.Month:00}", ["count"] = m.Count }).ToArray()),
                ["markedInDisabled"] = counts.MarkedInDisabled,
            },
        };
    }

    /// <summary>
    /// Marca o desmarca una casella: <c>trackId</c>, <c>time</c> («HH:MM» o «HH:MM:SS»), <c>day</c> (0 = dilluns … 6 =
    /// diumenge) i <c>marked</c>. Si a la casella hi ha un esdeveniment, la pista hi entra o en surt; si no n'hi ha cap
    /// (D-105), va al bloc de falques d'aquella hora, que es crea si no existeix. Un bloc que es queda sense pistes s'esborra.
    /// Tot es desa d'una vegada i el planificador ho té en compte de seguida. Retorna el calendari (com <c>calendar.get</c>,
    /// amb el mateix <c>from</c> i <c>to</c> si s'envien).
    /// </summary>
    private JsonObject CalendarSet(JsonObject msg)
    {
        var library = LibraryOrError();
        var events = EventsOrError();
        var track = TrackOrError(library, RequiredId(msg, "trackId"));
        string text = (ReadString(msg["time"]) ?? "").Trim();
        if (!TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], out var time))
            throw new BadParamsException("Falta 'time' (l'hora de la casella, HH:MM).");
        int day = ReadInt(msg["day"]) ?? -1;
        if (day is < 0 or > 6) throw new BadParamsException("El dia ha de ser de 0 (dilluns) a 6 (diumenge).");
        if (msg["marked"] is not JsonValue mv || !mv.TryGetValue(out bool marked)) throw new BadParamsException("Falta 'marked' (true o false).");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var all = events.List();
        // L'esdeveniment de la casella; si no n'hi ha cap, el bloc de falques d'aquella hora (un de nou si no n'hi ha)
        var ev = TrackCalendar.CellAt(TrackCalendar.Build(all, track.Id, today), time, day)?.Event;
        if (ev is null)
        {
            if (!marked) return CalendarGet(msg); // treure-la d'on no hi és: res a fer
            ev = all.FirstOrDefault(e => e.CommercialBlock && !e.IsTemplate && e.StartTime == time) ?? TrackCalendar.NewBlock(time);
        }
        try
        {
            var changed = TrackCalendar.Set(ev, track.Id, day, marked, id => library.GetTrack(id)?.Type, today);
            if (TrackCalendar.IsEmptyBlock(changed)) { if (changed.Id != 0) events.Delete(changed.Id); }
            else events.Save(changed);
        }
        catch (ArgumentException ex)
        {
            throw new BadParamsException(ex.Message);
        }
        // Els blocs de falques buits (p. ex. d'abans, o si s'han buidat des del Planificador) s'esborren (D-105)
        foreach (var empty in events.List().Where(TrackCalendar.IsEmptyBlock)) events.Delete(empty.Id);
        _scheduler?.Reload();
        return CalendarGet(msg);
    }

    // --- Reserva de l'AutoDJ (fase 3, pas 8d; D-099) ---

    /// <summary>Clau de la configuració amb la reserva: <c>{ "rotationId": 3, "excludedSubcategoryIds": [12, 40] }</c>.</summary>
    public const string ReserveSettingKey = "reserva";

    /// <summary>La reserva desada (o cap, si no n'hi ha o no és vàlida).</summary>
    public static ReserveSettings ReadReserve(Library? library)
    {
        try
        {
            if (library?.GetSetting(ReserveSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
            {
                long? rotationId = saved["rotationId"]?.GetValue<long>();
                var excluded = saved["excludedSubcategoryIds"] is JsonArray list
                    ? list.Select(n => n!.GetValue<long>()).ToHashSet()
                    : [];
                return new ReserveSettings(rotationId, excluded);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NullReferenceException) { }
        return ReserveSettings.None;
    }

    /// <summary>La reserva per al protocol, amb els noms (la rotació i les subcategories que ja no existeixen no hi surten).</summary>
    private JsonObject ReserveJson(ReserveSettings reserve)
    {
        var rotation = reserve.RotationId is { } id ? _rotations?.Get(id) : null;
        var excluded = reserve.ExcludedSubcategoryIds
            .Select(s => _library?.GetSubcategory(s))
            .OfType<SubcategoryRef>()
            .OrderBy(s => s.CategoryName).ThenBy(s => s.Name)
            .Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["name"] = s.Name, ["categoryName"] = s.CategoryName })
            .ToArray();
        return new JsonObject
        {
            ["rotationId"] = rotation?.Id,
            ["rotationName"] = rotation?.Name,
            ["excludedSubcategories"] = new JsonArray(excluded),
        };
    }

    /// <summary>
    /// Canvia la reserva: <c>rotationId</c> (la rotació de reserva, o null perquè no n'hi hagi cap) i
    /// <c>excludedSubcategoryIds</c> (les subcategories «no de reserva»). Tots dos opcionals: el que no s'envia no canvia.
    /// </summary>
    private JsonObject ReserveSet(JsonObject msg)
    {
        var autoDj = AutoDjOrError();
        var library = LibraryOrError();
        var current = autoDj.Reserve;
        long? rotationId = current.RotationId;
        if (msg.ContainsKey("rotationId"))
        {
            if (msg["rotationId"] is null) rotationId = null;
            else
            {
                rotationId = RequiredId(msg, "rotationId");
                if (_rotations?.Get(rotationId.Value) is null) throw new BadParamsException($"No existeix la rotació {rotationId}.");
            }
        }
        var excluded = current.ExcludedSubcategoryIds;
        if (msg.ContainsKey("excludedSubcategoryIds"))
        {
            if (msg["excludedSubcategoryIds"] is not JsonArray list || list.Any(n => n is not JsonValue v || !v.TryGetValue(out long _)))
                throw new BadParamsException("El camp 'excludedSubcategoryIds' ha de ser una llista de números.");
            var ids = list.Select(n => n!.GetValue<long>()).ToHashSet();
            foreach (long s in ids)
                if (library.GetSubcategory(s) is null) throw new BadParamsException($"No existeix la subcategoria {s}.");
            excluded = ids;
        }
        var reserve = new ReserveSettings(rotationId, excluded);
        autoDj.Reserve = reserve;
        library.SetSetting(ReserveSettingKey, new JsonObject
        {
            ["rotationId"] = rotationId,
            ["excludedSubcategoryIds"] = new JsonArray(excluded.Order().Select(s => (JsonNode)s).ToArray()),
        }.ToJsonString());
        RotationsChanged?.Invoke(); // la pantalla Rotacions la mostra
        return ReserveJson(reserve);
    }

    /// <summary>
    /// Les properes pistes que triaria una rotació, sense canviar res: <c>rotationId</c>, <c>count</c> (per defecte 20, com a
    /// molt 100). Cada una amb el pas d'on surt (o la reserva), la regla que s'ha hagut d'afluixar i l'avís, si n'hi ha.
    /// </summary>
    private JsonObject RotationsPreview(JsonObject msg)
    {
        var rotations = _rotations ?? throw new BadParamsException("El motor no té rotacions.");
        var autoDj = AutoDjOrError();
        long id = RequiredId(msg, "rotationId");
        var rotation = rotations.Get(id) ?? throw new BadParamsException($"No existeix la rotació {id}.");
        int count = Math.Clamp(ReadInt(msg["count"]) ?? 20, 1, 100);
        var items = autoDj.Preview(rotation, count);
        return new JsonObject
        {
            ["rotationId"] = id,
            ["fromStep"] = autoDj.Rotation?.Id == id ? autoDj.NextStep : 0,
            ["separation"] = SeparationJson(autoDj.Separation),
            ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
            {
                ["stepIndex"] = i.StepIndex,
                ["step"] = i.Source ?? i.Step?.ToString(),
                ["track"] = i.Track is null ? null : QueueJson.Track(i.Track),
                ["relaxed"] = i.Relaxed,
                ["problem"] = i.Problem,
                ["fallback"] = i.Fallback,
            }).ToArray()),
        };
    }

    /// <summary>Esborra una rotació: <c>rotationId</c>. No es pot si és l'activa o si la posa algun esdeveniment (es diu quins).</summary>
    private JsonObject RotationsDelete(JsonObject msg)
    {
        var rotations = _rotations ?? throw new BadParamsException("El motor no té rotacions.");
        long id = RequiredId(msg, "rotationId");
        var rotation = rotations.Get(id) ?? throw new BadParamsException($"No existeix la rotació {id}.");
        if (_autoDj?.Rotation?.Id == id)
            throw new BadParamsException($"«{rotation.Name}» és la rotació activa de l'AutoDJ: canvia-la primer.");
        if (_autoDj?.Reserve.RotationId == id)
            throw new BadParamsException($"«{rotation.Name}» és la rotació de reserva: tria'n una altra (o cap) primer.");
        var events = rotations.EventsUsingRotation(id);
        if (events.Count > 0)
            throw new BadParamsException($"No es pot esborrar: la posa{(events.Count == 1 ? " l'esdeveniment" : "n els esdeveniments")} " +
                                         string.Join(", ", events.Select(e => $"«{e.Name}»")) + ".");
        rotations.Delete(id);
        RotationsChanged?.Invoke();
        return new JsonObject { ["deleted"] = id };
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
