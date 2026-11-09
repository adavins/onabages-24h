using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Resultat d'importar una carpeta.</summary>
public sealed class FolderImportReport
{
    public int Added { get; set; }
    public int AlreadyThere { get; set; }
    public List<string> Unreadable { get; } = [];
    public int WithoutSound { get; set; }

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Pistes noves: {Added} · ja hi eren: {AlreadyThere} · no es poden llegir: {Unreadable.Count} · sense so: {WithoutSound}",
        };
        if (Unreadable.Count > 0) lines.Add("No es poden llegir:\n  " + string.Join("\n  ", Unreadable));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Importació d'una carpeta de música (versió senzilla de la fase 1, pas 5b; la completa, amb carpetes vigilades,
/// és a la fase 3). Per a cada fitxer d'àudio que encara no és a la biblioteca: llegeix les etiquetes i la durada,
/// en calcula els punts d'encadenament (<see cref="CueAnalyzer"/>) i el desa a la subcategoria indicada.
/// Els fitxers no es copien mai: només se'n desa la ruta.
/// </summary>
public static class FolderImporter
{
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".wma"];

    /// <param name="parallelism">Quants fitxers s'analitzen alhora (l'anàlisi descodifica el fitxer sencer).</param>
    public static FolderImportReport Import(
        Library library, string folder, long subcategoryId, TrackType type = TrackType.Music,
        CueThresholds? thresholds = null, int parallelism = 4, Action<string>? progress = null, CancellationToken cancel = default)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"No es troba la carpeta: {folder}");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var report = new FolderImportReport();
        var sync = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancel }, path =>
        {
            if (library.FindByPath(path) is not null)
            {
                lock (sync) report.AlreadyThere++;
                return;
            }

            try
            {
                var metadata = TrackInfo.ReadMetadata(path);
                var cues = CueAnalyzer.AnalyzeFile(path, thresholds, cancel);
                var (artist, title) = NamesFor(path, metadata.Artist, metadata.Title);
                var info = new FileInfo(path);

                library.AddTrack(new Track
                {
                    Path = path,
                    Type = type,
                    SubcategoryId = subcategoryId,
                    Artist = artist,
                    Title = title,
                    Album = metadata.Album ?? "",
                    Year = metadata.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    DurationMs = (long)metadata.Duration.TotalMilliseconds,
                    StartMs = Ms(cues?.Start),
                    MixMs = Ms(cues?.Mix),
                    EndMs = Ms(cues?.End),
                    CueSource = cues is null ? null : CueSource.Analyzer,
                    FileSize = info.Length,
                    FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                });

                lock (sync)
                {
                    report.Added++;
                    if (cues is null) report.WithoutSound++;
                }
                progress?.Invoke(path);
            }
            catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException or DatabaseException)
            {
                // Un fitxer dolent no atura la importació dels altres
                lock (sync) report.Unreadable.Add($"{path}: {ex.Message}");
            }
        });

        return report;
    }

    /// <summary>
    /// Artista i títol: els de les etiquetes; si no n'hi ha, del nom del fitxer «Artista - Títol.mp3»
    /// (com estan anomenades les bosses de l'emissora); si no, el nom del fitxer com a títol.
    /// </summary>
    internal static (string Artist, string Title) NamesFor(string path, string? tagArtist, string? tagTitle)
    {
        if (!string.IsNullOrWhiteSpace(tagTitle)) return (tagArtist?.Trim() ?? "", tagTitle.Trim());

        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? (name[..dash].Trim(), name[(dash + 3)..].Trim()) : (tagArtist?.Trim() ?? "", name.Trim());
    }

    private static long? Ms(TimeSpan? time) => time is { } t ? (long)Math.Round(t.TotalMilliseconds) : null;
}
