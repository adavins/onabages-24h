using System.Globalization;
using System.Text.RegularExpressions;
using OnaBages24.Data;

namespace OnaBages24.Engine;

/// <summary>Un forat a l'emissió: entre el final previst d'una pista i l'inici de la següent no sonava res de la cua.</summary>
public sealed record BroadcastGap(DateTimeOffset From, TimeSpan Length, HistoryEntry? Before, HistoryEntry After);

/// <summary>Resultat de <see cref="BroadcastCheck.Analyze"/>.</summary>
public sealed class BroadcastReport
{
    public DateTimeOffset? First { get; init; }
    public DateTimeOffset? Last { get; init; }
    public int Tracks { get; init; }
    /// <summary>Temps amb alguna pista sonant (els encavalcaments dels encadenaments es compten una sola vegada).</summary>
    public TimeSpan Covered { get; init; }
    public List<BroadcastGap> Gaps { get; } = [];
    /// <summary>Pistes tancades en arrencar el motor: es va aturar de cop mentre sonaven i no se sap quant van sonar.</summary>
    public List<HistoryEntry> Unknown { get; } = [];
    /// <summary>La pista que encara sona (l'última, sense tancar), si n'hi ha.</summary>
    public HistoryEntry? Open { get; init; }
}

/// <summary>
/// Comprovació d'una prova llarga d'emissió (fase 1, pas 11): llegeix l'historial i busca forats entre pistes. És l'ordre
/// <c>comprova-emissio</c> del motor; només llegeix (la base de dades i els registres), no canvia res.
///
/// Com que l'historial es desa amb l'hora d'inici i quant ha sonat cada pista, el final d'una pista és l'inici més el que
/// ha sonat. Si la següent comença més tard que això (més del llindar), hi ha hagut un forat: silenci o, com a molt, el
/// so d'un reproductor auxiliar. Els encadenaments fan que la següent comenci abans: no és cap forat.
/// </summary>
public static class BroadcastCheck
{
    /// <param name="entries">L'historial, en ordre d'inici.</param>
    /// <param name="threshold">A partir de quan un forat es mostra (els d'uns quants mil·lisegons són de mesura).</param>
    public static BroadcastReport Analyze(IReadOnlyList<HistoryEntry> entries, TimeSpan threshold)
    {
        var ordered = entries.OrderBy(e => e.Started).ThenBy(e => e.Id).ToList();
        HistoryEntry? open = ordered.Count > 0 && ordered[^1].PlayedMs is null ? ordered[^1] : null;
        var report = new BroadcastReport
        {
            First = ordered.FirstOrDefault()?.Started,
            Last = ordered.LastOrDefault()?.Started,
            Tracks = ordered.Count,
            Open = open,
            Covered = Covered(ordered),
        };

        // El final més tardà fins ara (un encadenament llarg pot fer que una pista acabi després de l'inici de la següent)
        DateTimeOffset? end = null;
        HistoryEntry? previous = null;
        foreach (var entry in ordered)
        {
            if (end is { } e && previous is not null && entry.Started - e > threshold)
                report.Gaps.Add(new BroadcastGap(e, entry.Started - e, previous, entry));

            if (entry.PlayedMs == 0 && entry.Complete == false)
            {
                // Tancada en arrencar: no se sap fins quan va sonar; el forat fins a la següent no es pot mesurar
                report.Unknown.Add(entry);
                end = null;
                previous = null;
                continue;
            }
            var entryEnd = entry.Started + TimeSpan.FromMilliseconds(entry.PlayedMs ?? 0);
            end = end is { } current && current > entryEnd ? current : entryEnd;
            previous = entry;
        }
        return report;
    }

    /// <summary>Temps cobert per alguna pista (unió dels intervals).</summary>
    private static TimeSpan Covered(List<HistoryEntry> ordered)
    {
        var total = TimeSpan.Zero;
        DateTimeOffset? start = null, end = null;
        foreach (var entry in ordered)
        {
            if (entry.PlayedMs is not { } ms || ms <= 0) continue;
            var s = entry.Started;
            var e = s + TimeSpan.FromMilliseconds(ms);
            if (end is { } currentEnd && s <= currentEnd)
            {
                if (e > currentEnd) end = e;
                continue;
            }
            if (start is { } a && end is { } b) total += b - a;
            start = s;
            end = e;
        }
        if (start is { } x && end is { } y) total += y - x;
        return total;
    }

    // --- Registres del motor ---

    private static readonly Regex LogLine = new(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+ (\w+)\s+(.*)$", RegexOptions.Compiled);

    /// <summary>
    /// Del registre del motor (<c>dades\logs\motor-*.log</c>), entre dues hores: les arrencades i els avisos i errors,
    /// agrupats pel text (sense els números, perquè «pista 12» i «pista 13» compten com el mateix avís).
    /// </summary>
    public static (List<DateTime> Starts, List<(string Level, string Message, int Count, DateTime FirstAt)> Problems) ReadLogs(
        string logsFolder, DateTime from, DateTime to)
    {
        var starts = new List<DateTime>();
        var problems = new Dictionary<string, (string Level, string Message, int Count, DateTime FirstAt)>();
        if (!Directory.Exists(logsFolder)) return (starts, []);

        foreach (string file in Directory.GetFiles(logsFolder, "motor-*.log").Order())
        {
            IEnumerable<string> lines;
            try
            {
                // El motor pot estar escrivint-hi: s'obre sense bloquejar-lo
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n');
            }
            catch (IOException) { continue; }

            foreach (string raw in lines)
            {
                var m = LogLine.Match(raw.TrimEnd('\r'));
                if (!m.Success) continue;
                var at = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                if (at < from || at >= to) continue;
                string level = m.Groups[2].Value, message = m.Groups[3].Value;
                if (message.Contains(" arrencant (port", StringComparison.Ordinal)) starts.Add(at);
                if (level is not ("WARN" or "ERROR" or "FAIL" or "CRIT")) continue;
                string key = level + " " + Regex.Replace(message, @"\d+", "#");
                problems[key] = problems.TryGetValue(key, out var p) ? p with { Count = p.Count + 1 } : (level, message, 1, at);
            }
        }
        return (starts, problems.Values.OrderByDescending(p => p.Level == "WARN" ? 0 : 1).ThenByDescending(p => p.Count).ToList());
    }
}
