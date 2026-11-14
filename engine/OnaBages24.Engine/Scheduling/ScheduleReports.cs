using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Un passi previst per a l'informe de programació: l'emissió d'un esdeveniment i la pista (null = passi buit).</summary>
public sealed record PlannedPass(long EventId, string EventName, DateTimeOffset At, long? TrackId);

/// <summary>
/// Dades de l'informe de programació (fase 4, pas 4; planificador.md 10): els passis previstos d'una pista o d'una
/// campanya entre dues dates. Només calcula; no desa res. El certificat d'emissió no surt d'aquí, sinó de l'historial.
/// </summary>
public static class ScheduleReports
{
    /// <summary>
    /// Els passis previstos d'una pista entre <paramref name="from"/> i <paramref name="to"/> (dies locals, tots dos
    /// inclosos): cada emissió dels esdeveniments actius que la porten aquell dia (acció PISTA, amb els dies propis), dins
    /// de les dates de la pista. Les hores que no existeixen pel canvi d'hora no compten.
    /// </summary>
    public static IReadOnlyList<PlannedPass> TrackPasses(Track track, IEnumerable<ScheduledEvent> events, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var subject = CalendarSubject.ForTrack(track.Id);
        var mine = events.Where(e => e.Enabled && TrackCalendar.Carries(e) && e.Actions.Any(subject.Matches)).ToList();
        var list = new List<PlannedPass>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (!track.ValidOn(date)) continue;
            foreach (var ev in mine)
            {
                if (!Recurrences.OccursOn(ev, date)) continue;
                int count = Recurrences.ActionsFor(ev, date).Count(subject.Matches);
                if (count == 0) continue;
                foreach (var time in Recurrences.TimesOfDay(ev))
                {
                    var o = Recurrences.At(ev, date, time, zone);
                    if (o.SkippedClockChange) continue;
                    for (int i = 0; i < count; i++) list.Add(new PlannedPass(ev.Id, ev.Name, o.At, track.Id));
                }
            }
        }
        list.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.EventId.CompareTo(b.EventId));
        return list;
    }

    /// <summary>
    /// Els passis previstos d'una campanya entre <paramref name="from"/> i <paramref name="to"/> (instants; <c>to</c> no
    /// inclòs), amb la falca de cadascun:
    /// - el que ja ha passat (abans de <paramref name="now"/>), del pla que es va desar (el que estava previst);
    /// - d'ara endavant, el pla calculat com el del motor (D-123), també si la campanya encara és un esborrany (per poder
    ///   donar l'informe a l'anunciant abans d'activar-la).
    /// </summary>
    public static IReadOnlyList<PlannedPass> CampaignPasses(Campaign campaign, Campaigns campaigns, IReadOnlyList<ScheduledEvent> events,
        Func<long, Track?> getTrack, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, TimeZoneInfo zone)
    {
        var names = events.ToDictionary(e => e.Id, e => e.Name);
        string Name(long id) => names.GetValueOrDefault(id) ?? $"Esdeveniment {id} (ja no existeix)";

        var list = new List<PlannedPass>();
        if (from < now)
        {
            foreach (var p in campaigns.Plan(campaign.Id, from, to < now ? to : now))
                list.Add(new PlannedPass(p.EventId, Name(p.EventId), p.At, p.TrackId));
        }
        if (to > now)
        {
            var past = campaigns.Played(campaign.Id, to: now);
            foreach (var p in CampaignPlanner.Compute(campaign, events, getTrack, past, now, zone))
                if (p.At >= from && p.At < to) list.Add(new PlannedPass(p.EventId, Name(p.EventId), p.At, p.TrackId));
        }
        list.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.EventId.CompareTo(b.EventId));
        return list;
    }
}
