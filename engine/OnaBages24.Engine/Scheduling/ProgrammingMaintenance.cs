using System.Globalization;
using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Quin cas del manteniment de la programació és (D-194). L'ordre és el de la pantalla.</summary>
public enum MaintenanceCase
{
    /// <summary>ESDEVENIMENT_ACABAT: la data de fi ja ha passat, o totes les dates soltes.</summary>
    EndedEvent,
    /// <summary>ESDEVENIMENT_DESACTIVAT: desactivat i sense cap canvi des de fa temps.</summary>
    DisabledEvent,
    /// <summary>ESDEVENIMENT_DUPLICAT: un altre esdeveniment fa exactament el mateix, a la mateixa hora i els mateixos dies.</summary>
    DuplicateEvent,
    /// <summary>ESDEVENIMENT_NO_GUANYA: sempre coincideix amb un altre Talla o Encua de més prioritat.</summary>
    NeverWins,
    /// <summary>ESDEVENIMENT_SENSE_EFECTE: totes les seves pistes i campanyes ja no poden sonar mai.</summary>
    DeadEvent,
    /// <summary>ACCIONS_TRENCADES: accions que apunten a rotacions, bosses, subcategories o plantilles que ja no existeixen.</summary>
    BrokenActions,
    /// <summary>EXCEPCIONS_PASSADES: dies que no s'emet que ja han passat.</summary>
    PastExceptions,
    /// <summary>ACABADA_ALS_ESDEVENIMENTS: una pista o una campanya acabada que encara és als esdeveniments.</summary>
    ExpiredInEvents,
    /// <summary>PISTA_CADUCADA: una pista amb la data de fi passada que no fa servir res.</summary>
    ExpiredTrack,
    /// <summary>SUBCATEGORIA_BUIDA: una subcategoria sense pistes que no fa servir res.</summary>
    EmptySubcategory,
}

/// <summary>Què es proposa fer (D-194). Mai no es fa sol: l'aplica qui ho marca.</summary>
public enum MaintenanceAction
{
    DeleteEvent,
    RemoveActions,
    RemovePastExceptions,
    RemoveFromEvents,
    DeleteTrack,
    DisableTrack,
    DeleteSubcategory,
}

/// <summary>De què és la proposta.</summary>
public enum MaintenanceSubject { Event, Track, Campaign, Subcategory }

/// <summary>
/// Una proposta del manteniment. <paramref name="Key"/> la identifica per aplicar-la (p. ex. «event:12»): en aplicar, el
/// motor torna a analitzar i només fa les que encara hi són. <paramref name="ActionIds"/>: les accions que es treuen
/// (<see cref="MaintenanceAction.RemoveActions"/>).
/// </summary>
public sealed record MaintenanceFinding(
    string Key, MaintenanceCase Case, MaintenanceSubject Subject, long Id, string Name, string Reason, MaintenanceAction Action,
    IReadOnlyList<long> ActionIds);

/// <summary>El que necessita l'anàlisi. Tot es llegeix abans: l'anàlisi no toca la base de dades.</summary>
public sealed record MaintenanceInputs
{
    public required IReadOnlyList<ScheduledEvent> Events { get; init; }
    public required Func<long, Track?> GetTrack { get; init; }
    public required IReadOnlyList<Campaign> Campaigns { get; init; }
    public required IReadOnlySet<long> RotationIds { get; init; }
    public required IReadOnlySet<long> BagIds { get; init; }
    public required IReadOnlySet<long> SubcategoryIds { get; init; }
    /// <summary>Les pistes amb la data de fi passada (de tota la biblioteca).</summary>
    public required IReadOnlyList<Track> ExpiredTracks { get; init; }
    /// <summary>Si una pista ha sonat els últims 2 anys (D-011): llavors no s'esborra, es desactiva (els certificats).</summary>
    public required Func<long, bool> PlayedRecently { get; init; }
    /// <summary>Les subcategories sense pistes que no fa servir res (les calcula qui crida, amb els mateixos motius que en esborrar-ne una).</summary>
    public required IReadOnlyList<(long Id, string Name)> UnusedEmptySubcategories { get; init; }
    public required DateTimeOffset Now { get; init; }
    public required TimeZoneInfo Zone { get; init; }
}

