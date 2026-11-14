using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Un passi previst per a l'informe de programació: l'emissió d'un esdeveniment i la pista (null = passi buit).</summary>
public sealed record PlannedPass(long EventId, string EventName, DateTimeOffset At, long? TrackId);

/// <summary>
/// Un passi previst comparat amb el que va sonar (certificat amb «Previst i emès», fase 4, pas 4c; D-141): l'emissió que
/// li correspon (null = no va sonar) i, si no va sonar, el que diu el registre d'execució d'aquella emissió de
/// l'esdeveniment (null = no hi consta res: el motor no funcionava).
/// </summary>
public sealed record PassCheck(PlannedPass Pass, HistoryEntry? Emission, ExecutionRecord? Execution);

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

    /// <summary>Quant abans de l'hora prevista pot començar una emissió perquè compti per a aquell passi.</summary>
    public static readonly TimeSpan EarlyMargin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Quant després de l'hora prevista pot començar (un ENCUA espera que s'acabi el que sona), sempre abans del passi
    /// següent.
    /// </summary>
    public static readonly TimeSpan LateMargin = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Compara els passis previstos amb les emissions reals (D-141). Per ordre, cada passi es queda la primera emissió
    /// que encara no és de ningú i que va començar entre 2 minuts abans de l'hora prevista i 60 minuts després (i abans
    /// del passi previst següent); si n'hi ha una de la mateixa falca, aquesta. Un passi sense emissió porta la línia del
    /// registre que n'explica el motiu (<paramref name="executions"/>: les línies d'una emissió d'un esdeveniment).
    /// Retorna també les emissions que no corresponen a cap passi previst (p. ex. posades a mà).
    /// </summary>
    public static (IReadOnlyList<PassCheck> Checks, IReadOnlyList<HistoryEntry> Unplanned) Compare(
        IReadOnlyList<PlannedPass> planned, IReadOnlyList<HistoryEntry> emissions,
        Func<long, DateTimeOffset, IReadOnlyList<ExecutionRecord>> executions)
    {
        var sorted = emissions.OrderBy(e => e.Started).ThenBy(e => e.Id).ToList();
        var used = new bool[sorted.Count];
        var passes = planned.OrderBy(p => p.At).ThenBy(p => p.EventId).ToList();
        var checks = new List<PassCheck>();
        for (int i = 0; i < passes.Count; i++)
        {
            var pass = passes[i];
            var limit = pass.At + LateMargin;
            var next = passes.Skip(i + 1).FirstOrDefault(p => p.At > pass.At);
            if (next is not null && next.At - EarlyMargin < limit) limit = next.At - EarlyMargin;

            int found = -1;
            for (int j = 0; j < sorted.Count; j++)
            {
                if (used[j] || sorted[j].Started < pass.At - EarlyMargin || sorted[j].Started >= limit) continue;
                if (sorted[j].TrackId == pass.TrackId) { found = j; break; }
                if (found < 0) found = j;
            }
            if (found >= 0)
            {
                used[found] = true;
                checks.Add(new PassCheck(pass, sorted[found], null));
                continue;
            }
            // No va sonar: el motiu, del registre. Un NO_EMES (esborrat de la cua) diu més que l'OK de quan es va encuar.
            var lines = executions(pass.EventId, pass.At);
            var why = lines.FirstOrDefault(l => l.Result == ExecutionResult.NotPlayed)
                ?? lines.FirstOrDefault(l => l.Result != ExecutionResult.Ok)
                ?? lines.FirstOrDefault();
            checks.Add(new PassCheck(pass, null, why));
        }
        var unplanned = sorted.Where((_, j) => !used[j]).ToList();
        return (checks, unplanned);
    }
}
