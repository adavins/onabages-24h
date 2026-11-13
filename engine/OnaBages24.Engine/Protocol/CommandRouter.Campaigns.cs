using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Protocol;

// Campanyes de falques (fase 4, pas 3a; D-011, D-029, D-122; docs/planificador.md, secció 10).
public sealed partial class CommandRouter
{
    /// <summary>Quan es desa o s'esborra una campanya: les interfícies tornen a demanar el que mostren (<c>campaigns.changed</c>).</summary>
    public event Action? CampaignsChanged;

    private Campaigns CampaignsOrError() => _campaigns ?? throw new BadParamsException("El motor no té campanyes.");

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Totes les campanyes, amb les falques (sense la fitxa de cada pista: per a la llista).</summary>
    private JsonObject CampaignsList()
    {
        var campaigns = CampaignsOrError();
        var today = Today();
        return new JsonObject
        {
            ["campaigns"] = new JsonArray(campaigns.List().Select(c => (JsonNode)CampaignJson(c, today, null)).ToArray()),
        };
    }

    /// <summary>Una campanya sencera: <c>campaignId</c>. Cada falca porta la pista (artista, títol, tipus, dates, fitxer).</summary>
    private JsonObject CampaignsGet(JsonObject msg)
    {
        var campaigns = CampaignsOrError();
        long id = RequiredId(msg, "campaignId");
        var campaign = campaigns.Get(id) ?? throw new BadParamsException($"No existeix la campanya {id}.");
        return new JsonObject { ["campaign"] = CampaignDetail(campaigns, campaign) };
    }

    /// <summary>
    /// Crea (sense <c>campaignId</c>) o substitueix (amb <c>campaignId</c>) una campanya sencera, amb les falques en ordre.
    /// Camps: vegeu docs/protocol.md (4.12).
    /// </summary>
    private JsonObject CampaignsSave(JsonObject msg)
    {
        var campaigns = CampaignsOrError();
        long id = msg["campaignId"] is null ? 0 : RequiredId(msg, "campaignId");
        if (id != 0 && campaigns.Get(id) is null) throw new BadParamsException($"No existeix la campanya {id}.");

        var campaign = ReadCampaign(msg) with { Id = id };
        try
        {
            Campaigns.Validate(campaign);
        }
        catch (DatabaseException ex) { throw new BadParamsException(ex.Message); }
        var library = LibraryOrError();
        foreach (var spot in campaign.Spots)
            if (library.GetTrack(spot.TrackId) is null) throw new BadParamsException($"No existeix la pista {spot.TrackId}.");

        id = campaigns.Save(campaign, DateTimeOffset.UtcNow);
        CampaignsChanged?.Invoke();
        return new JsonObject { ["campaign"] = CampaignDetail(campaigns, campaigns.Get(id)!) };
    }

