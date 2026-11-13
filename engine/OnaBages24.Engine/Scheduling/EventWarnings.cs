using System.Globalization;
using System.Text.Json;
using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Un avís de l'editor, amb els dies en què passa (buit si no depèn del dia).</summary>
public sealed record EventWarning(string Text, IReadOnlyList<DateOnly> Dates);

/// <summary>Durada i estat d'una pista, per als càlculs dels avisos.</summary>
/// <param name="Title">Com es diu (per als textos).</param>
/// <param name="DurationMs">Quant sona (de l'inici al final), o null si no se sap.</param>
/// <param name="Missing">La pista no existeix o el fitxer falta.</param>
/// <param name="ValidFrom">Data d'inici de la pista (abans no sona; D-110).</param>
/// <param name="ValidUntil">Data de fi de la pista (després no sona; D-110).</param>
public sealed record TrackFacts(string Title, long? DurationMs, bool Missing, DateOnly? ValidFrom = null, DateOnly? ValidUntil = null)
{
    public bool ValidOn(DateOnly date) => (ValidFrom is not { } from || date >= from) && (ValidUntil is not { } until || date <= until);
}

/// <summary>
/// Avisos de l'editor en desar (fase 2, pas 8c; D-021, D-028; docs/planificador.md, 2.1): qui programa ha de saber què
/// passarà abans que passi. Càlcul pur: les durades de les pistes les dona qui ho crida.
///
/// Es fa una simulació senzilla dels pròxims dies amb tots els TALLA i ENCUA actius (i l'esdeveniment que s'edita, encara
/// que estigui desactivat): cada TALLA comença a l'hora; cada ENCUA comença quan s'acaba el contingut d'abans (sense
/// comptar la cançó de l'AutoDJ que soni, que no se sap). Amb això:
/// - un TALLA el contingut del qual dura més que fins al pròxim TALLA: se'n tallarà un tros;
/// - un ENCUA que no arribarà a sonar perquè, quan li tocaria, un esdeveniment posterior ja haurà esborrat la cua;
/// - un ENCUA que començarà però el tallarà un TALLA;
/// - contingut més llarg que la durada màxima de l'esdeveniment (D-028);
/// - emissions que no sonaran pel canvi d'hora (D-057);
/// - pistes que no existeixen o amb el fitxer que falta, i pistes de durada desconeguda;
/// - pistes que no sonaran alguns dies perquè són fora de les seves dates d'inici i de fi (D-110; fins a un any vista).
/// Només es diuen els avisos en què hi intervé l'esdeveniment que s'edita (com a afectat o com a causa).
/// </summary>
public static class EventWarnings
{
    /// <summary>Quants dies es miren: dues setmanes cobreixen els setmanals i els de cada 2 setmanes.</summary>
    public const int Days = 14;

    private sealed class Slot(Occurrence occurrence, long durationMs)
    {
        public Occurrence Occurrence { get; } = occurrence;
        public ScheduledEvent Event => Occurrence.Event;
        public long DurationMs { get; } = durationMs;
        public DateTimeOffset Start { get; set; }
        public DateTimeOffset End { get; set; }
    }

