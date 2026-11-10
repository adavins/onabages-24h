using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>
/// Una emissió concreta d'un esdeveniment: un dia i una hora.
/// </summary>
/// <param name="Event">L'esdeveniment.</param>
/// <param name="Date">Dia (calendari local).</param>
/// <param name="LocalTime">Hora local programada.</param>
/// <param name="At">
/// Instant en què ha de sonar. Si l'hora no existeix aquell dia (<paramref name="SkippedClockChange"/>), és l'instant del
/// canvi d'hora: serveix per ordenar-la i per anotar-la al registre, però no sona.
/// </param>
/// <param name="SkippedClockChange">L'hora no existeix aquell dia (pas a l'hora d'estiu): no sona (D-057).</param>
public sealed record Occurrence(ScheduledEvent Event, DateOnly Date, TimeOnly LocalTime, DateTimeOffset At, bool SkippedClockChange);

/// <summary>
/// Quan sona cada esdeveniment (docs/planificador.md, secció 12). Càlcul pur, sense base de dades ni rellotge.
///
/// Els dies es calculen amb dates de calendari (<see cref="DateOnly"/>), mai sumant hores, perquè els canvis d'horari
/// d'estiu no facin saltar un dia. Després, cada hora local es converteix a un instant amb la zona horària (D-057):
/// - una hora que no existeix (el dia que es passa a l'hora d'estiu, de 02:00 a 03:00) no sona: surt marcada com a omesa;
/// - una hora que passa dues vegades (a l'octubre) sona només la primera vegada.
///
/// No es mira si l'esdeveniment està actiu o és una plantilla: això ho decideix qui ho fa servir (el planificador no els
/// dispara; l'editor sí que en vol veure les dates).
/// </summary>
public static class Recurrences
{
    /// <summary>Sona aquest dia? (recurrència, rang de dates, dates afegides i excepcions)</summary>
    public static bool OccursOn(ScheduledEvent ev, DateOnly date)
    {
        if (ev.ExcludedDates.Contains(date)) return false;
        if (ev.StartDate is { } start && date < start) return false;
        if (ev.EndDate is { } end && date > end) return false;
        // Les dates afegides sonen sempre (en la recurrència DATES són les úniques)
        if (ev.IncludedDates.Contains(date)) return true;

        bool weekday = Weekdays.Contains(ev.DaysMask, date.DayOfWeek);
        return ev.Recurrence switch
        {
            EventRecurrence.Weekly => weekday,
            EventRecurrence.EveryNWeeks => weekday && IsInCycle(date, ev.AnchorDate, ev.IntervalWeeks),
            EventRecurrence.MonthlyOrdinal => weekday && ev.Ordinals.Any(o => IsOrdinal(date, o)),
            EventRecurrence.DaysOfMonth => ev.MonthDays.Any(d => d == date.Day || (d == -1 && date.Day == DaysInMonth(date))),
            EventRecurrence.Dates => false,
            _ => false,
        };
    }

    /// <summary>Hores del dia en què sona (una, o diverses si es repeteix cada hora o cada N minuts), en ordre.</summary>
    public static IReadOnlyList<TimeOnly> TimesOfDay(ScheduledEvent ev)
    {
        switch (ev.Repeat)
        {
            case EventRepeat.EveryHour:
                // Al minut i segon de l'hora d'inici, a cada hora triada (null = totes)
                return Enumerable.Range(0, 24)
                    .Where(h => ev.HoursMask is not { } mask || (mask & (1 << h)) != 0)
                    .Select(h => new TimeOnly(h, ev.StartTime.Minute, ev.StartTime.Second))
                    .ToList();

            case EventRepeat.EveryNMinutes when ev.IntervalMinutes is > 0:
                // De l'hora d'inici fins a mitjanit
                var times = new List<TimeOnly>();
                for (var t = ev.StartTime.ToTimeSpan(); t < TimeSpan.FromDays(1); t += TimeSpan.FromMinutes(ev.IntervalMinutes.Value))
                    times.Add(TimeOnly.FromTimeSpan(t));
                return times;

            default:
                return [ev.StartTime];
        }
    }

