using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Resultat d'importar una carpeta.</summary>
public sealed class FolderImportReport
{
    public int Added { get; set; }
    public int AlreadyThere { get; set; }
    /// <summary>Pistes que ja hi eren amb el fitxer que faltava (o retirades) i ara el tornen a tenir (fase 3, pas 7b).</summary>
    public int Reactivated { get; set; }
    public List<string> Unreadable { get; } = [];
    public int WithoutSound { get; set; }

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Pistes noves: {Added} · ja hi eren: {AlreadyThere} · recuperades: {Reactivated} · no es poden llegir: {Unreadable.Count} · sense so: {WithoutSound}",
        };
        if (Unreadable.Count > 0) lines.Add("No es poden llegir:\n  " + string.Join("\n  ", Unreadable));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Com queden les pistes noves d'una importació (fase 3, pas 7b): subcategoria, tipus, etiquetes i durada variable.
/// <paramref name="EmptySubcategory"/>: en acabar bé, les pistes de la subcategoria que no són a la carpeta s'esborren (com
/// «buidar la subcategoria» de RadioDJ; les que fan servir esdeveniments es queden com eren).
/// </summary>
public sealed record ImportDefaults(long SubcategoryId, TrackType Type = TrackType.Music, IReadOnlyList<string>? Tags = null,
    bool VariableDuration = false, bool EmptySubcategory = false);

/// <summary>Què ha passat amb un fitxer en importar-lo.</summary>
public enum ImportOutcome
{
    /// <summary>Pista nova.</summary>
    Added,
    /// <summary>Ja era a la biblioteca: no es toca.</summary>
    AlreadyThere,
    /// <summary>Ja hi era, amb el fitxer que faltava o retirada: torna a estar disponible (amb la mateixa fitxa).</summary>
    Reactivated,
    /// <summary>No es pot llegir (no és àudio, està malmès o bloquejat).</summary>
    Unreadable,
}

