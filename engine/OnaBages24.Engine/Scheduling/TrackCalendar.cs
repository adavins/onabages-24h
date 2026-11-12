using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>
/// Una casella del calendari d'una pista: l'esdeveniment que hi ha aquell dia a aquella hora i si la pista hi és.
/// </summary>
/// <param name="Event">L'esdeveniment (si n'hi ha diversos, el de més prioritat; els altres, a <paramref name="Others"/>).</param>
/// <param name="Marked">La pista hi és (una acció PISTA que toca aquell dia).</param>
/// <param name="Others">Altres esdeveniments a la mateixa hora i el mateix dia (avís: només se'n fa servir un).</param>
/// <param name="BusyBy">El TALLA que ocupa aquesta franja amb la seva durada màxima (un programa llarg), o null.</param>
public sealed record CalendarCell(ScheduledEvent Event, bool Marked, IReadOnlyList<string> Others, string? BusyBy);

/// <summary>Una fila del calendari: una hora del dia i, per a cada dia de la setmana (0 = dilluns), la casella o null.</summary>
public sealed record CalendarRow(TimeOnly Time, CalendarCell?[] Days);

/// <summary>Passis d'una pista: total del període, per setmana natural i per mes natural (dins del període), i els d'una setmana tipus.</summary>
public sealed record CalendarCounts(
    DateOnly From, DateOnly To, int Total, int[] PerWeekday,
    IReadOnlyList<(DateOnly WeekStart, int Count)> Weeks, IReadOnlyList<(int Year, int Month, int Count)> Months,
    int MarkedInDisabled);

/// <summary>
/// Calendari d'una pista (fase 4, pas 2; D-028, D-029, planificador.md 10): la graella setmanal on es marca a quins
/// esdeveniments ha de sonar una pista (una falca, una càpsula, una promo…; de qualsevol tipus).
/// - Files: les hores on hi ha esdeveniments que poden portar pistes (TALLA, ENCUA i blocs de falques; no les plantilles).
/// - Marcar una casella afegeix la pista a l'esdeveniment (acció PISTA) només aquell dia (dies propis de l'acció, D-028),
///   abans de la primera pista de tipus Programa (si no n'hi ha, al final). Desmarcar-la la treu d'aquell dia.
/// - Recomptes de passis amb les dates reals: recurrències, excepcions, dies propis i les dates de la pista.
/// Només calcula; no desa res.
/// </summary>
public static class TrackCalendar
{
    /// <summary>Els esdeveniments que poden portar pistes al calendari.</summary>
    public static bool Carries(ScheduledEvent ev) => !ev.IsTemplate && ev.Mode is EventMode.Cut or EventMode.Queue;

    /// <summary>
    /// Els dies de la setmana en què pot sonar (bit0 = dilluns). Els que no van per dies de la setmana (dies del mes, dates
    /// soltes) poden caure qualsevol dia.
    /// </summary>
    public static int EventDays(ScheduledEvent ev) =>
        ev.Recurrence is EventRecurrence.DaysOfMonth or EventRecurrence.Dates ? Weekdays.All : ev.DaysMask;

