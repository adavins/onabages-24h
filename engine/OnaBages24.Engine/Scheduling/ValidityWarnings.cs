using System.Globalization;
using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Tipus d'avís de vigència (fase 4, pas 6b; D-146).</summary>
public enum ValidityKind
{
    /// <summary>CADUCADA: la pista o la campanya ja s'ha acabat i encara és als esdeveniments.</summary>
    Expired,
    /// <summary>NO_SONARA: és als esdeveniments però no sonarà (desactivada, fitxer que falta, esborrany, falques que no poden sonar…).</summary>
    WontPlay,
    /// <summary>DATES_INCORRECTES: les dates no quadren (fi abans de l'inici, la pista no cobreix la falca…).</summary>
    BadDates,
    /// <summary>COMENCA_AVIAT: comença d'aquí a 7 dies o menys.</summary>
    StartsSoon,
}

/// <summary>
/// Un avís de vigència d'una pista o d'una campanya que fan servir els esdeveniments. <paramref name="Events"/>: els
/// esdeveniments actius on és. <paramref name="CanRemove"/>: es pot treure dels esdeveniments (només les caducades).
/// </summary>
public sealed record ValidityWarning(
    ValidityKind Kind, bool IsCampaign, long Id, string Name, string Text, IReadOnlyList<TrackUse> Events, bool CanRemove);

/// <summary>
/// Avisos de vigència (fase 4, pas 6b; D-146): les pistes i les campanyes que fan servir els esdeveniments actius
/// (activats i sense la data de fi passada; també les plantilles) i que ja s'han acabat, no sonaran, tenen les dates
/// malament o comencen d'aquí a pocs dies. Només calcula; no desa res.
/// </summary>
public static class ValidityWarnings
{
    /// <summary>Quants dies endavant es considera que una pista o una campanya «comença aviat».</summary>
    public const int SoonDays = 7;

    public static IReadOnlyList<ValidityWarning> Check(
        IEnumerable<ScheduledEvent> events, Func<long, Track?> getTrack, Func<long, Campaign?> getCampaign, DateOnly today)
    {
        // Qui fa servir cada pista i cada campanya (només els esdeveniments que encara poden sonar)
        var trackUses = new Dictionary<long, List<TrackUse>>();
        var campaignUses = new Dictionary<long, List<TrackUse>>();
        foreach (var ev in events.Where(e => e.Enabled && !(e.EndDate is { } end && end < today)))
        {
            var use = new TrackUse(ev.Id, ev.Name);
            foreach (var action in ev.Actions)
            {
                if (TrackIdOf(action) is { } trackId) Add(trackUses, trackId, use);
                else if (action.Type == EventActionType.Campaign && CampaignPlanner.CampaignIdOf(action) is { } campaignId) Add(campaignUses, campaignId, use);
            }
        }

        var list = new List<ValidityWarning>();
        foreach (var (trackId, uses) in trackUses)
        {
            if (getTrack(trackId) is not { } track) continue; // una pista que no existeix ja l'avisen els avisos de l'esdeveniment
            CheckTrack(track, uses, today, list);
        }
        foreach (var (campaignId, uses) in campaignUses)
        {
            if (getCampaign(campaignId) is not { } campaign) continue;
            CheckCampaign(campaign, uses, getTrack, today, list);
        }
        return list
            .OrderBy(w => w.Kind)
            .ThenBy(w => w.Name, StringComparer.Create(CultureInfo.GetCultureInfo("ca-ES"), CompareOptions.IgnoreCase))
            .ThenBy(w => w.Id)
            .ToList();
    }

