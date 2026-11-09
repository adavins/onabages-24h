using System.Text;
using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Diferència entre els punts de RadioDJ i els de l'analitzador per a una pista (en mil·lisegons; analitzador − RadioDJ).</summary>
public sealed record CueDifference(Track Track, long StartDiff, long MixDiff, long EndDiff);

/// <summary>
/// Compara els punts d'encadenament importats de RadioDJ amb els que calcula el nostre analitzador (fase 1, pas 5),
/// per comprovar que la biblioteca sonarà igual que ara (D-006). No modifica la base de dades.
/// </summary>
public static class CueComparison
{
    /// <summary>Analitza les pistes d'una carpeta que tenen punts de RadioDJ i el fitxer disponible.</summary>
    public static IReadOnlyList<CueDifference> Compare(
        IEnumerable<Track> tracks, CueThresholds? thresholds = null, int parallelism = 4, CancellationToken cancel = default)
    {
        var candidates = tracks
            .Where(t => t.CueSource == CueSource.RadioDj && t.FileState == FileState.Ok && t.MixMs is not null && File.Exists(t.Path))
            .ToList();
        var results = new List<CueDifference>();
        var sync = new object();

        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancel }, track =>
        {
            try
            {
                if (CueAnalyzer.AnalyzeFile(track.Path, thresholds, cancel) is not { } cues) return;
                var diff = new CueDifference(track,
                    (long)cues.Start.TotalMilliseconds - track.StartMs!.Value,
                    (long)cues.Mix.TotalMilliseconds - track.MixMs!.Value,
                    (long)cues.End.TotalMilliseconds - track.EndMs!.Value);
                lock (sync) results.Add(diff);
            }
            catch (AudioFileException) { /* un fitxer dolent no compta */ }
        });

        return results.OrderBy(r => r.Track.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resum: mediana, percentil 90 i màxim de cada diferència, i les pistes amb més diferència al punt de mescla.</summary>
    public static string Summary(IReadOnlyList<CueDifference> results, int worst = 10)
    {
        if (results.Count == 0) return "Cap pista per comparar (cal que tinguin punts de RadioDJ i el fitxer disponible).";

        var text = new StringBuilder();
        text.AppendLine($"Pistes comparades: {results.Count}");
        text.AppendLine("Diferència (analitzador − RadioDJ), en valor absolut:");
        text.AppendLine(Line("  Inici ", results.Select(r => r.StartDiff)));
        text.AppendLine(Line("  Mescla", results.Select(r => r.MixDiff)));
        text.AppendLine(Line("  Final ", results.Select(r => r.EndDiff)));
        text.AppendLine($"Les {Math.Min(worst, results.Count)} amb més diferència al punt de mescla (ms: inici / mescla / final):");
        foreach (var r in results.OrderByDescending(r => Math.Abs(r.MixDiff)).Take(worst))
            text.AppendLine($"  {r.StartDiff,7} / {r.MixDiff,7} / {r.EndDiff,7}   {Path.GetFileName(r.Track.Path)}");
        return text.ToString();
    }

    private static string Line(string label, IEnumerable<long> diffs)
    {
        var sorted = diffs.Select(Math.Abs).Order().ToArray();
        long Percentile(double p) => sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(p * sorted.Length))];
        int within100 = sorted.Count(d => d <= 100);
        return $"{label}: mediana {Percentile(0.5)} ms · 90% fins a {Percentile(0.9)} ms · màxim {sorted[^1]} ms · " +
               $"a menys de 100 ms: {100.0 * within100 / sorted.Length:0}%";
    }
}
