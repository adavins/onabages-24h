using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

// Paquet de campanya (fase 4, pas 7a; D-150): una campanya preparada en una emissora es passa a una altra amb un fitxer
// JSON que es deixa a la carpeta compartida (Google Drive) al costat dels fitxers de les falques. Porta la campanya, les
// falques (pel nom del fitxer) i el calendari (les hores i els dies on sona). En importar, les falques es busquen al
// costat del JSON; la campanya es crea en esborrany i el calendari s'aplica com un calendari desat (D-145).
public sealed partial class CommandRouter
{
    /// <summary>Què és el fitxer i quina versió del format.</summary>
    public const string PackageFormat = "onabages24-campanya";
    public const int PackageVersion = 1;

    /// <summary>
    /// Exporta el paquet d'una campanya (<c>campaignId</c>) a <c>path</c> (ruta completa d'un fitxer .json, en una carpeta
    /// que existeixi). Diu quantes falques i caselles porta, i quins fitxers de falques no són a la mateixa carpeta (cal
    /// copiar-los-hi perquè l'altra emissora els trobi).
    /// </summary>
    private JsonObject CampaignsExportPackage(JsonObject msg)
    {
        var library = LibraryOrError();
        var campaign = CampaignsOrError().Get(RequiredId(msg, "campaignId")) ?? throw new BadParamsException("Aquesta campanya ja no existeix.");
        string path = PackagePath(msg);
        string folder = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(folder)) throw new BadParamsException($"No es troba la carpeta: {folder}");

