using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Protocol;

/// <summary>
/// Ordres de la pestanya Pistes (D-111): la cerca amb tots els filtres, seleccionar-les totes, canvis en bloc, esborrar
/// categories i subcategories buides, i el gestor d'etiquetes (docs/protocol.md).
/// </summary>
public sealed partial class CommandRouter
{
    /// <summary>Com a molt, quantes pistes es poden seleccionar d'una vegada (totes les d'un filtre).</summary>
    public const int MaxSelection = 20_000;

    /// <summary>Els filtres i l'ordre d'una cerca (library.search i library.ids). Les dates són dies locals (AAAA-MM-DD).</summary>
    private TrackQuery ReadTrackQuery(JsonObject msg)
    {
        long? Id(string field) => msg[field] is JsonValue v && v.TryGetValue(out long id) ? id : null;
        bool? Bool(string field) => msg[field] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
        DateOnly? Day(string field)
        {
            if (ReadString(msg[field]) is not { } text) return null;
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d) ? d : throw new BadParamsException($"El camp '{field}' ha de ser una data AAAA-MM-DD.");
        }
        // Un dia local → l'instant en què comença (l'historial és en UTC)
        DateTimeOffset? Start(DateOnly? day) => day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), Zone.GetUtcOffset(d.ToDateTime(TimeOnly.MinValue))) : null;

        var playedTo = Day("playedTo");
        return new TrackQuery
        {
            Text = ReadString(msg["text"]) ?? "",
            CategoryId = Id("categoryId"),
            SubcategoryId = Id("subcategoryId"),
            TagId = Id("tagId"),
            Type = ReadString(msg["type"]) is { } type
                ? Enum.GetValues<TrackType>().FirstOrDefault(t => QueueJson.Type(t) == type) is var found && QueueJson.Type(found) == type
                    ? found : throw new BadParamsException($"El camp 'type' no és un tipus de pista: «{type}».")
                : null,
            State = ReadString(msg["state"]) switch
            {
                null or "all" => TrackStateFilter.All,
                "missing" => TrackStateFilter.Missing,
                "retired" => TrackStateFilter.Retired,
                "disabled" => TrackStateFilter.Disabled,
                "unclassified" => TrackStateFilter.Unclassified,
                var other => throw new BadParamsException($"El camp 'state' no és vàlid: «{other}» (all, missing, retired, disabled o unclassified)."),
            },
            InEvents = Bool("inEvents"),
            Validity = ReadString(msg["validity"]) switch
            {
                null or "all" => TrackValidity.All,
                "valid" => TrackValidity.Valid,
                "expired" => TrackValidity.Expired,
                "notYet" => TrackValidity.NotYet,
                "overlaps" => TrackValidity.Overlaps,
                var other => throw new BadParamsException($"El camp 'validity' no és vàlid: «{other}» (all, valid, expired, notYet o overlaps)."),
            },
            ValidFrom = Day("validFrom"),
            ValidTo = Day("validTo"),
            Today = DateOnly.FromDateTime(DateTime.Today),
            NeverPlayed = Bool("neverPlayed") ?? false,
            PlayedFrom = Start(Day("playedFrom")),
            PlayedBefore = Start(playedTo?.AddDays(1)), // «fins al dia X» inclou tot el dia X
            Sort = ReadString(msg["sort"]) switch
            {
                null or "artist" => TrackSort.Artist,
                "title" => TrackSort.Title,
                "type" => TrackSort.Type,
                "duration" => TrackSort.Duration,
                "plays" => TrackSort.Plays,
                "lastPlayed" => TrackSort.LastPlayed,
                "validUntil" => TrackSort.ValidUntil,
                var other => throw new BadParamsException($"El camp 'sort' no és vàlid: «{other}»."),
            },
            Descending = Bool("desc") ?? false,
            Offset = Math.Max(0, ReadInt(msg["offset"]) ?? 0),
        };
    }

    /// <summary>Cerca a la biblioteca amb tots els filtres (docs/protocol.md): una pàgina de pistes, si n'hi ha més i el total.</summary>
    private JsonObject LibrarySearch(JsonObject msg)
    {
        var library = LibraryOrError();
        int limit = Math.Clamp(ReadInt(msg["limit"]) ?? 50, 1, 200);
        var query = ReadTrackQuery(msg) with { Limit = limit + 1 };
        var tracks = library.Search(query);
        return new JsonObject
        {
            ["tracks"] = new JsonArray(tracks.Take(limit).Select(t => (JsonNode)QueueJson.Track(t)).ToArray()),
            ["more"] = tracks.Count > limit,
            ["total"] = library.Count(query),
        };
    }

    /// <summary>Els ids de totes les pistes d'una cerca (per seleccionar-les totes), amb els mateixos filtres que library.search.</summary>
    private JsonObject LibraryIds(JsonObject msg)
    {
        var library = LibraryOrError();
        var query = ReadTrackQuery(msg);
        var ids = library.SearchIds(query, MaxSelection);
        return new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(id => (JsonNode)id).ToArray()),
            ["total"] = library.Count(query),
        };
    }

    /// <summary>
    /// Canvis en bloc (D-111): <c>trackIds</c> i <c>action</c>: <c>subcategory</c> (amb <c>subcategoryId</c>, null = cap),
    /// <c>enable</c>, <c>disable</c>, <c>addTag</c> (amb <c>tag</c>, el nom), <c>removeTag</c> (amb <c>tagId</c>) o
    /// <c>delete</c>. En esborrar, les que fa servir algun esdeveniment no s'esborren (es diuen a <c>kept</c>). Amb
    /// <c>dryRun</c>, no canvia res: per a <c>delete</c>, diu quantes es quedarien i quantes han sonat els últims 2 anys.
    /// </summary>
    private JsonObject LibraryBulk(JsonObject msg)
    {
        var library = LibraryOrError();
        if (msg["trackIds"] is not JsonArray list || list.Count == 0) throw new BadParamsException("Falta 'trackIds' (la llista de pistes).");
        var ids = list.Select(n => n is JsonValue v && v.TryGetValue(out long id) ? id : throw new BadParamsException("'trackIds' ha de ser una llista de números."))
            .Distinct().ToList();
        if (ids.Count > MaxSelection) throw new BadParamsException($"Com a molt {MaxSelection:N0} pistes d'una vegada.");
        string action = ReadString(msg["action"]) ?? throw new BadParamsException("Falta 'action'.");
        bool dryRun = msg["dryRun"] is JsonValue dv && dv.TryGetValue(out bool d) && d;
        var result = new JsonObject { ["action"] = action, ["tracks"] = ids.Count };

        if (action == "delete")
        {
            // Pels certificats (D-011): les que han sonat els últims 2 anys perden l'enllaç amb l'historial
            var since = DateTimeOffset.UtcNow.AddYears(-2);
            var kept = new JsonArray();
            int recent = 0, deleted = 0;
            foreach (long id in ids)
            {
                if (library.GetTrack(id) is not { } track) continue;
                // Les que fan servir esdeveniments o campanyes (falques) es deixen
                var uses = library.EventsUsingTrack(id).Select(u => u.EventName)
                    .Concat(library.CampaignsUsingTrack(id).Select(c => $"campanya {c}")).ToList();
                if (uses.Count > 0)
                {
                    kept.Add(new JsonObject { ["id"] = id, ["title"] = track.Title, ["events"] = string.Join(", ", uses) });
                    continue;
                }
                if (library.PlaysSince(id, since) > 0) recent++;
                if (dryRun) continue;
                library.DeleteTrack(id);
                deleted++;
            }
            result["kept"] = kept;
            result["playedRecently"] = recent;
            result["deleted"] = deleted;
            if (!dryRun && deleted > 0) LibraryChanged?.Invoke(null);
            return result;
        }

        if (dryRun) return result;
        int changed = action switch
        {
            "subcategory" when msg.ContainsKey("subcategoryId") =>
                library.SetSubcategory(ids, msg["subcategoryId"] is null ? null : RequiredId(msg, "subcategoryId")),
            "subcategory" => throw new BadParamsException("Falta 'subcategoryId' (un número, o null perquè no en tinguin cap)."),
            "enable" => library.SetEnabled(ids, true),
            "disable" => library.SetEnabled(ids, false),
            "addTag" => (ReadString(msg["tag"])?.Trim() is { Length: > 0 and <= 100 } tag)
                ? library.AddTag(ids, tag) : throw new BadParamsException("Falta 'tag' (el nom de l'etiqueta, com a molt 100 caràcters)."),
            "removeTag" => library.RemoveTag(ids, RequiredId(msg, "tagId")),
            _ => throw new BadParamsException($"L'acció «{action}» no existeix (subcategory, enable, disable, addTag, removeTag o delete)."),
        };
        result["changed"] = changed;
        if (changed > 0) LibraryChanged?.Invoke(null);
        return result;
    }

    // --- Esborrar categories i subcategories buides ---

    private JsonObject CategoryDelete(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "categoryId");
        library.DeleteCategory(id);
        LibraryChanged?.Invoke(null);
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>
    /// Esborra una subcategoria buida. A més del que mira la biblioteca (pistes, rotacions, bosses, carpeta vigilada), no
    /// ha de ser la categoria de sweepers, ni la fan servir accions SWEEPERS o BOSSA dels esdeveniments, ni la reserva.
    /// </summary>
    private JsonObject SubcategoryDelete(JsonObject msg)
    {
        var library = LibraryOrError();
        long id = RequiredId(msg, "subcategoryId");
        if (library.GetSubcategory(id) is null) throw new BadParamsException($"No existeix la subcategoria {id}.");
        var reasons = library.SubcategoryBlockers(id).ToList();
        if (_autoDj?.SweepersSubcategoryId == id) reasons.Add("és la categoria de sweepers de l'AutoDJ");
        if (ReadReserve(_library).ExcludedSubcategoryIds.Contains(id)) reasons.Add("és a les subcategories excloses de la reserva");
        var events = _events?.List()
            .Where(e => e.Actions.Any(a => a.Type is EventActionType.Sweepers or EventActionType.Bag
                && JsonNode.Parse(a.Params)?["subcategoriaId"] is JsonValue v && v.TryGetValue(out long s) && s == id))
            .Select(e => $"«{e.Name}»").ToList() ?? [];
        if (events.Count > 0) reasons.Add($"la fan servir els esdeveniments {string.Join(", ", events)}");
        if (reasons.Count > 0) throw new BadParamsException("No es pot esborrar la subcategoria: " + string.Join("; ", reasons) + ".");
        library.DeleteSubcategory(id);
        LibraryChanged?.Invoke(null);
        return new JsonObject { ["deleted"] = id };
    }

    // --- Gestor d'etiquetes ---

    private JsonObject TagsList() => new()
    {
        ["tags"] = new JsonArray(LibraryOrError().Tags().Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id,
            ["name"] = t.Name,
            ["tracks"] = t.Tracks,
            ["rotations"] = new JsonArray(t.Rotations.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        }).ToArray()),
    };

    private JsonObject TagsRename(JsonObject msg)
    {
        LibraryOrError().RenameTag(RequiredId(msg, "tagId"), RequiredName(msg));
        LibraryChanged?.Invoke(null);
        return TagsList();
    }

    private JsonObject TagsMerge(JsonObject msg)
    {
        LibraryOrError().MergeTags(RequiredId(msg, "fromId"), RequiredId(msg, "intoId"));
        LibraryChanged?.Invoke(null);
        return TagsList();
    }

    private JsonObject TagsDelete(JsonObject msg)
    {
        LibraryOrError().DeleteTag(RequiredId(msg, "tagId"));
        LibraryChanged?.Invoke(null);
        return TagsList();
    }
}
