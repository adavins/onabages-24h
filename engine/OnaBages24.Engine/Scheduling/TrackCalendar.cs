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
    /// Els dies de la setmana en què pot sonar a partir d'avui (bit0 = dilluns; 0 = ja no sonarà mai):
    /// - dates soltes: els dies de la setmana de les dates d'avui en endavant (una sola data, un sol dia);
    /// - dies del mes: qualsevol dia;
    /// - la resta, els seus dies de la setmana; si la data de fi ja ha passat, cap.
    /// </summary>
    public static int EventDays(ScheduledEvent ev, DateOnly today)
    {
        if (ev.EndDate is { } end && end < today) return 0;
        if (ev.Recurrence == EventRecurrence.Dates)
            return ev.IncludedDates.Where(d => d >= today && !ev.ExcludedDates.Contains(d))
                .Aggregate(0, (mask, d) => mask | Weekdays.Bit(d.DayOfWeek));
        return ev.Recurrence == EventRecurrence.DaysOfMonth ? Weekdays.All : ev.DaysMask;
    }

    /// <summary>Les dates soltes d'avui en endavant (per dir-les a la casella), en ordre.</summary>
    public static IReadOnlyList<DateOnly> UpcomingDates(ScheduledEvent ev, DateOnly today) =>
        ev.Recurrence == EventRecurrence.Dates
            ? ev.IncludedDates.Where(d => d >= today && !ev.ExcludedDates.Contains(d)).Order().ToList()
            : [];

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

    /// <summary>
    /// La graella de la pista: una fila per hora, en ordre. Només els esdeveniments que encara poden sonar a partir de
    /// <paramref name="today"/> (els que ja s'han acabat o les dates soltes passades, no).
    /// </summary>
    public static IReadOnlyList<CalendarRow> Build(IEnumerable<ScheduledEvent> events, long trackId, DateOnly today)
    {
        // Un bloc de falques buit no hi surt (D-105: no n'hauria d'haver cap; s'esborren sols)
        var carrying = events.Where(Carries).Where(e => EventDays(e, today) != 0 && !IsEmptyBlock(e)).ToList();

        // Franges ocupades: els TALLA amb durada màxima ocupen des de la seva hora fins a hora + durada (planificador.md 3)
        var busy = carrying.Where(e => e.Mode == EventMode.Cut && e.MaxDurationSeconds is > 0)
            .SelectMany(e => Recurrences.TimesOfDay(e).Select(t => (Event: e, Start: t.ToTimeSpan(), End: t.ToTimeSpan() + TimeSpan.FromSeconds(e.MaxDurationSeconds!.Value))))
            .ToList();

        var slots = new Dictionary<(TimeOnly Time, int Day), List<ScheduledEvent>>();
        foreach (var ev in carrying)
        {
            int days = EventDays(ev, today);
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
                string? busyBy = busy.FirstOrDefault(b => b.Event.Id != chosen.Id && (EventDays(b.Event, today) & (1 << day)) != 0
                    && time.ToTimeSpan() > b.Start && time.ToTimeSpan() < b.End).Event?.Name;
                cells[day] = new CalendarCell(chosen, IsMarked(chosen, trackId, day), ordered.Skip(1).Select(e => e.Name).ToList(), busyBy);
            }
            rows.Add(new CalendarRow(time, cells));
        }
        return rows;
    }

    /// <summary>Un bloc de falques sense cap pista (D-105: s'esborra sol).</summary>
    public static bool IsEmptyBlock(ScheduledEvent ev) => ev.CommercialBlock && !ev.IsTemplate && ev.Actions.Count == 0;

    /// <summary>La casella d'una hora i un dia (0 = dilluns), o null si no n'hi ha cap (no hi ha cap esdeveniment).</summary>
    public static CalendarCell? CellAt(IReadOnlyList<CalendarRow> rows, TimeOnly time, int day) =>
        day is >= 0 and <= 6 ? rows.FirstOrDefault(r => r.Time == time)?.Days[day] : null;

    /// <summary>
    /// Un bloc de falques nou (D-105), encara sense pistes ni dies: el crea el calendari en marcar una casella on no hi
    /// ha cap esdeveniment. Els seus dies són sempre els de les seves pistes (<see cref="Set"/>).
    /// </summary>
    public static ScheduledEvent NewBlock(TimeOnly time) => new()
    {
        Name = $"Bloc de falques {time:HH\\:mm}",
        Mode = EventMode.Queue,
        StartTime = time,
        DaysMask = 0,
        CommercialBlock = true,
        Enabled = true,
    };

    /// <summary>
    /// Marca o desmarca la pista a l'esdeveniment el dia <paramref name="day"/> (0 = dilluns) i retorna l'esdeveniment
    /// canviat (sense desar). Una pista nova va abans de la primera de tipus Programa (si no n'hi ha, al final), perquè no
    /// quedi mai darrere d'un programa (D-028). Si ja hi era, només canvien els seus dies.
    /// Un bloc de falques (D-105) es pot marcar qualsevol dia: els dies del bloc passen a ser els de les seves pistes (cada
    /// pista amb els seus dies, sempre explícits). Si es queda sense pistes, té 0 dies i qui el desa l'ha d'esborrar.
    /// </summary>
    /// <param name="typeOf">El tipus de cada pista de l'esdeveniment (per trobar el primer programa); null si no hi és.</param>
    /// <exception cref="ArgumentException">Si l'esdeveniment no pot portar pistes o no sona aquell dia.</exception>
    public static ScheduledEvent Set(ScheduledEvent ev, long trackId, int day, bool marked, Func<long, TrackType?> typeOf, DateOnly today)
    {
        if (!Carries(ev)) throw new ArgumentException("Aquest esdeveniment no pot portar pistes (només TALLA i ENCUA).");
        if (day is < 0 or > 6) throw new ArgumentException("El dia ha de ser de 0 (dilluns) a 6 (diumenge).");
        bool block = ev.CommercialBlock;
        // Dies de la setmana per als dies propis de l'acció: en una data solta, els d'aquella data (i, si ja ha passat, cap).
        // Un bloc de falques, qualsevol dia (els seus dies surten de les pistes).
        int eventDays = block ? Weekdays.All : EventDays(ev, today);
        int bit = 1 << day;
        if ((eventDays & bit) == 0) throw new ArgumentException($"«{ev.Name}» no sona aquest dia.");

        // Dies d'una acció sense dies propis: els de l'esdeveniment (en un bloc, els que tenia el bloc)
        int ActionDays(EventAction a) => a.DaysMask ?? (block ? ev.DaysMask : eventDays);
        int? Normalize(int mask) => block ? mask & Weekdays.All : (mask & eventDays) == eventDays ? null : mask & eventDays;
        var actions = ev.Actions.ToList();
        int index = actions.FindIndex(a => TrackOf(a) == trackId);
        if (marked)
        {
            if (index >= 0)
            {
                int mask = ActionDays(actions[index]) | bit;
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
            int mask = ActionDays(actions[index]) & ~bit;
            if ((mask & eventDays) == 0) actions.RemoveAt(index);
            else actions[index] = actions[index] with { DaysMask = Normalize(mask) };
        }
        if (!block) return ev with { Actions = actions };

        // Bloc de falques: sona els dies que té alguna acció (una sense dies propis, p. ex. posada des del Planificador,
        // compta amb els dies que tenia el bloc). Sense cap acció, 0 dies: s'ha d'esborrar.
        int blockDays = actions.Aggregate(0, (m, a) => m | (a.DaysMask ?? ev.DaysMask));
        return ev with { Actions = actions, DaysMask = blockDays & Weekdays.All };
    }

    /// <summary>L'acció sona el dia <paramref name="day"/> (0 = dilluns): no té dies propis o el té marcat.</summary>
    public static bool AppliesOn(EventAction action, int day) => action.DaysMask is not { } mask || (mask & (1 << day)) != 0;

    /// <summary>
    /// Mou la pista una posició amunt (<paramref name="delta"/> = −1) o avall (+1) dins del contingut de l'esdeveniment
    /// que sona el dia <paramref name="day"/> (fase 4, pas 2c): passa just abans (o just després) de l'acció veïna
    /// d'aquell dia. L'ordre de les accions és un de sol per a tots els dies. Retorna l'esdeveniment canviat (sense
    /// desar); si ja és la primera (o l'última), sense canvis.
    /// </summary>
    /// <exception cref="ArgumentException">Si la pista no hi és aquell dia o el moviment no és −1 o +1.</exception>
    public static ScheduledEvent Move(ScheduledEvent ev, long trackId, int day, int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentException("Només es pot moure una posició amunt (−1) o avall (+1).");
        var actions = ev.Actions.ToList();
        var ofDay = Enumerable.Range(0, actions.Count).Where(i => AppliesOn(actions[i], day)).ToList();
        int position = ofDay.FindIndex(i => TrackOf(actions[i]) == trackId);
        if (position < 0) throw new ArgumentException($"La pista no és a «{ev.Name}» aquest dia.");
        int target = position + delta;
        if (target < 0 || target >= ofDay.Count) return ev;

        int from = ofDay[position];
        int neighbor = ofDay[target];
        var item = actions[from];
        actions.RemoveAt(from);
        if (neighbor > from) neighbor--; // s'ha desplaçat en treure la pista
        actions.Insert(delta < 0 ? neighbor : neighbor + 1, item);
        return ev with { Actions = actions };
    }

    /// <summary>
    /// Passis de la pista entre <paramref name="from"/> i <paramref name="to"/> (tots dos inclosos), dia a dia amb les
    /// dates reals: només els esdeveniments actius, les seves recurrències i excepcions, els dies propis de l'acció i les
    /// dates de la pista. També els d'una setmana tipus (per dia de la setmana) i quants dies estan marcats en esdeveniments desactivats.
    /// </summary>
    /// <param name="today">Per a la setmana tipus: les dates soltes que ja han passat no hi compten.</param>
    public static CalendarCounts Count(IEnumerable<ScheduledEvent> events, Track track, DateOnly from, DateOnly to, DateOnly today)
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
                if ((EventDays(ev, today) & (1 << day)) == 0 || !IsMarked(ev, track.Id, day)) continue;
                if (ev.Enabled) perWeekday[day] += Recurrences.TimesOfDay(ev).Count;
                else markedInDisabled++;
            }

        return new CalendarCounts(from, to, total, perWeekday,
            weeks.Select(w => (w.Key, w.Value)).ToList(), months.Select(m => (m.Key.Item1, m.Key.Item2, m.Value)).ToList(), markedInDisabled);
    }
}