    /// <param name="subject">L'esdeveniment que s'edita (si és nou, amb id 0).</param>
    /// <param name="all">Tots els esdeveniments desats (si hi ha la versió desada de <paramref name="subject"/>, es fa servir la nova).</param>
    /// <param name="track">Les dades d'una pista pel seu id (null si no existeix).</param>
    public static IReadOnlyList<EventWarning> For(ScheduledEvent subject, IEnumerable<ScheduledEvent> all, Func<long, TrackFacts?> track,
        TimeZoneInfo zone, DateTimeOffset now)
    {
        var warnings = new List<(string Text, DateOnly? Date)>();
        var to = now.AddDays(Days);
        // La simulació comença un dia abans: un programa que ja ha començat i encara sona també es pot tallar més tard.
        // Només s'avisa del que encara ha de passar (el tall, o el moment en què s'esborra la cua, a partir d'ara).
        var from = now.AddDays(-1);

        // Les pistes de l'esdeveniment: les que falten i les de durada desconeguda (no depenen del dia)
        foreach (long id in TrackIds(subject).Distinct())
        {
            var facts = track(id);
            if (facts is null) warnings.Add(($"La pista {id} no existeix a la biblioteca: no sonarà.", null));
            else if (facts.Missing) warnings.Add(($"«{facts.Title}» no sonarà en aquest ordinador: el fitxer falta.", null));
            else if (facts.DurationMs is null && subject.Mode is EventMode.Cut or EventMode.Queue)
                warnings.Add(($"No se sap quant dura «{facts.Title}»: els càlculs de sota no la compten.", null));
        }

        // La simulació: TALLA i ENCUA dels pròxims dies, en ordre
        var events = all.Where(e => e.Id != subject.Id && e.Enabled && !e.IsTemplate).Append(subject).ToList();
        var occurrences = events
            .Where(e => e.Mode is EventMode.Cut or EventMode.Queue)
            .SelectMany(e => Recurrences.Between(e, from, to, zone))
            .Where(o => !o.SkippedClockChange)
            .OrderBy(o => o.At).ThenByDescending(o => o.Event.Priority).ThenBy(o => o.Event.Id)
            .ToList();

        var slots = new List<Slot>();
        DateTimeOffset? lastAt = null;
        var queueEnd = from;
        foreach (var o in occurrences)
        {
            if (lastAt == o.At) continue; // dos al mateix instant: només sona el de prioritat més alta (el planificador fa igual)
            lastAt = o.At;
            var slot = new Slot(o, ContentMs(o, track));
            if (o.Event.Mode == EventMode.Cut)
            {
                slot.Start = o.At;
                slot.End = o.At.AddMilliseconds(slot.DurationMs);
                // Sense «Esborra la cua», el que esperava a la cua sona després del contingut del TALLA
                if (!o.Event.ClearQueue && queueEnd > o.At) slot.End += queueEnd - o.At;
            }
            else
            {
                slot.Start = queueEnd > o.At ? queueEnd : o.At;
                slot.End = slot.Start.AddMilliseconds(slot.DurationMs);
            }
            queueEnd = slot.End;
            slots.Add(slot);
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.DurationMs == 0) continue;
            if (s.Event.Mode == EventMode.Cut)
            {
                // El pròxim TALLA el talla (sigui com sigui l'opció «Esborra la cua»)
                var next = slots.Skip(i + 1).FirstOrDefault(n => n.Event.Mode == EventMode.Cut);
                if (next is null || next.Occurrence.At >= s.Start.AddMilliseconds(s.DurationMs) || next.Occurrence.At < now) continue;
                if (!Involves(subject, s, next)) continue;
                var lost = s.Start.AddMilliseconds(s.DurationMs) - next.Occurrence.At;
                warnings.Add(($"«{s.Event.Name}» ({Time(s)}) dura {Duration(s.DurationMs)}: «{next.Event.Name}» el tallarà a les " +
                    $"{Time(next)} i se'n perdran {Duration((long)lost.TotalMilliseconds)}.", s.Occurrence.Date));
            }
            else
            {
                // El pròxim que esborra la cua (un TALLA o un ENCUA amb «Esborra la cua»), o un TALLA que el talla
                var clears = slots.Skip(i + 1).FirstOrDefault(n => n.Event.ClearQueue || n.Event.Mode == EventMode.Cut);
                if (clears is null || clears.Occurrence.At < now || !Involves(subject, s, clears)) continue;
                var at = clears.Occurrence.At;
                if (at <= s.Start && clears.Event.ClearQueue)
                {
                    warnings.Add(($"«{s.Event.Name}» ({Time(s)}) no arribarà a sonar: el contingut d'abans s'acaba a les " +
                        $"{Local(s.Start, zone)} i «{clears.Event.Name}» esborra la cua a les {Time(clears)}.", s.Occurrence.Date));
                }
                else if (at < s.End && clears.Event.Mode == EventMode.Cut)
                {
                    warnings.Add(($"«{s.Event.Name}» ({Time(s)}) començarà a les {Local(s.Start, zone)} i «{clears.Event.Name}» el tallarà " +
                        $"a les {Time(clears)}.", s.Occurrence.Date));
                }
            }
        }