    /// <summary>
    /// Emissions d'un esdeveniment amb l'instant entre <paramref name="from"/> (inclòs) i <paramref name="to"/> (exclòs),
    /// en ordre. Inclou les omeses pel canvi d'hora, marcades.
    /// </summary>
    public static IEnumerable<Occurrence> Between(ScheduledEvent ev, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        // Un dia de marge a cada banda: l'instant d'una hora local pot caure en un altre dia UTC
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, zone).DateTime).AddDays(1);
        var times = TimesOfDay(ev);
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (!OccursOn(ev, date)) continue;
            foreach (var time in times)
            {
                var occurrence = At(ev, date, time, zone);
                if (occurrence.At >= from && occurrence.At < to) yield return occurrence;
            }
        }
    }

    /// <summary>
    /// Les <paramref name="count"/> pròximes emissions a partir de <paramref name="from"/> (inclòs), per a la previsualització
    /// de l'editor. Busca com a molt <paramref name="maxDays"/> dies endavant. Inclou les omeses pel canvi d'hora.
    /// </summary>
    public static IReadOnlyList<Occurrence> Next(ScheduledEvent ev, DateTimeOffset from, int count, TimeZoneInfo zone, int maxDays = 800)
    {
        var list = new List<Occurrence>();
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime).AddDays(-1);
        var times = TimesOfDay(ev);
        for (int i = 0; i <= maxDays && list.Count < count; i++, day = day.AddDays(1))
        {
            if (!OccursOn(ev, day)) continue;
            foreach (var time in times)
            {
                var occurrence = At(ev, day, time, zone);
                if (occurrence.At >= from) list.Add(occurrence);
                if (list.Count == count) break;
            }
        }
        return list;
    }

    /// <summary>Les accions que toquen aquest dia (les que no tenen dies propis, sempre; D-028).</summary>
    public static IReadOnlyList<EventAction> ActionsFor(ScheduledEvent ev, DateOnly date) =>
        ev.Actions.Where(a => a.DaysMask is not { } mask || Weekdays.Contains(mask, date.DayOfWeek)).ToList();

    /// <summary>Converteix un dia i una hora locals en una emissió amb el seu instant (D-057).</summary>
    public static Occurrence At(ScheduledEvent ev, DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // L'hora no existeix: l'instant és el del salt, és a dir, la primera hora en punt vàlida de després (03:00)
            var after = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
            while (zone.IsInvalidTime(after)) after = after.AddHours(1);
            return new Occurrence(ev, date, time, new DateTimeOffset(after, zone.GetUtcOffset(after)), SkippedClockChange: true);
        }
        // Una hora que passa dues vegades: la primera, que és la de l'horari d'estiu (el desplaçament més gran)
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new Occurrence(ev, date, time, new DateTimeOffset(local, offset), SkippedClockChange: false);
    }

    /// <summary>CADA_N_SETMANES: la setmana d'aquest dia és a N setmanes (o un múltiple) de la de l'àncora?</summary>
    private static bool IsInCycle(DateOnly date, DateOnly? anchor, int? intervalWeeks)
    {
        if (anchor is not { } a || intervalWeeks is not > 0) return false;
        int weeks = (MondayOf(date).DayNumber - MondayOf(a).DayNumber) / 7;
        int n = intervalWeeks.Value;
        return ((weeks % n) + n) % n == 0; // també abans de l'àncora
    }

    /// <summary>MENSUAL_ORDINAL: és el primer (1)… cinquè (5) o l'últim (−1) d'aquest dia de la setmana dins del mes?</summary>
    private static bool IsOrdinal(DateOnly date, int ordinal) =>
        ordinal == -1 ? date.Day + 7 > DaysInMonth(date) : (date.Day - 1) / 7 + 1 == ordinal;

    private static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static int DaysInMonth(DateOnly date) => DateTime.DaysInMonth(date.Year, date.Month);
}