    /// <summary>La pista (<c>songId</c>) d'una acció PISTA, AUXILIAR o STREAM, o null.</summary>
    public static long? TrackIdOf(EventAction action)
    {
        if (action.Type is not (EventActionType.Track or EventActionType.Auxiliary or EventActionType.Stream)) return null;
        try
        {
            return JsonNode.Parse(action.Params)?["songId"]?.GetValue<long>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void CheckTrack(Track track, List<TrackUse> uses, DateOnly today, List<ValidityWarning> list)
    {
        string name = TrackName(track);
        void Warn(ValidityKind kind, string text) => list.Add(new ValidityWarning(kind, false, track.Id, name, text, uses, kind == ValidityKind.Expired));
        string where = EventsText(uses);

        if (track.ValidFrom is { } from && track.ValidUntil is { } until && until < from)
            Warn(ValidityKind.BadDates, $"La data de fi ({Day(until)}) és anterior a la d'inici ({Day(from)}): no sonarà mai. És a {where}.");
        else if (track.ValidUntil is { } end && end < today)
            Warn(ValidityKind.Expired, $"Es va acabar el {Day(end)} i encara és a {where}.");
        else if (track.ValidFrom is { } start && start > today && start <= today.AddDays(SoonDays))
            Warn(ValidityKind.StartsSoon, $"Comença a sonar el {Day(start)} ({InDays(start, today)}). És a {where}.");

        if (!track.Enabled)
            Warn(ValidityKind.WontPlay, $"Està desactivada: no sonarà. És a {where}.");
        else if (track.FileState == FileState.Missing)
            Warn(ValidityKind.WontPlay, $"El fitxer falta: no sonarà. És a {where}.");
    }

    private static void CheckCampaign(Campaign campaign, List<TrackUse> uses, Func<long, Track?> getTrack, DateOnly today, List<ValidityWarning> list)
    {
        void Warn(ValidityKind kind, string text) =>
            list.Add(new ValidityWarning(kind, true, campaign.Id, campaign.Name, text, uses, kind == ValidityKind.Expired));
        string where = EventsText(uses);
        bool soon = campaign.StartDate <= today.AddDays(SoonDays);

        switch (campaign.EffectiveState(today))
        {
            case CampaignState.Finished:
                Warn(ValidityKind.Expired, campaign.EndDate < today
                    ? $"Es va acabar el {Day(campaign.EndDate)} i encara és a {where}."
                    : $"Està acabada i encara és a {where}.");
                return; // la resta ja no importa
            case CampaignState.Draft when soon:
                Warn(ValidityKind.WontPlay, campaign.StartDate <= today
                    ? $"És un esborrany: no sona, tot i que hauria d'haver començat el {Day(campaign.StartDate)}. És a {where}."
                    : $"És un esborrany: no sonarà si no la poses activa. Comença el {Day(campaign.StartDate)} ({InDays(campaign.StartDate, today)}). És a {where}.");
                break;
            case CampaignState.Draft:
                return; // un esborrany per a més endavant: és normal
            case CampaignState.Active when campaign.StartDate > today && soon:
                Warn(ValidityKind.StartsSoon, $"Comença a sonar el {Day(campaign.StartDate)} ({InDays(campaign.StartDate, today)}). És a {where}.");
                break;
        }

        // Les falques que encara han de sonar (la part d'avui en endavant)
        if (campaign.Spots.Count == 0)
        {
            Warn(ValidityKind.WontPlay, $"No té cap falca: no sonarà res. És a {where}.");
            return;
        }
        var wontPlay = new List<string>();
        var badDates = new List<string>();
        foreach (var spot in campaign.Spots)
        {
            var start = spot.StartDate ?? campaign.StartDate;
            var end = spot.EndDate ?? campaign.EndDate;
            if (end < today) continue; // ja s'ha acabat: és normal
            if (getTrack(spot.TrackId) is not { } track)
            {
                wontPlay.Add($"la pista {spot.TrackId} ja no existeix");
                continue;
            }
            string title = $"«{TrackName(track)}»";
            if (track.FileState == FileState.Missing) wontPlay.Add($"{title}: el fitxer falta");
            else if (!track.Enabled) wontPlay.Add($"{title}: està desactivada");

            if (end < start) badDates.Add($"{title}: la data de fi ({Day(end)}) és anterior a la d'inici ({Day(start)})");
            else if (start < campaign.StartDate || end > campaign.EndDate) badDates.Add($"{title}: les dates ({Day(start)}–{Day(end)}) surten de les de la campanya");
            else
            {
                // Les dates de la pista han de cobrir la part de la falca que queda
                var from = start < today ? today : start;
                if ((track.ValidFrom is { } tf && tf > from) || (track.ValidUntil is { } tu && tu < end))
                    badDates.Add($"{title}: les dates de la pista ({TrackDates(track)}) no cobreixen les de la falca ({Day(start)}–{Day(end)})");
            }
        }
        if (wontPlay.Count > 0)
            Warn(ValidityKind.WontPlay, $"{(wontPlay.Count == 1 ? "Una falca no sonarà" : $"{wontPlay.Count} falques no sonaran")}: {string.Join("; ", wontPlay)}. És a {where}.");
        if (badDates.Count > 0)
            Warn(ValidityKind.BadDates, $"{(badDates.Count == 1 ? "Una falca té" : $"{badDates.Count} falques tenen")} les dates malament: {string.Join("; ", badDates)}.");
    }

    private static void Add(Dictionary<long, List<TrackUse>> map, long id, TrackUse use)
    {
        if (!map.TryGetValue(id, out var uses)) map[id] = uses = [];
        if (!uses.Any(u => u.EventId == use.EventId)) uses.Add(use);
    }

    /// <summary>«l'esdeveniment «Tall de les 9»» o «3 esdeveniments».</summary>
    private static string EventsText(List<TrackUse> uses) => uses.Count == 1 ? $"l'esdeveniment «{uses[0].EventName}»" : $"{uses.Count} esdeveniments";

    public static string TrackName(Track track) =>
        string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.Path) : track.Title;

    private static string Day(DateOnly d) => d.ToString("d/M/yyyy", CultureInfo.InvariantCulture);

    private static string InDays(DateOnly date, DateOnly today) => (date.DayNumber - today.DayNumber) switch
    {
        1 => "demà",
        int n => $"d'aquí a {n} dies",
    };

    private static string TrackDates(Track track) => (track.ValidFrom, track.ValidUntil) switch
    {
        ({ } f, { } u) => $"{Day(f)}–{Day(u)}",
        ({ } f, null) => $"des del {Day(f)}",
        (null, { } u) => $"fins al {Day(u)}",
        _ => "sense límit",
    };
}