        var spots = new JsonArray();
        var notInFolder = new JsonArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spot in campaign.Spots)
        {
            var track = library.GetTrack(spot.TrackId) ?? throw new BadParamsException($"No existeix la pista {spot.TrackId}.");
            if (track.Type == TrackType.Stream || track.Path.Contains("://"))
                throw new BadParamsException($"«{ValidityWarnings.TrackName(track)}» és un stream: no es pot posar en un paquet.");
            string file = Path.GetFileName(track.Path);
            // A l'altra emissora es busquen pel nom: dos fitxers amb el mateix nom serien el mateix
            if (!names.Add(file)) throw new BadParamsException($"Hi ha dues falques amb el mateix nom de fitxer ({file}): canvia'n el nom d'una.");
            if (!File.Exists(Path.Combine(folder, file))) notInFolder.Add((JsonNode)file);
            spots.Add((JsonNode)new JsonObject
            {
                ["file"] = file,
                ["artist"] = track.Artist,
                ["title"] = track.Title,
                ["album"] = track.Album,
                ["year"] = track.Year,
                ["type"] = QueueJson.Type(track.Type),
                ["durationMs"] = track.DurationMs,
                ["startMs"] = track.StartMs,
                ["mixMs"] = track.MixMs,
                ["endMs"] = track.EndMs,
                ["validFrom"] = track.ValidFrom is { } vf ? DbTime.ToText(vf) : null,
                ["validUntil"] = track.ValidUntil is { } vu ? DbTime.ToText(vu) : null,
                ["startDate"] = spot.StartDate is { } from ? DbTime.ToText(from) : null,
                ["endDate"] = spot.EndDate is { } to ? DbTime.ToText(to) : null,
                ["daysMask"] = spot.DaysMask,
                ["weight"] = spot.Weight,
            });
        }

        var slots = MarkedSlots(CalendarSubject.ForCampaign(campaign.Id));
        var package = new JsonObject
        {
            ["format"] = PackageFormat,
            ["version"] = PackageVersion,
            ["exported"] = CampaignClock().ToString("O"),
            ["station"] = ReadString(StationGet()["station"]?["name"]),
            ["campaign"] = new JsonObject
            {
                ["name"] = campaign.Name,
                ["advertiser"] = campaign.Advertiser,
                ["startDate"] = DbTime.ToText(campaign.StartDate),
                ["endDate"] = DbTime.ToText(campaign.EndDate),
                ["selection"] = Campaigns.SelectionText(campaign.Selection),
                ["contractedTotal"] = campaign.ContractedTotal,
                ["contractedPerDay"] = campaign.ContractedPerDay,
                ["notes"] = campaign.Notes,
            },
            ["spots"] = spots,
            ["calendar"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject { ["time"] = s.Time.ToString("HH:mm:ss"), ["daysMask"] = s.DaysMask }).ToArray()),
        };
        try
        {
            File.WriteAllText(path, package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException($"No s'ha pogut desar el paquet: {ex.Message}");
        }
        return new JsonObject
        {
            ["path"] = path,
            ["spots"] = spots.Count,
            ["cells"] = slots.Sum(s => System.Numerics.BitOperations.PopCount((uint)s.DaysMask)),
            ["notInFolder"] = notInFolder,
        };
    }

    /// <summary>
    /// Importa un paquet de campanya (<c>path</c>). Amb <c>dryRun</c>, només diu què faria: la campanya, cada falca (si ja és
    /// a la biblioteca, si és nova o si el fitxer no és al costat del paquet o no es pot llegir) i el calendari. Sense, crea
    /// les pistes noves (a <c>subcategoryId</c>, opcional), la campanya en esborrany amb les falques trobades i marca el
    /// calendari (com «Afegeix» d'un calendari desat). Les falques que no es troben no hi entren.
    /// </summary>
    private JsonObject CampaignsImportPackage(JsonObject msg)
    {
        var library = LibraryOrError();
        var campaigns = CampaignsOrError();
        var events = EventsOrError();
        string path = PackagePath(msg);
        bool dryRun = msg["dryRun"] is JsonValue dv && dv.TryGetValue(out bool d) && d;
        long? subcategoryId = msg["subcategoryId"] is null ? null : RequiredId(msg, "subcategoryId");
        if (subcategoryId is { } sub && library.GetSubcategory(sub) is null) throw new BadParamsException($"No existeix la subcategoria {sub}.");

        JsonObject package;
        try
        {
            package = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new BadParamsException("El fitxer no és un paquet de campanya.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException($"No s'ha pogut llegir el paquet: {ex.Message}");
        }
        catch (JsonException)
        {
            throw new BadParamsException("El fitxer no és un paquet de campanya (no és JSON).");
        }
        if (ReadString(package["format"]) != PackageFormat) throw new BadParamsException("El fitxer no és un paquet de campanya d'Ona Bages 24h.");
        if (ReadInt(package["version"]) is not { } version || version > PackageVersion)
            throw new BadParamsException("El paquet és d'una versió més nova de l'aplicació: actualitza-la per importar-lo.");
        if (package["campaign"] is not JsonObject info) throw new BadParamsException("Al paquet hi falta la campanya.");

        // Les falques: al costat del paquet, pel nom del fitxer
        string folder = Path.GetDirectoryName(path)!;
        var found = new List<(JsonObject Spot, string FullPath, Track? Existing)>();
        var report = new JsonArray();
        foreach (var node in package["spots"] as JsonArray ?? [])
        {
            if (node is not JsonObject spot) continue;
            string file = Path.GetFileName(ReadString(spot["file"]) ?? "");
            var line = new JsonObject { ["file"] = file, ["title"] = ReadString(spot["title"]) ?? "", ["artist"] = ReadString(spot["artist"]) ?? "" };
            report.Add((JsonNode)line);
            string full = Path.Combine(folder, file);
            if (file.Length == 0 || !File.Exists(full))
            {
                line["status"] = "missing";
                line["reason"] = "El fitxer no és a la carpeta del paquet.";
                continue;
            }
            // Llegir-lo sencer: amb el Google Drive en «transmissió», així es baixa ara i no a l'hora de sonar
            if (ReadWholeFile(full) is { } problem)
            {
                line["status"] = "unreadable";
                line["reason"] = $"No es pot llegir el fitxer: {problem}";
                continue;
            }
            var existing = library.FindByPath(full);
            line["status"] = existing is null ? "new" : "existing";
            if (existing is not null) line["trackId"] = existing.Id;
            found.Add((spot, full, existing));
        }
        var slots = new List<SavedSlot>();
        foreach (var node in package["calendar"] as JsonArray ?? [])
        {
            if (node is JsonObject o && TimeOnly.TryParseExact(ReadString(o["time"]) ?? "", "HH:mm:ss", out var time)
                && ReadInt(o["daysMask"]) is { } mask and >= 1 and <= Weekdays.All)
                slots.Add(new SavedSlot(time, mask));
        }

        var result = new JsonObject
        {
            ["campaign"] = new JsonObject
            {
                ["name"] = ReadString(info["name"]) ?? "",
                ["advertiser"] = ReadString(info["advertiser"]),
                ["startDate"] = ReadString(info["startDate"]),
                ["endDate"] = ReadString(info["endDate"]),
            },
            ["station"] = ReadString(package["station"]),
            ["exported"] = ReadString(package["exported"]),
            ["spots"] = report,
            ["calendar"] = new JsonObject
            {
                ["cells"] = slots.Sum(s => System.Numerics.BitOperations.PopCount((uint)s.DaysMask)),
                ["slots"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject { ["time"] = s.Time.ToString("HH:mm:ss"), ["daysMask"] = s.DaysMask }).ToArray()),
            },
        };
        if (dryRun) return result;

        // Les pistes noves, apuntant al fitxer de la carpeta del paquet (no es copia, D-150)
        var spotsMsg = new JsonArray();
        bool libraryChanged = false;
        foreach (var (spot, full, existing) in found)
        {
            long trackId = existing?.Id ?? AddPackageTrack(library, spot, full, subcategoryId);
            libraryChanged |= existing is null;
            spotsMsg.Add((JsonNode)new JsonObject
            {
                ["trackId"] = trackId,
                ["startDate"] = spot["startDate"]?.DeepClone(),
                ["endDate"] = spot["endDate"]?.DeepClone(),
                ["daysMask"] = spot["daysMask"]?.DeepClone(),
                ["weight"] = spot["weight"]?.DeepClone(),
            });
        }
        var campaignMsg = new JsonObject
        {
            ["name"] = info["name"]?.DeepClone(),
            ["advertiser"] = info["advertiser"]?.DeepClone(),
            ["startDate"] = info["startDate"]?.DeepClone(),
            ["endDate"] = info["endDate"]?.DeepClone(),
            ["selection"] = info["selection"]?.DeepClone(),
            ["contractedTotal"] = info["contractedTotal"]?.DeepClone(),
            ["contractedPerDay"] = info["contractedPerDay"]?.DeepClone(),
            ["notes"] = info["notes"]?.DeepClone(),
            ["spots"] = spotsMsg,
        };
        // Sempre en esborrany: la persona de l'altra emissora la revisa i l'activa (decisió del desenvolupador)
        var campaign = ReadCampaign(campaignMsg) with { Id = 0, State = CampaignState.Draft };
        try
        {
            Campaigns.Validate(campaign);
        }
        catch (DatabaseException ex)
        {
            if (libraryChanged) LibraryChanged?.Invoke(null);
            throw new BadParamsException($"La campanya del paquet no és vàlida: {ex.Message}");
        }
        long campaignId = campaigns.Save(campaign, DateTimeOffset.UtcNow);
        if (libraryChanged) LibraryChanged?.Invoke(null);
        CampaignsChanged?.Invoke();

        // El calendari, com «Afegeix» d'un calendari desat: on no hi ha esdeveniment, el bloc de falques (D-105)
        result["applied"] = ApplySlots(library, events, CalendarSubject.ForCampaign(campaignId), slots, replace: false);
        result["campaignId"] = campaignId;
        return result;
    }

    /// <summary>La ruta completa del paquet (<c>path</c>, un fitxer .json).</summary>
    private static string PackagePath(JsonObject msg)
    {
        string path = (ReadString(msg["path"]) ?? "").Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) throw new BadParamsException("Falta 'path' (la ruta completa del fitxer del paquet).");
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new BadParamsException("El paquet ha de ser un fitxer .json.");
        return Path.GetFullPath(path);
    }

    /// <summary>Llegeix un fitxer sencer sense bloquejar-lo. Null si s'ha pogut; si no, el motiu.</summary>
    private static string? ReadWholeFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64 * 1024];
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Una pista nova per a una falca del paquet, amb les dades i els punts de l'emissora que l'ha exportat.</summary>
    private static long AddPackageTrack(Library library, JsonObject spot, string fullPath, long? subcategoryId)
    {
        var info = new FileInfo(fullPath);
        DateOnly? Date(string field) => DateOnly.TryParseExact(ReadString(spot[field]) ?? "", "yyyy-MM-dd", out var d) ? d : null;
        long? Ms(string field) => spot[field] is JsonValue v && v.TryGetValue(out long ms) ? ms : null;
        var type = TrackType.Commercial;
        try { if (ReadString(spot["type"]) is { } name) type = NowPlayingSettings.ParseType(name); }
        catch (FormatException) { /* un tipus que no coneixem: es queda com a falca */ }
        long? mix = Ms("mixMs");
        return library.AddTrack(new Track
        {
            Path = fullPath,
            Type = type,
            SubcategoryId = subcategoryId,
            Artist = ReadString(spot["artist"]) ?? "",
            Title = ReadString(spot["title"]) ?? Path.GetFileNameWithoutExtension(fullPath),
            Album = ReadString(spot["album"]) ?? "",
            Year = ReadString(spot["year"]) ?? "",
            DurationMs = Ms("durationMs"),
            StartMs = Ms("startMs"),
            MixMs = mix,
            EndMs = Ms("endMs"),
            CueSource = mix is null ? null : CueSource.Manual,
            ValidFrom = Date("validFrom"),
            ValidUntil = Date("validUntil"),
            FileSize = info.Length,
            FileModified = info.LastWriteTimeUtc,
            Notes = "Importada d'un paquet de campanya.",
        });
    }
}