    /// <summary>La pista (<c>songId</c>) d'una acció PISTA, o null.</summary>
    public static long? TrackOf(EventAction action)
    {
        if (action.Type != EventActionType.Track) return null;
        try
        {
            return JsonNode.Parse(action.Params)?["songId"]?.GetValue<long>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>La pista és a l'esdeveniment el dia <paramref name="day"/> (0 = dilluns).</summary>
    public static bool IsMarked(ScheduledEvent ev, long trackId, int day) =>
        ev.Actions.Any(a => TrackOf(a) == trackId && (a.DaysMask is not { } mask || (mask & (1 << day)) != 0));

    /// <summary>La graella de la pista: una fila per hora, en ordre.</summary>
    public static IReadOnlyList<CalendarRow> Build(IEnumerable<ScheduledEvent> events, long trackId)
    {
        var carrying = events.Where(Carries).ToList();

        // Franges ocupades: els TALLA amb durada màxima ocupen des de la seva hora fins a hora + durada (planificador.md 3)
        var busy = carrying.Where(e => e.Mode == EventMode.Cut && e.MaxDurationSeconds is > 0)
            .SelectMany(e => Recurrences.TimesOfDay(e).Select(t => (Event: e, Start: t.ToTimeSpan(), End: t.ToTimeSpan() + TimeSpan.FromSeconds(e.MaxDurationSeconds!.Value))))
            .ToList();

        var slots = new Dictionary<(TimeOnly Time, int Day), List<ScheduledEvent>>();
        foreach (var ev in carrying)
        {
            int days = EventDays(ev);
            foreach (var time in Recurrences.TimesOfDay(ev))
                for (int day = 0; day < 7; day++)
                {
                    if ((days & (1 << day)) == 0) continue;
                    if (!slots.TryGetValue((time, day), out var list)) slots[(time, day)] = list = [];
                    list.Add(ev);
                }
        }

        var rows = new List<CalendarRow>();
        foreach (var time in slots.Keys.Select(k => k.Time).Distinct().Order())
        {
            var cells = new CalendarCell?[7];
            for (int day = 0; day < 7; day++)
            {
                if (!slots.TryGetValue((time, day), out var list)) continue;
                // Si n'hi ha diversos, el que es fa servir: el de més prioritat; a igualtat, l'actiu i el més antic
                var ordered = list.OrderByDescending(e => e.Priority).ThenByDescending(e => e.Enabled).ThenBy(e => e.Id).ToList();
                var chosen = ordered[0];
                string? busyBy = busy.FirstOrDefault(b => b.Event.Id != chosen.Id && (EventDays(b.Event) & (1 << day)) != 0
                    && time.ToTimeSpan() > b.Start && time.ToTimeSpan() < b.End).Event?.Name;
                cells[day] = new CalendarCell(chosen, IsMarked(chosen, trackId, day), ordered.Skip(1).Select(e => e.Name).ToList(), busyBy);
            }
            rows.Add(new CalendarRow(time, cells));
        }
        return rows;
    }

    /// <summary>
    /// Marca o desmarca la pista a l'esdeveniment el dia <paramref name="day"/> (0 = dilluns) i retorna l'esdeveniment
    /// canviat (sense desar). Una pista nova va abans de la primera de tipus Programa (si no n'hi ha, al final), perquè no
    /// quedi mai darrere d'un programa (D-028). Si ja hi era, només canvien els seus dies.
    /// </summary>
    /// <param name="typeOf">El tipus de cada pista de l'esdeveniment (per trobar el primer programa); null si no hi és.</param>
    /// <exception cref="ArgumentException">Si l'esdeveniment no pot portar pistes o no sona aquell dia.</exception>
    public static ScheduledEvent Set(ScheduledEvent ev, long trackId, int day, bool marked, Func<long, TrackType?> typeOf)
    {
        if (!Carries(ev)) throw new ArgumentException("Aquest esdeveniment no pot portar pistes (només TALLA i ENCUA).");
        if (day is < 0 or > 6) throw new ArgumentException("El dia ha de ser de 0 (dilluns) a 6 (diumenge).");
        int eventDays = EventDays(ev);
        int bit = 1 << day;
        if ((eventDays & bit) == 0) throw new ArgumentException($"«{ev.Name}» no sona aquest dia.");

        int? Normalize(int mask) => (mask & eventDays) == eventDays ? null : mask & eventDays;
        var actions = ev.Actions.ToList();
        int index = actions.FindIndex(a => TrackOf(a) == trackId);
        if (marked)
        {
            if (index >= 0)
            {
                int mask = (actions[index].DaysMask ?? eventDays) | bit;
                actions[index] = actions[index] with { DaysMask = Normalize(mask) };
            }
            else
            {
                var action = new EventAction
                {
                    Type = EventActionType.Track,
                    Params = new JsonObject { ["songId"] = trackId }.ToJsonString(),
                    DaysMask = Normalize(bit),
                };
                int program = actions.FindIndex(a => TrackOf(a) is { } id && typeOf(id) == TrackType.Program);
                actions.Insert(program >= 0 ? program : actions.Count, action);
            }
        }
        else if (index >= 0)
        {
            int mask = (actions[index].DaysMask ?? eventDays) & ~bit;
            if ((mask & eventDays) == 0) actions.RemoveAt(index);
            else actions[index] = actions[index] with { DaysMask = Normalize(mask) };
        }
        return ev with { Actions = actions };
    }

    /// <summary>
    /// Passis de la pista entre <paramref name="from"/> i <paramref name="to"/> (tots dos inclosos), dia a dia amb les
    /// dates reals: només els esdeveniments actius, les seves recurrències i excepcions, els dies propis de l'acció i les
    /// dates de la pista. També els d'una setmana tipus (per dia de la setmana) i quants dies estan marcats en esdeveniments desactivats.
    /// </summary>
    public static CalendarCounts Count(IEnumerable<ScheduledEvent> events, Track track, DateOnly from, DateOnly to)
    {
        var carrying = events.Where(Carries).Where(e => e.Actions.Any(a => TrackOf(a) == track.Id)).ToList();
        var active = carrying.Where(e => e.Enabled).ToList();

        int total = 0;
        var weeks = new SortedDictionary<DateOnly, int>();
        var months = new SortedDictionary<(int, int), int>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (track.ValidFrom is { } vf && date < vf) continue;
            if (track.ValidUntil is { } vu && date > vu) continue;
            int count = 0;
            foreach (var ev in active)
            {
                if (!Recurrences.OccursOn(ev, date)) continue;
                int times = Recurrences.TimesOfDay(ev).Count;
                count += times * Recurrences.ActionsFor(ev, date).Count(a => TrackOf(a) == track.Id);
            }
            if (count == 0) continue;
            total += count;
            var weekStart = date.AddDays(-(((int)date.DayOfWeek + 6) % 7)); // dilluns
            weeks[weekStart] = weeks.GetValueOrDefault(weekStart) + count;
            months[(date.Year, date.Month)] = months.GetValueOrDefault((date.Year, date.Month)) + count;
        }

        // Setmana tipus: les caselles marcades (amb les repeticions dins del dia), sense dates
        var perWeekday = new int[7];
        int markedInDisabled = 0;
        foreach (var ev in carrying)
            for (int day = 0; day < 7; day++)
            {
                if ((EventDays(ev) & (1 << day)) == 0 || !IsMarked(ev, track.Id, day)) continue;
                if (ev.Enabled) perWeekday[day] += Recurrences.TimesOfDay(ev).Count;
                else markedInDisabled++;
            }

        return new CalendarCounts(from, to, total, perWeekday,
            weeks.Select(w => (w.Key, w.Value)).ToList(), months.Select(m => (m.Key.Item1, m.Key.Item2, m.Value)).ToList(), markedInDisabled);
    }
}
