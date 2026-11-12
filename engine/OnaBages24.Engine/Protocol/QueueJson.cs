using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Protocol;

/// <summary>Conversió de la cua, les pistes i la sortida al format del protocol (docs/protocol.md, 4.2 a 4.4).</summary>
public static class QueueJson
{
    public static JsonObject Item(QueueItem item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["trackId"] = item.TrackId,
        ["path"] = item.Path,
        ["artist"] = item.Artist,
        ["title"] = item.Title,
        ["type"] = Type(item.Type),
        ["origin"] = Origin(item.Origin),
        ["eventName"] = item.EventName,
        ["durationMs"] = item.PlannedMs,
        // Trepitjador (fase 3, pas 5): el sweeper que sonarà a sobre de l'inici, o null
        ["sweeper"] = item.Sweeper is { } sweeper ? Item(sweeper) : null,
    };

    /// <summary>
    /// La cua (docs/protocol.md, 4.2). Fase 3, pas 1: cada pista que espera porta <c>plannedAt</c> (hora prevista d'inici,
    /// o null si no se sap) i la cua porta <c>currentEndsAt</c> (quan s'acaba la que sona). Hores locals amb zona.
    /// </summary>
    public static JsonObject Queue(QueuePlayer player)
    {
        var now = DateTimeOffset.Now;
        var upcoming = player.Upcoming;
        var forecast = player.Forecast();
        var starts = forecast.Starts.ToDictionary(s => s.Id, s => s.StartsIn);
        return new JsonObject
        {
            ["current"] = player.Current is { } current ? Item(current) : null,
            ["currentEndsAt"] = forecast.CurrentEndsIn is { } ends ? (now + ends).ToString("O") : null,
            ["upcoming"] = new JsonArray(upcoming.Select(i =>
            {
                var json = Item(i);
                json["plannedAt"] = starts.GetValueOrDefault(i.Id) is { } inTime ? (now + inTime).ToString("O") : null;
                return (JsonNode)json;
            }).ToArray()),
        };
    }

    public static JsonObject Track(Track track) => new()
    {
        ["id"] = track.Id,
        ["path"] = track.Path,
        ["artist"] = track.Artist,
        ["title"] = track.Title,
        ["type"] = Type(track.Type),
        ["durationMs"] = track.DurationMs,
        // Fase 3, pas 3a: per a la cerca de la biblioteca («ha sonat fa 3 dies»)
        ["album"] = track.Album,
        ["lastPlayed"] = track.LastPlayed?.ToString("O"),
        ["playCount"] = track.PlayCount,
        // Fase 3, pas 7a: per marcar les desactivades a la llista del gestor de pistes
        ["enabled"] = track.Enabled,
        ["cueSource"] = track.CueSource switch
        {
            CueSource.RadioDj => "radiodj",
            CueSource.Analyzer => "analyzer",
            CueSource.Manual => "manual",
            _ => null,
        },
        ["fileState"] = track.FileState switch
        {
            FileState.Missing => "missing",
            FileState.Retired => "retired",
            _ => "ok",
        },
    };

    /// <summary>
    /// La fitxa sencera d'una pista (gestor de pistes, fase 3, pas 7a; docs/protocol.md, 4.4): el que porta <see cref="Track(Track)"/>
    /// i, a més, tots els camps que es poden editar, els punts, la subcategoria, les etiquetes i els esdeveniments que la fan servir.
    /// </summary>
    public static JsonObject TrackDetail(Track track, IReadOnlyList<string> tags, SubcategoryRef? subcategory, IReadOnlyList<TrackUse> usedBy)
    {
        var json = Track(track);
        json["year"] = track.Year;
        json["variableDuration"] = track.VariableDuration;
        json["subcategoryId"] = track.SubcategoryId;
        json["subcategory"] = subcategory is null ? null : new JsonObject
        {
            ["id"] = subcategory.Id, ["name"] = subcategory.Name, ["categoryId"] = subcategory.CategoryId, ["categoryName"] = subcategory.CategoryName,
        };
        json["tags"] = new JsonArray(tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray());
        json["validFrom"] = track.ValidFrom?.ToString("yyyy-MM-dd");
        json["validUntil"] = track.ValidUntil?.ToString("yyyy-MM-dd");
        json["enabled"] = track.Enabled;
        json["notes"] = track.Notes;
        json["startMs"] = track.StartMs;
        json["mixMs"] = track.MixMs;
        json["endMs"] = track.EndMs;
        json["priority"] = track.Priority;
        json["fileSize"] = track.FileSize;
        json["fileModified"] = track.FileModified?.ToString("O");
        json["radioDjId"] = track.RadioDjId;
        json["usedBy"] = new JsonArray(usedBy.Select(u => (JsonNode)new JsonObject { ["id"] = u.EventId, ["name"] = u.EventName }).ToArray());
        return json;
    }