/// <summary>
/// Manteniment de la programació (D-194): detecta el que s'ha anat acumulant i ja no sonarà (esdeveniments acabats,
/// duplicats o que no guanyen mai, accions trencades, pistes caducades, subcategories buides…) i proposa què fer-ne.
/// Només calcula: no desa res. Cada esdeveniment surt com a molt una vegada, pel primer motiu (l'ordre de
/// <see cref="MaintenanceCase"/>).
/// </summary>
public static class ProgrammingMaintenance
{
    /// <summary>Un esdeveniment desactivat es proposa per esborrar si fa aquests dies que no es toca (decisió del desenvolupador).</summary>
    public const int DisabledDays = 90;

    /// <summary>Quants dies endavant es miren les coincidències per saber si un esdeveniment no guanya mai.</summary>
    public const int ConflictDays = 365;

    public static IReadOnlyList<MaintenanceFinding> Scan(MaintenanceInputs input)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(input.Now, input.Zone).DateTime);
        var list = new List<MaintenanceFinding>();
        var events = input.Events.Where(e => !e.IsTemplate).ToList();
        var taken = new HashSet<long>(); // els esdeveniments que ja tenen una proposta d'esborrar-los

        void DeleteEvent(MaintenanceCase kind, ScheduledEvent ev, string reason)
        {
            if (!taken.Add(ev.Id)) return;
            list.Add(new MaintenanceFinding($"event:{ev.Id}", kind, MaintenanceSubject.Event, ev.Id, ev.Name, reason, MaintenanceAction.DeleteEvent, []));
        }

        // 1. Acabats: la data de fi ja ha passat, o totes les dates soltes
        foreach (var ev in events)
        {
            if (ev.EndDate is { } end && end < today)
                DeleteEvent(MaintenanceCase.EndedEvent, ev, $"Es va acabar el {Day(end)}.");
            else if (ev.Recurrence == EventRecurrence.Dates && ev.IncludedDates.Count > 0 && ev.IncludedDates.All(d => d < today))
                DeleteEvent(MaintenanceCase.EndedEvent, ev, $"Totes les seves dates ja han passat (l'última, el {Day(ev.IncludedDates.Max())}).");
        }

        // 2. Desactivats i sense tocar des de fa temps
        foreach (var ev in events.Where(e => !e.Enabled && e.Modified < input.Now.AddDays(-DisabledDays)))
            DeleteEvent(MaintenanceCase.DisabledEvent, ev,
                $"Desactivat, i no s'ha canviat des del {Day(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ev.Modified, input.Zone).DateTime))}.");

        // 3. Duplicats: es queda el primer (el d'id més baix) i es proposa esborrar els altres
        foreach (var group in events.Where(e => !taken.Contains(e.Id)).GroupBy(Signature).Where(g => g.Count() > 1))
        {
            var ordered = group.OrderBy(e => e.Id).ToList();
            foreach (var ev in ordered.Skip(1))
                DeleteEvent(MaintenanceCase.DuplicateEvent, ev, $"Fa exactament el mateix que «{ordered[0].Name}», a la mateixa hora i els mateixos dies.");
        }

        // 4. No guanyen mai: cada vegada que sonarien, hi ha un altre Talla o Encua de més prioritat al mateix instant
        foreach (var (ev, winner) in NeverWinning(events.Where(e => !taken.Contains(e.Id)).ToList(), input.Now, input.Zone))
            DeleteEvent(MaintenanceCase.NeverWins, ev,
                $"Sempre coincideix amb «{winner.Name}», de prioritat més alta ({winner.Priority}): no sona mai.");

        // 5. Sense efecte: totes les seves pistes i campanyes ja no poden sonar mai
        var campaigns = input.Campaigns.ToDictionary(c => c.Id);
        foreach (var ev in events.Where(e => !taken.Contains(e.Id)))
        {
            if (DeadReason(ev, input.GetTrack, campaigns, today) is { } reason)
                DeleteEvent(MaintenanceCase.DeadEvent, ev, reason);
        }

        // 6. Accions trencades i excepcions passades (dels que no s'esborren)
        foreach (var ev in events.Where(e => !taken.Contains(e.Id)))
        {
            var broken = ev.Actions.Select(a => (Action: a, Problem: BrokenReason(a, input))).Where(x => x.Problem is not null).ToList();
            if (broken.Count > 0)
            {
                string what = string.Join("; ", broken.Select(b => b.Problem));
                bool empty = broken.Count == ev.Actions.Count;
                if (empty)
                    DeleteEvent(MaintenanceCase.BrokenActions, ev, $"Totes les seves accions apunten a coses que ja no existeixen: {what}.");
                else
                    list.Add(new MaintenanceFinding($"actions:{ev.Id}", MaintenanceCase.BrokenActions, MaintenanceSubject.Event, ev.Id, ev.Name,
                        $"{(broken.Count == 1 ? "Una acció apunta" : $"{broken.Count} accions apunten")} a coses que ja no existeixen: {what}.",
                        MaintenanceAction.RemoveActions, broken.Select(b => b.Action.Id).ToList()));
            }
            if (taken.Contains(ev.Id)) continue;
            var past = ev.ExcludedDates.Where(d => d < today).OrderBy(d => d).ToList();
            if (past.Count > 0)
                list.Add(new MaintenanceFinding($"exceptions:{ev.Id}", MaintenanceCase.PastExceptions, MaintenanceSubject.Event, ev.Id, ev.Name,
                    past.Count == 1 ? $"Un dia que no s'emet ja ha passat ({Day(past[0])})."
                        : $"{past.Count} dies que no s'emet ja han passat (del {Day(past[0])} al {Day(past[^1])}).",
                    MaintenanceAction.RemovePastExceptions, []));
        }

        // 7. Pistes i campanyes acabades que encara són als esdeveniments que poden sonar (com els avisos de vigència)
        var remaining = input.Events.Where(e => !taken.Contains(e.Id)).ToList();
        foreach (var w in ValidityWarnings.Check(remaining, input.GetTrack, id => campaigns.GetValueOrDefault(id), today).Where(w => w.CanRemove))
        {
            var subject = w.IsCampaign ? MaintenanceSubject.Campaign : MaintenanceSubject.Track;
            list.Add(new MaintenanceFinding($"{(w.IsCampaign ? "campaign" : "track")}-events:{w.Id}", MaintenanceCase.ExpiredInEvents, subject,
                w.Id, w.Name, w.Text, MaintenanceAction.RemoveFromEvents, []));
        }

        // 8. Pistes caducades que no fa servir cap esdeveniment ni cap campanya
        var usedByEvents = input.Events.SelectMany(e => e.Actions).Select(ValidityWarnings.TrackIdOf).OfType<long>().ToHashSet();
        var usedByCampaigns = input.Campaigns.SelectMany(c => c.Spots).Select(s => s.TrackId).ToHashSet();
        foreach (var track in input.ExpiredTracks.Where(t => t.ValidUntil is { } u && u < today && !usedByEvents.Contains(t.Id)))
        {
            string name = ValidityWarnings.TrackName(track);
            string ended = $"Es va acabar el {Day(track.ValidUntil!.Value)} i no la fa servir cap esdeveniment";
            if (usedByCampaigns.Contains(track.Id) || input.PlayedRecently(track.Id))
            {
                // Els certificats la necessiten (D-011): es desactiva, no s'esborra
                if (!track.Enabled) continue;
                string why = usedByCampaigns.Contains(track.Id) ? "és d'una campanya" : "ha sonat els últims 2 anys";
                list.Add(new MaintenanceFinding($"track:{track.Id}", MaintenanceCase.ExpiredTrack, MaintenanceSubject.Track, track.Id, name,
                    $"{ended}. Com que {why}, es desactiva en lloc d'esborrar-la (els certificats la necessiten).", MaintenanceAction.DisableTrack, []));
            }
            else
                list.Add(new MaintenanceFinding($"track:{track.Id}", MaintenanceCase.ExpiredTrack, MaintenanceSubject.Track, track.Id, name,
                    $"{ended} i no ha sonat els últims 2 anys.", MaintenanceAction.DeleteTrack, []));
        }

        // 9. Subcategories buides que no fa servir res
        foreach (var (id, name) in input.UnusedEmptySubcategories)
            list.Add(new MaintenanceFinding($"subcategory:{id}", MaintenanceCase.EmptySubcategory, MaintenanceSubject.Subcategory, id, name,
                "No té cap pista i no la fa servir cap rotació, bossa, agenda, carpeta vigilada ni esdeveniment.", MaintenanceAction.DeleteSubcategory, []));

        var collator = StringComparer.Create(CultureInfo.GetCultureInfo("ca-ES"), CompareOptions.IgnoreCase);
        return list.OrderBy(f => f.Case).ThenBy(f => f.Name, collator).ThenBy(f => f.Id).ToList();
    }

    /// <summary>
    /// Els esdeveniments Talla o Encua activats que, cada vegada que sonarien en els pròxims <see cref="ConflictDays"/> dies,
    /// coincideixen amb un altre que hi guanya (més prioritat, o la mateixa i un id més baix: com el planificador). Amb el
    /// guanyador de la primera coincidència.
    /// </summary>
    private static IEnumerable<(ScheduledEvent Event, ScheduledEvent Winner)> NeverWinning(
        IReadOnlyList<ScheduledEvent> events, DateTimeOffset now, TimeZoneInfo zone)
    {
        var exclusive = events.Where(e => e.Enabled && e.Mode is EventMode.Cut or EventMode.Queue).ToList();
        var to = now.AddDays(ConflictDays);
        // Per instant, el que guanya (el primer per prioritat i id); i les emissions de cada esdeveniment
        var winners = new Dictionary<DateTimeOffset, ScheduledEvent>();
        var occurrences = new Dictionary<long, List<DateTimeOffset>>();
        foreach (var ev in exclusive)
        {
            var times = Recurrences.Between(ev, now, to, zone).Where(o => !o.SkippedClockChange).Select(o => o.At).ToList();
            occurrences[ev.Id] = times;
            foreach (var at in times)
            {
                if (!winners.TryGetValue(at, out var current) || Beats(ev, current)) winners[at] = ev;
            }
        }
        foreach (var ev in exclusive)
        {
            var times = occurrences[ev.Id];
            if (times.Count == 0 || times.Any(at => winners[at].Id == ev.Id)) continue;
            yield return (ev, winners[times[0]]);
        }
    }

    private static bool Beats(ScheduledEvent a, ScheduledEvent b) => a.Priority > b.Priority || (a.Priority == b.Priority && a.Id < b.Id);

    /// <summary>Per què un esdeveniment ja no farà res (totes les seves pistes i campanyes no poden sonar mai), o null.</summary>
    private static string? DeadReason(ScheduledEvent ev, Func<long, Track?> getTrack, Dictionary<long, Campaign> campaigns, DateOnly today)
    {
        if (ev.Actions.Count == 0) return null;
        var problems = new List<string>();
        foreach (var action in ev.Actions)
        {
            if (ValidityWarnings.TrackIdOf(action) is { } trackId)
            {
                var track = getTrack(trackId);
                if (track is null) problems.Add($"la pista {trackId} ja no existeix");
                else if (track.ValidUntil is { } until && until < today) problems.Add($"«{ValidityWarnings.TrackName(track)}» es va acabar el {Day(until)}");
                else return null; // aquesta encara pot sonar (una que falta o està desactivada es pot arreglar)
            }
            else if (action.Type == EventActionType.Campaign)
            {
                long? id = CampaignPlanner.CampaignIdOf(action);
                if (id is null || !campaigns.TryGetValue(id.Value, out var campaign)) problems.Add($"la campanya {id} ja no existeix");
                else if (campaign.EffectiveState(today) == CampaignState.Finished) problems.Add($"la campanya «{campaign.Name}» s'ha acabat");
                else return null;
            }
            else return null; // una acció d'una altra mena (rotació, entrada, títol…) encara fa alguna cosa
        }
        return $"Ja no farà res: {string.Join("; ", problems)}.";
    }

    /// <summary>Per què una acció apunta a una cosa que ja no existeix (rotació, bossa, subcategoria o plantilla), o null.</summary>
    private static string? BrokenReason(EventAction action, MaintenanceInputs input)
    {
        JsonNode? p;
        try { p = JsonNode.Parse(action.Params); }
        catch (System.Text.Json.JsonException) { return null; }
        long? Id(string name) => p?[name] is JsonValue v && v.TryGetValue(out long id) ? id : null;

        switch (action.Type)
        {
            case EventActionType.Rotation when Id("rotacioId") is { } rotation && !input.RotationIds.Contains(rotation):
                return $"la rotació {rotation}";
            case EventActionType.Bag when Id("bossaId") is { } bag && !input.BagIds.Contains(bag):
                return $"la bossa {bag}";
            case EventActionType.Bag or EventActionType.Sweepers when Id("subcategoriaId") is { } sub && !input.SubcategoryIds.Contains(sub):
                return $"la subcategoria {sub}";
            case EventActionType.Template when Id("plantillaId") is { } template
                && !input.Events.Any(e => e.Id == template && e.IsTemplate):
                return $"la plantilla {template}";
            default:
                return null;
        }
    }

    /// <summary>El que ha de ser igual perquè dos esdeveniments siguin duplicats: tot menys l'id, el nom, les notes i la data de canvi.</summary>
    private static string Signature(ScheduledEvent ev)
    {
        var parts = new List<string>
        {
            ev.Mode.ToString(), ev.StartTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture), ev.Recurrence.ToString(),
            ev.DaysMask.ToString(CultureInfo.InvariantCulture), ev.IntervalWeeks?.ToString(CultureInfo.InvariantCulture) ?? "",
            ev.AnchorDate?.ToString("O") ?? "", string.Join(',', ev.Ordinals), string.Join(',', ev.MonthDays),
            ev.StartDate?.ToString("O") ?? "", ev.EndDate?.ToString("O") ?? "", ev.Repeat.ToString(),
            ev.IntervalMinutes?.ToString(CultureInfo.InvariantCulture) ?? "", ev.HoursMask?.ToString(CultureInfo.InvariantCulture) ?? "",
            ev.ClearQueue.ToString(), ev.Enabled.ToString(),
            string.Join(',', ev.IncludedDates.Order().Select(d => d.ToString("O"))),
            string.Join(',', ev.ExcludedDates.Order().Select(d => d.ToString("O"))),
        };
        // Les accions, en ordre, amb els paràmetres normalitzats (el mateix JSON escrit diferent és el mateix)
        foreach (var a in ev.Actions)
            parts.Add($"{a.Type}|{Normalize(a.Params)}|{a.DaysMask?.ToString(CultureInfo.InvariantCulture) ?? ""}");
        return string.Join("\u001f", parts);
    }

    private static string Normalize(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return json;
            var sorted = new JsonObject();
            foreach (var (key, value) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[key] = value?.DeepClone();
            return sorted.ToJsonString();
        }
        catch (System.Text.Json.JsonException)
        {
            return json;
        }
    }

    private static string Day(DateOnly d) => d.ToString("d/M/yyyy", CultureInfo.InvariantCulture);
}