        // Contingut més llarg que la durada màxima (D-028)
        if (subject.MaxDurationSeconds is { } max && subject.Mode is EventMode.Cut or EventMode.Queue)
        {
            foreach (var s in slots.Where(s => ReferenceEquals(s.Event, subject) && s.Occurrence.At >= now && s.DurationMs > max * 1000L))
            {
                warnings.Add(($"El contingut dura {Duration(s.DurationMs)} i la durada màxima de l'esdeveniment és " +
                    $"{Duration(max * 1000L)}.", s.Occurrence.Date));
            }
        }

        // Pistes fora de les seves dates els dies que l'esdeveniment sona (fins a un any vista; D-110)
        if (subject.Actions.Any(a => a.Type is EventActionType.Track or EventActionType.Auxiliary))
        {
            foreach (var date in Recurrences.Between(subject, now, now.AddDays(400), zone).Select(o => o.Date).Distinct())
            {
                foreach (var action in Recurrences.ActionsFor(subject, date).Where(a => a.Type is EventActionType.Track or EventActionType.Auxiliary))
                {
                    if (SongId(action) is not { } id || track(id) is not { } facts || facts.ValidOn(date)) continue;
                    warnings.Add((facts.ValidUntil is { } until && date > until
                        ? $"«{facts.Title}» no sonarà: la seva data de fi és el {Day(until)}."
                        : $"«{facts.Title}» no sonarà: la seva data d'inici és el {Day(facts.ValidFrom)}.", date));
                }
            }
        }

        // Emissions que no sonaran pel canvi d'hora (fins a un any vista)
        foreach (var o in Recurrences.Between(subject, now, now.AddDays(400), zone).Where(o => o.SkippedClockChange))
        {
            warnings.Add(($"Les {o.LocalTime:HH\\:mm\\:ss} no existeixen pel canvi d'hora: aquell dia no sonarà.", o.Date));
        }

        // Els mateixos avisos de dies diferents van junts
        return warnings
            .GroupBy(w => w.Text)
            .Select(g => new EventWarning(g.Key, g.Where(w => w.Date is not null).Select(w => w.Date!.Value).Distinct().Order().ToList()))
            .ToList();
    }

    /// <summary>Si l'esdeveniment que s'edita hi intervé (com a afectat o com a causa).</summary>
    private static bool Involves(ScheduledEvent subject, Slot affected, Slot cause) =>
        ReferenceEquals(affected.Event, subject) || ReferenceEquals(cause.Event, subject);

    /// <summary>Quant dura el contingut d'una emissió: les pistes (no els auxiliars, que sonen per sobre) i els streams.</summary>
    private static long ContentMs(Occurrence o, Func<long, TrackFacts?> track)
    {
        long total = 0;
        foreach (var action in Recurrences.ActionsFor(o.Event, o.Date))
        {
            // També les pistes amb el fitxer que falta: és per planificar (a l'ordinador d'emissió hi seran), i el fitxer
            // que falta ja té el seu propi avís
            if (action.Type == EventActionType.Track && SongId(action) is { } id && track(id) is { DurationMs: { } ms } facts
                && facts.ValidOn(o.Date)) // fora de les seves dates, no sona (D-110)
                total += ms;
            if (action.Type == EventActionType.Stream && Long(action, "durada_seg") is { } seconds)
                total += seconds * 1000;
        }
        return total;
    }

    private static IEnumerable<long> TrackIds(ScheduledEvent ev) =>
        ev.Actions.Where(a => a.Type is EventActionType.Track or EventActionType.Auxiliary).Select(SongId).OfType<long>();

    private static long? SongId(EventAction action) => Long(action, "songId");

    private static long? Long(EventAction action, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            return json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Time(Slot s) => s.Occurrence.LocalTime.ToString(s.Occurrence.LocalTime.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return local.ToString(local.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>«30/11/2026».</summary>
    private static string Day(DateOnly? date) => date?.ToString("d/M/yyyy", CultureInfo.InvariantCulture) ?? "";

    /// <summary>«61:20», «1:05:00», «45 s».</summary>
    private static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(ms / 1000.0) * 1000);
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds} s";
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