    /// <summary>La carpeta vigilada d'una subcategoria (fase 3, pas 7c; docs/protocol.md, 4.11).</summary>
    public static JsonObject FolderSettings(Data.FolderSettings s) => new()
    {
        ["subcategoryId"] = s.SubcategoryId,
        ["name"] = s.Name,
        ["folder"] = s.Folder,
        ["renewable"] = s.Renewable,
        ["type"] = Type(s.DefaultType),
        ["tags"] = new JsonArray(s.DefaultTags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
        ["variableDuration"] = s.DefaultVariableDuration,
    };

    /// <summary>Com estan les carpetes vigilades (a l'última revisió).</summary>
    public static JsonArray Folders(IReadOnlyList<LibraryTools.FolderWatchStatus> list) => new(list.Select(s => (JsonNode)new JsonObject
    {
        ["subcategoryId"] = s.SubcategoryId,
        ["name"] = s.Name,
        ["folder"] = s.Folder,
        ["renewable"] = s.Renewable,
        ["lastScan"] = s.LastScan?.ToString("O"),
        ["files"] = s.Files,
        ["tracks"] = s.Tracks,
        ["missing"] = s.Missing,
        ["problem"] = s.Problem,
    }).ToArray());

    /// <summary>Una línia del registre de les carpetes vigilades.</summary>
    public static JsonObject FolderLog(FolderLogEntry e) => new()
    {
        ["id"] = e.Id,
        ["time"] = e.Time.ToString("O"),
        ["subcategoryId"] = e.SubcategoryId,
        ["subcategory"] = e.Subcategory,
        ["path"] = e.Path,
        ["action"] = e.Action switch
        {
            FolderAction.Imported => "imported",
            FolderAction.Recovered => "recovered",
            FolderAction.Missing => "missing",
            FolderAction.Deleted => "deleted",
            FolderAction.Updated => "updated",
            FolderAction.Error => "error",
            _ => "folder",
        },
        ["detail"] = e.Detail,
    };

    /// <summary>Com va una importació de carpeta (fase 3, pas 7b; docs/protocol.md, 4.10).</summary>
    public static JsonObject Import(LibraryTools.ImportStatus s) => new()
    {
        ["folder"] = s.Folder,
        ["subcategory"] = s.SubcategoryName,
        ["state"] = s.State switch
        {
            LibraryTools.ImportState.Listing => "listing",
            LibraryTools.ImportState.Running => "running",
            LibraryTools.ImportState.Done => "done",
            LibraryTools.ImportState.Cancelled => "cancelled",
            _ => "failed",
        },
        ["total"] = s.Total,
        ["done"] = s.Done,
        ["added"] = s.Added,
        ["alreadyThere"] = s.AlreadyThere,
        ["reactivated"] = s.Reactivated,
        ["withoutSound"] = s.WithoutSound,
        // Com a molt els 200 primers (s'envia 2 vegades per segon); el total, a part
        ["unreadable"] = new JsonArray(s.Unreadable.Take(200).Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
        ["unreadableCount"] = s.Unreadable.Count,
        ["emptySubcategory"] = s.EmptySubcategory,
        ["removed"] = s.Removed,
        ["keptForEvents"] = s.KeptForEvents,
        ["warning"] = s.Warning,
        ["started"] = s.Started.ToString("O"),
        ["finished"] = s.Finished?.ToString("O"),
        ["error"] = s.Error,
    };

    public static JsonObject Output(OutputManager outputs) => new()
    {
        ["configured"] = new JsonObject { ["id"] = outputs.Configured.Id, ["name"] = outputs.Configured.Name },
        ["current"] = outputs.Current is { } device ? DeviceJson.Device(device) : null,
        ["emergency"] = outputs.IsEmergency,
        ["problem"] = outputs.Problem,
        // Fase 2, pas 4b: quant tarda el so a sortir per la targeta, i si és mesurat (si no, el suposat de 100 ms)
        ["latencyMs"] = Math.Round(outputs.Latency.TotalMilliseconds, 1),
        ["latencyMeasured"] = outputs.LatencyMeasured,
    };

    /// <summary>Els reproductors auxiliars que no estan lliures (docs/protocol.md, 4.8).</summary>
    public static JsonArray Aux(AuxPlayers aux) => new(aux.Status.Select(s => (JsonNode)new JsonObject
    {
        ["player"] = s.Player,
        ["item"] = Item(s.Item),
        ["started"] = s.Started,
        ["elapsedMs"] = (long)s.Elapsed.TotalMilliseconds,
        ["remainingMs"] = (long)s.Remaining.TotalMilliseconds,
        ["duckingDb"] = s.DuckingDb,
    }).ToArray());

    /// <summary>Pistes carregades a mà als reproductors auxiliars (fase 3, pas 4a): una entrada per reproductor, amb null si és buit.</summary>
    public static JsonArray AuxLoaded(AuxPlayers aux) => new(Enumerable.Range(1, AuxPlayers.Count).Select(player => (JsonNode)new JsonObject
    {
        ["player"] = player,
        ["item"] = aux.Loaded(player) is { } item ? Item(item) : null,
    }).ToArray());

    /// <summary>Estat de l'AutoDJ (docs/protocol.md, 4.5).</summary>
    public static JsonObject AutoDj(AutoDj autoDj) => new()
    {
        ["enabled"] = autoDj.Enabled,
        ["rotation"] = autoDj.Rotation is { } r ? new JsonObject { ["id"] = r.Id, ["name"] = r.Name } : null,
        ["nextStep"] = autoDj.NextStep,
        ["problem"] = autoDj.Problem,
        // Fase 3, pas 2b (D-073): fins quan està aturat, l'avís de quan s'ha tornat a activar sol i d'on ve la rotació
        ["disabledUntil"] = autoDj.DisabledUntil?.ToString("O"),
        ["notice"] = autoDj.Notice,
        ["rotationChangedBy"] = autoDj.RotationChangedBy,
        ["rotationChangedAt"] = autoDj.RotationChangedAt?.ToString("O"),
        // Fase 3, pas 5: la categoria de sweepers del trepitjador, i d'on ve (null si no n'hi ha cap)
        ["sweepers"] = autoDj.SweepersSubcategoryId is { } sweepersId
            ? new JsonObject
            {
                ["subcategoryId"] = sweepersId,
                ["name"] = autoDj.SweepersName,
                ["changedBy"] = autoDj.SweepersChangedBy,
                ["changedAt"] = autoDj.SweepersChangedAt?.ToString("O"),
            }
            : null,
    };

    /// <summary>Una rotació amb els seus passos (docs/protocol.md, 4.5).</summary>
    public static JsonObject Rotation(Rotation rotation) => new()
    {
        ["id"] = rotation.Id,
        ["name"] = rotation.Name,
        ["steps"] = new JsonArray(rotation.Steps.Select(s => (JsonNode)new JsonObject
        {
            ["category"] = s.CategoryName,
            ["subcategory"] = s.SubcategoryName,
            ["tag"] = s.TagName,
            ["selection"] = s.Selection switch
            {
                RotationSelection.Random => "random",
                RotationSelection.LeastPlayed => "leastPlayed",
                _ => "leastRecent",
            },
            ["repeatRule"] = s.RepeatRule,
            ["sweeper"] = s.Sweeper,
        }).ToArray()),
    };

    /// <summary>Una entrada de l'historial (docs/protocol.md, 4.6).</summary>
    public static JsonObject History(HistoryEntry entry) => new()
    {
        ["id"] = entry.Id,
        ["trackId"] = entry.TrackId,
        ["startedAt"] = entry.Started.ToUniversalTime().ToString("O"),
        ["playedMs"] = entry.PlayedMs,
        ["complete"] = entry.Complete,
        ["origin"] = Origin(entry.Origin),
        ["artist"] = entry.Artist,
        ["title"] = entry.Title,
        ["path"] = entry.Path,
    };

    /// <summary>Exportació del títol per a BUTT (docs/protocol.md, 4.6).</summary>
    public static JsonObject NowPlaying(NowPlaying nowPlaying) => new()
    {
        ["enabled"] = nowPlaying.Settings.Enabled,
        ["path"] = nowPlaying.FilePath,
        ["customPath"] = !string.IsNullOrWhiteSpace(nowPlaying.Settings.Path),
        ["template"] = nowPlaying.Settings.Template,
        ["types"] = new JsonArray(nowPlaying.Settings.Types.Select(t => (JsonNode)Type(t)).ToArray()),
        ["text"] = nowPlaying.Text,
        ["problem"] = nowPlaying.Problem,
    };

    /// <summary>
    /// Esdeveniment <c>meters</c> (docs/protocol.md, 5.1): el pic de la sortida (dBFS, <c>[esquerre, dret]</c>, −90 = silenci)
    /// i la posició de la pista que sona. <c>reductionDb</c>: quant ha abaixat el processador de sortida (fase 3, pas 6; ≥ 0).
    /// </summary>
    public static JsonObject Meters((float Left, float Right) peak, QueuePosition? position, float reductionDb = 0)
    {
        var ev = Messages.Event("meters");
        ev["output"] = new JsonArray(OnaBages.Audio.PeakMeter.ToDb(peak.Left), OnaBages.Audio.PeakMeter.ToDb(peak.Right));
        ev["reductionDb"] = Math.Round(reductionDb, 1);
        ev["position"] = position is null ? null : new JsonObject
        {
            ["itemId"] = position.ItemId.ToString(),
            ["elapsedMs"] = (long)position.Elapsed.TotalMilliseconds,
            ["remainingMs"] = (long)position.Remaining.TotalMilliseconds,
            ["mixInMs"] = (long)position.UntilMix.TotalMilliseconds,
            ["durationMs"] = (long)position.Duration.TotalMilliseconds,
        };
        return ev;
    }

    /// <summary>
    /// El processador de sortida (fase 3, pas 6; docs/protocol.md): el preajust, el volum, els preajusts que es poden triar
    /// i els marges del volum.
    /// </summary>
    public static JsonObject Processor(OutputProcessor processor) => new()
    {
        ["preset"] = processor.Preset.Id,
        ["presetName"] = processor.Preset.Name,
        ["volumeDb"] = processor.VolumeDb,
        ["ceilingDb"] = OutputProcessor.CeilingDb,
        ["minVolumeDb"] = OutputProcessor.MinVolumeDb,
        ["maxVolumeDb"] = OutputProcessor.MaxVolumeDb,
        ["presets"] = new JsonArray(OutputProcessor.Presets.Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["name"] = p.Name }).ToArray()),
    };

    /// <summary>Tipus de pista: <c>music</c>, <c>sweeper</c>, <c>stationId</c>, <c>promo</c>, <c>commercial</c>, <c>program</c>, <c>news</c>, <c>timeAnnouncement</c>, <c>timeSignal</c>, <c>stream</c>, <c>other</c>.</summary>
    public static string Type(TrackType type)
    {
        string name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>D'on ve un element de la cua (D-034): <c>autodj</c>, <c>manual</c> o <c>event</c>.</summary>
    public static string Origin(QueueOrigin origin) => origin switch
    {
        QueueOrigin.AutoDj => "autodj",
        QueueOrigin.Event => "event",
        _ => "manual",
    };
}