/// <summary>
/// Importació d'una carpeta de música (fase 1, pas 5b; des de la pantalla, fase 3, pas 7b). Per a cada fitxer d'àudio que
/// encara no és a la biblioteca: llegeix les etiquetes i la durada, en calcula els punts d'encadenament
/// (<see cref="CueAnalyzer"/>) i el desa amb els valors per defecte. Els fitxers no es copien mai: només se'n desa la ruta.
/// </summary>
public static class FolderImporter
{
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".wma"];

    private static readonly object RelinkLock = new();

    /// <summary>Els fitxers d'àudio de la carpeta i les subcarpetes, per ordre alfabètic.</summary>
    /// <exception cref="DirectoryNotFoundException">Si la carpeta no existeix.</exception>
    public static List<string> ListAudioFiles(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"No es troba la carpeta: {folder}");
        return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Importació de tota una carpeta d'una vegada (l'ordre <c>importa-carpeta</c>), amb diversos fitxers alhora.</summary>
    /// <param name="parallelism">Quants fitxers s'analitzen alhora (l'anàlisi descodifica el fitxer sencer).</param>
    public static FolderImportReport Import(
        Library library, string folder, long subcategoryId, TrackType type = TrackType.Music,
        CueThresholds? thresholds = null, int parallelism = 4, Action<string>? progress = null, CancellationToken cancel = default)
    {
        var files = ListAudioFiles(folder);
        var defaults = new ImportDefaults(subcategoryId, type);
        var report = new FolderImportReport();
        var sync = new object();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancel }, path =>
        {
            var (outcome, withoutSound, error) = ImportFile(library, path, defaults, thresholds, cancel);
            lock (sync) Count(report, path, outcome, withoutSound, error);
            if (outcome == ImportOutcome.Added) progress?.Invoke(path);
        });
        return report;
    }

    /// <summary>Suma el resultat d'un fitxer a l'informe.</summary>
    public static void Count(FolderImportReport report, string path, ImportOutcome outcome, bool withoutSound, string? error)
    {
        switch (outcome)
        {
            case ImportOutcome.Added: report.Added++; break;
            case ImportOutcome.AlreadyThere: report.AlreadyThere++; break;
            case ImportOutcome.Reactivated: report.Reactivated++; break;
            case ImportOutcome.Unreadable: report.Unreadable.Add($"{path}: {error}"); break;
        }
        if (withoutSound) report.WithoutSound++;
    }

    /// <summary>
    /// Importa un fitxer. Si ja és a la biblioteca, no es toca; si hi era amb el fitxer que faltava (o retirada), es torna a
    /// posar disponible amb la mateixa fitxa. Un fitxer dolent no fa caure res: torna <see cref="ImportOutcome.Unreadable"/>
    /// amb el motiu.
    /// </summary>
    /// <returns>Què ha passat, si la pista nova és tota silenci (sense punts) i, si no s'ha pogut llegir, per què.</returns>
    public static (ImportOutcome Outcome, bool WithoutSound, string? Error) ImportFile(
        Library library, string path, ImportDefaults defaults, CueThresholds? thresholds = null, CancellationToken cancel = default)
    {
        try
        {
            if (library.FindByPath(path) is { } existing)
            {
                if (existing.FileState == FileState.Ok) return (ImportOutcome.AlreadyThere, false, null);
                var found = new FileInfo(path);
                library.UpdateTrack(existing with
                {
                    FileState = FileState.Ok,
                    FileSize = found.Length,
                    FileModified = new DateTimeOffset(found.LastWriteTimeUtc, TimeSpan.Zero),
                });
                return (ImportOutcome.Reactivated, false, null);
            }

            var metadata = TrackInfo.ReadMetadata(path);

            // A la mateixa subcategoria hi ha una pista que falta amb el mateix nom de fitxer: és la mateixa cançó, que s'ha
            // mogut (o que en aquest ordinador es veu amb una altra ruta). Es recupera, amb la fitxa i l'historial, en lloc de
            // duplicar-la. Els punts es tornen a calcular en segon pla (excepte els manuals); mentrestant, els d'abans.
            // Amb un bloqueig: si s'importen dos fitxers alhora amb el mateix nom (de subcarpetes diferents), només un la recupera.
            lock (RelinkLock)
            {
                if (library.FindMissingByFileName(defaults.SubcategoryId, Path.GetFileName(path)) is { } moved)
                {
                    var found = new FileInfo(path);
                    library.UpdateTrack(moved with
                    {
                        Path = path,
                        FileState = FileState.Ok,
                        DurationMs = (long)metadata.Duration.TotalMilliseconds,
                        FileSize = found.Length,
                        FileModified = new DateTimeOffset(found.LastWriteTimeUtc, TimeSpan.Zero),
                        CueSource = moved.CueSource == CueSource.Manual ? CueSource.Manual : null,
                    });
                    return (ImportOutcome.Reactivated, false, null);
                }
            }

            var cues = CueAnalyzer.AnalyzeFile(path, thresholds, cancel);
            var (artist, title) = NamesFor(path, metadata.Artist, metadata.Title);
            var info = new FileInfo(path);

            // La pista i les etiquetes, juntes: si el motor s'atura a mig fer, o hi és tot o no hi és res
            library.AddTrack(new Track
            {
                Path = path,
                Type = defaults.Type,
                VariableDuration = defaults.VariableDuration,
                SubcategoryId = defaults.SubcategoryId,
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
            }, defaults.Tags);
            return (ImportOutcome.Added, cues is null, null);
        }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException or DatabaseException)
        {
            // Un fitxer dolent no atura la importació dels altres
            return (ImportOutcome.Unreadable, false, ex.Message);
        }
    }

    /// <summary>
    /// Buida una subcategoria (com RadioDJ): les pistes que no són a <paramref name="keepPaths"/> (p. ex. els fitxers de la
    /// carpeta importada; buit = totes) s'esborren de la biblioteca. Les que fa servir algun esdeveniment <b>no es toquen</b>
    /// (decisió del desenvolupador, D-089): es queden tal com eren. Els fitxers no es toquen mai.
    /// </summary>
    /// <returns>Quantes s'han esborrat i quantes s'han conservat perquè les fan servir esdeveniments.</returns>
    public static (int Removed, int KeptForEvents) RemoveOthers(Library library, long subcategoryId, IEnumerable<string> keepPaths)
    {
        var keep = new HashSet<string>(keepPaths, StringComparer.OrdinalIgnoreCase);
        int removed = 0, kept = 0;
        foreach (var track in library.TracksInSubcategory(subcategoryId))
        {
            if (keep.Contains(track.Path)) continue;
            if (library.EventsUsingTrack(track.Id).Count > 0)
            {
                kept++;
                continue;
            }
            library.DeleteTrack(track.Id);
            removed++;
        }
        return (removed, kept);
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