    /// <summary>Esborra una campanya: <c>campaignId</c>. No es pot si un esdeveniment la fa servir o si ja ha sonat.</summary>
    private JsonObject CampaignsDelete(JsonObject msg)
    {
        var campaigns = CampaignsOrError();
        long id = RequiredId(msg, "campaignId");
        if (campaigns.Get(id) is null) throw new BadParamsException($"No existeix la campanya {id}.");
        try
        {
            campaigns.Delete(id);
        }
        catch (DatabaseException ex) { throw new BadParamsException(ex.Message); }
        CampaignsChanged?.Invoke();
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>Llegeix els camps d'una campanya de l'ordre. Els que falten prenen el valor per defecte.</summary>
    private static Campaign ReadCampaign(JsonObject msg)
    {
        string? Text(string field)
        {
            if (msg[field] is null) return null;
            return ReadString(msg[field]) ?? throw new BadParamsException($"El camp '{field}' ha de ser un text o null.");
        }
        int? Count(JsonObject o, string field, string what)
        {
            if (o[field] is null) return null;
            return o[field] is JsonValue v && v.TryGetValue(out int n) ? n : throw new BadParamsException($"{what} ha de ser un número o null.");
        }

        var spots = new List<CampaignSpot>();
        if (msg["spots"] is not null)
        {
            if (msg["spots"] is not JsonArray array) throw new BadParamsException("El camp 'spots' ha de ser una llista.");
            foreach (var node in array)
            {
                if (node is not JsonObject s) throw new BadParamsException("Cada falca de 'spots' ha de ser un objecte.");
                spots.Add(new CampaignSpot
                {
                    TrackId = RequiredId(s, "trackId"),
                    StartDate = OptionalDate(s, "startDate"),
                    EndDate = OptionalDate(s, "endDate"),
                    DaysMask = Count(s, "daysMask", "Els dies d'una falca ('daysMask')") ?? Weekdays.All,
                    Weight = Count(s, "weight", "El pes d'una falca ('weight')") ?? 1,
                });
            }
        }

        return new Campaign
        {
            Name = Text("name") ?? "",
            Advertiser = Text("advertiser"),
            StartDate = OptionalDate(msg, "startDate") ?? throw new BadParamsException("Falta 'startDate' (la data d'inici, 'AAAA-MM-DD')."),
            EndDate = OptionalDate(msg, "endDate") ?? throw new BadParamsException("Falta 'endDate' (la data de fi, 'AAAA-MM-DD')."),
            Selection = msg["selection"] is null ? CampaignSelection.Alternate
                : Campaigns.ParseSelection(ReadString(msg["selection"])) ?? throw new BadParamsException("El camp 'selection' ha de ser ALTERNA, PES o SEQUENCIA."),
            State = msg["state"] is null ? CampaignState.Draft
                : Campaigns.ParseState(ReadString(msg["state"])) ?? throw new BadParamsException("El camp 'state' ha de ser ESBORRANY, ACTIVA o ACABADA."),
            ContractedTotal = Count(msg, "contractedTotal", "Els passis contractats ('contractedTotal')"),
            ContractedPerDay = Count(msg, "contractedPerDay", "Els passis contractats per dia ('contractedPerDay')"),
            Notes = Text("notes"),
            Spots = spots,
        };
    }

    private static DateOnly? OptionalDate(JsonObject o, string field)
    {
        if (o[field] is null) return null;
        return DateOnly.TryParseExact(ReadString(o[field]), "yyyy-MM-dd", out var d)
            ? d : throw new BadParamsException($"El camp '{field}' ha de ser una data 'AAAA-MM-DD' o null.");
    }

    /// <summary>La campanya sencera: com a la llista, més la pista de cada falca, els esdeveniments que la fan sonar i quantes vegades ha sonat.</summary>
    private JsonObject CampaignDetail(Campaigns campaigns, Campaign campaign)
    {
        var json = CampaignJson(campaign, Today(), _library);
        json["events"] = new JsonArray(campaigns.EventsUsingCampaign(campaign.Id)
            .Select(e => (JsonNode)new JsonObject { ["id"] = e.EventId, ["name"] = e.EventName }).ToArray());
        json["playCount"] = campaigns.PlayCount(campaign.Id);
        return json;
    }

    /// <summary>Una campanya en JSON (docs/protocol.md, 4.12). Amb <paramref name="library"/>, cada falca porta també la pista.</summary>
    private static JsonObject CampaignJson(Campaign c, DateOnly today, Library? library) => new()
    {
        ["id"] = c.Id,
        ["name"] = c.Name,
        ["advertiser"] = c.Advertiser,
        ["startDate"] = DbTime.ToText(c.StartDate),
        ["endDate"] = DbTime.ToText(c.EndDate),
        ["selection"] = Campaigns.SelectionText(c.Selection),
        ["state"] = Campaigns.StateText(c.State),
        ["effectiveState"] = Campaigns.StateText(c.EffectiveState(today)),
        ["contractedTotal"] = c.ContractedTotal,
        ["contractedPerDay"] = c.ContractedPerDay,
        ["notes"] = c.Notes,
        ["modified"] = c.Modified.ToString("O"),
        ["spots"] = new JsonArray(c.Spots.Select(s =>
        {
            var spot = new JsonObject
            {
                ["trackId"] = s.TrackId,
                ["startDate"] = s.StartDate is { } from ? DbTime.ToText(from) : null,
                ["endDate"] = s.EndDate is { } to ? DbTime.ToText(to) : null,
                ["daysMask"] = s.DaysMask,
                ["weight"] = s.Weight,
            };
            if (library is not null)
            {
                if (library.GetTrack(s.TrackId) is { } track)
                {
                    var t = QueueJson.Track(track);
                    t["validFrom"] = track.ValidFrom is { } vf ? DbTime.ToText(vf) : null;
                    t["validUntil"] = track.ValidUntil is { } vu ? DbTime.ToText(vu) : null;
                    spot["track"] = t;
                }
                else spot["track"] = null;
            }
            return (JsonNode)spot;
        }).ToArray()),
    };
}
