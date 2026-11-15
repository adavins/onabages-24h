using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

// Avisos de vigència (fase 4, pas 6b; D-146): les pistes i les campanyes dels esdeveniments que s'han acabat, no sonaran,
// tenen les dates malament o comencen aviat; i treure dels esdeveniments una que ja s'ha acabat.
public sealed partial class CommandRouter
{
    private DateOnly ValidityToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(CampaignClock(), TimeZoneInfo.Local).DateTime);

    private static string KindText(ValidityKind kind) => kind switch
    {
        ValidityKind.Expired => "CADUCADA",
        ValidityKind.WontPlay => "NO_SONARA",
        ValidityKind.BadDates => "DATES_INCORRECTES",
        _ => "COMENCA_AVIAT",
    };

    /// <summary>Tots els avisos de vigència, en ordre (primer els més greus).</summary>
    private JsonObject ValidityList()
    {
        var library = LibraryOrError();
        var warnings = ValidityWarnings.Check(EventsOrError().List(), library.GetTrack, id => _campaigns?.Get(id), ValidityToday());
        return new JsonObject
        {
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)new JsonObject
            {
                ["kind"] = KindText(w.Kind),
                ["subject"] = w.IsCampaign ? "campaign" : "track",
                ["id"] = w.Id,
                ["name"] = w.Name,
                ["text"] = w.Text,
                ["events"] = new JsonArray(w.Events.Select(u => (JsonNode)new JsonObject { ["id"] = u.EventId, ["name"] = u.EventName }).ToArray()),
                ["canRemove"] = w.CanRemove,
            }).ToArray()),
        };
    }

    /// <summary>
    /// Treu una pista (<c>trackId</c>: accions PISTA, AUXILIAR i STREAM) o una campanya (<c>campaignId</c>: accions CAMPANYA)
    /// dels esdeveniments actius (els mateixos de <c>validity.list</c>). Un bloc de falques que es queda buit s'esborra (D-105);
    /// un altre esdeveniment que es quedaria sense cap acció no es toca i es diu. L'historial i les campanyes no es toquen.
    /// </summary>
    private JsonObject ValidityRemove(JsonObject msg)
    {
        var events = EventsOrError();
        bool isCampaign = msg["campaignId"] is not null;
        long id = RequiredId(msg, isCampaign ? "campaignId" : "trackId");
        bool Matches(EventAction a) => isCampaign
            ? a.Type == EventActionType.Campaign && CampaignPlanner.CampaignIdOf(a) == id
            : ValidityWarnings.TrackIdOf(a) == id;

        var today = ValidityToday();
        int changed = 0, deleted = 0;
        var kept = new JsonArray();
        foreach (var ev in events.List().Where(e => e.Enabled && !(e.EndDate is { } end && end < today) && e.Actions.Any(Matches)))
        {
            var actions = ev.Actions.Where(a => !Matches(a)).ToList();
            if (actions.Count == 0)
            {
                if (ev.CommercialBlock && !ev.IsTemplate)
                {
                    events.Delete(ev.Id);
                    deleted++;
                }
                else kept.Add((JsonNode)new JsonObject { ["id"] = ev.Id, ["name"] = ev.Name });
                continue;
            }
            events.Save(ev with { Actions = actions });
            changed++;
        }
        if (changed + deleted > 0) _scheduler?.Reload();
        return new JsonObject { ["changed"] = changed, ["deleted"] = deleted, ["kept"] = kept };
    }
}
