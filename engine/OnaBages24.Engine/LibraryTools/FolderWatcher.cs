using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Com està una carpeta vigilada (per a la pantalla).</summary>
/// <param name="Files">Fitxers d'àudio que hi ha a la carpeta (a l'última revisió).</param>
/// <param name="Tracks">Pistes de la subcategoria que són d'aquesta carpeta.</param>
/// <param name="Missing">D'aquestes, quantes tenen el fitxer que falta.</param>
/// <param name="Problem">Si la carpeta no es pot revisar (no es troba, és buida…), per què; si no, null.</param>
public sealed record FolderWatchStatus(
    long SubcategoryId, string Name, string Folder, bool Renewable, DateTimeOffset? LastScan, int Files, int Tracks, int Missing, string? Problem);

/// <summary>
/// Carpetes vigilades (fase 3, pas 7c; D-026, D-033, D-090). Revisa les carpetes de les subcategories que en tenen cada
/// <see cref="DefaultInterval"/>, en arrencar i quan es demana (<see cref="Poke"/>). No fa servir els avisos de Windows
/// (FileSystemWatcher): amb Google Drive i les unitats de xarxa no són fiables; revisar una carpeta és mirar-ne la llista.
///
/// A cada revisió:
/// - **fitxer nou** (quan ja s'ha acabat de copiar: fa més de <see cref="SettleTime"/> que no canvia i es pot obrir):
///   s'importa amb els valors per defecte de la subcategoria (o es recupera una pista que falta amb el mateix nom, D-088);
/// - **fitxer que torna**: la pista es recupera amb la mateixa fitxa;
/// - **fitxer substituït** (canvia la mida o la data): es torna a llegir la durada i els punts es tornen a calcular;
/// - **fitxer que desapareix** (en dues revisions seguides, per si la carpeta s'està sincronitzant): en una bossa
///   renovable la pista s'esborra (decisió del desenvolupador, D-090), excepte si la fa servir un esdeveniment; si no, queda
///   com a «el fitxer falta» (avís). Les pistes no s'esborren mai si la carpeta no es troba o surt buida.
/// Tot queda al registre (registre_carpetes). Un fil de prioritat baixa: l'emissió va primer; un error no l'atura.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(2);

    /// <summary>Temps que ha de fer que un fitxer no canvia per considerar que s'ha acabat de copiar.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(30);

    private readonly Library _library;
    private readonly Func<bool> _busy;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly CueThresholds? _thresholds;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _poke = new(false);
    private readonly Thread _thread;
    private readonly object _lock = new();
    private IReadOnlyList<FolderWatchStatus> _status = [];

    /// <summary>Per subcategoria: els fitxers que faltaven a la revisió anterior (es confirma a la següent).</summary>
    private readonly Dictionary<long, HashSet<string>> _absent = [];

    /// <summary>Fitxers que no s'han pogut importar, amb la mida i la data: no es tornen a provar fins que canviïn.</summary>
    private readonly Dictionary<string, (long Size, DateTime Modified)> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per subcategoria: quantes revisions seguides fa que la carpeta no està disponible.</summary>
    private readonly Dictionary<long, int> _unavailableScans = [];

    /// <summary>Per subcategoria: l'últim problema anotat al registre (per no repetir-lo a cada revisió).</summary>
    private readonly Dictionary<long, string?> _lastProblem = [];

    /// <param name="busy">Si torna true (p. ex. s'està important una carpeta des de la pantalla), la revisió s'ajorna.</param>
    public FolderWatcher(Library library, Func<bool>? busy = null, TimeSpan? interval = null, Func<DateTimeOffset>? clock = null,
        Action<string>? log = null, CueThresholds? thresholds = null)
    {
        _library = library;
        _busy = busy ?? (() => false);
        _interval = interval ?? DefaultInterval;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _thresholds = thresholds;
        _thread = new Thread(Run) { IsBackground = true, Name = "Carpetes vigilades", Priority = ThreadPriority.Lowest };
    }

    /// <summary>Com estan les carpetes vigilades (a l'última revisió).</summary>
    public IReadOnlyList<FolderWatchStatus> Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>
    /// Després de cada revisió (des del fil de la vigilància): true si ha canviat alguna pista de la biblioteca. Per avisar les
    /// interfícies (l'estat de les carpetes i, si cal, la biblioteca).
    /// </summary>
    public event Action<bool>? Scanned;

    public void Start() => _thread.Start();

    /// <summary>Revisa les carpetes ara (p. ex. en canviar-ne la configuració o amb «Revisa ara»).</summary>
    public void Poke() => _poke.Set();

    private void Run()
    {
        var token = _stop.Token;
        // Primer, que l'emissió arrenqui tranquil·la
        if (WaitHandle.WaitAny([token.WaitHandle, _poke], TimeSpan.FromSeconds(10)) == 0) return;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_busy()) Scanned?.Invoke(ScanAll(token));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log?.Invoke($"Carpetes vigilades: error inesperat ({ex.Message}); es tornarà a provar");
            }
            if (WaitHandle.WaitAny([token.WaitHandle, _poke], _interval) == 0) return;
        }
    }

    /// <summary>Revisa totes les carpetes vigilades una vegada. Retorna true si ha canviat alguna pista. Pública per a les proves.</summary>
    public bool ScanAll(CancellationToken cancel = default)
    {
        bool changed = false;
        var status = new List<FolderWatchStatus>();
        foreach (var settings in _library.WatchedFolders())
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                var (one, oneChanged) = ScanOne(settings, cancel);
                status.Add(one);
                changed |= oneChanged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una carpeta amb problemes no atura les altres
                string problem = $"No s'ha pogut revisar: {ex.Message}";
                NoteProblem(settings, problem);
                status.Add(new FolderWatchStatus(settings.SubcategoryId, settings.Name, settings.Folder!, settings.Renewable, _clock(), 0, 0, 0, problem));
            }
        }
        lock (_lock) _status = status;
        return changed;
    }

    private (FolderWatchStatus Status, bool Changed) ScanOne(FolderSettings settings, CancellationToken cancel)
    {
        string folder = settings.Folder!;
        string prefix = folder.TrimEnd('\\', '/') + "\\";
        long sub = settings.SubcategoryId;
        var mine = _library.TracksInSubcategory(sub).Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        FolderWatchStatus Result(int files, string? problem) =>
            new(sub, settings.Name, folder, settings.Renewable, _clock(), files, mine.Count, mine.Count(t => t.FileState == FileState.Missing), problem);

        // Carpeta no disponible (no es troba, no s'hi pot accedir o surt buida amb pistes que hi eren: Drive desconnectat,
        // xarxa…). Mai no s'esborra res així. A la segona revisió seguida, les seves pistes queden com a «el fitxer falta»
        // (l'AutoDJ no les tria i no es poden posar a la cua); quan torni, es recuperen soles.
        (FolderWatchStatus, bool) Unavailable(string reason)
        {
            NoteProblem(settings, reason);
            int scans = _unavailableScans[sub] = _unavailableScans.GetValueOrDefault(sub) + 1;
            bool marked = false;
            if (scans >= 2)
            {
                var ok = mine.Where(t => t.FileState == FileState.Ok).ToList();
                foreach (var track in ok) _library.UpdateTrack(track with { FileState = FileState.Missing });
                if (ok.Count > 0)
                {
                    Log(settings, folder, FolderAction.Missing, $"{ok.Count} pistes marcades com que falten fins que torni la carpeta");
                    mine = _library.TracksInSubcategory(sub).Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                    marked = true;
                }
            }
            string state = scans >= 2
                ? "les seves pistes queden com que falta el fitxer fins que torni (no s'esborra res)."
                : "si continua així a la revisió següent, les seves pistes quedaran com que falta el fitxer (no s'esborra res).";
            return (Result(0, $"{reason}: {state}"), marked);
        }

        List<string> files;
        try
        {
            files = Directory.Exists(folder) ? FolderImporter.ListAudioFiles(folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable($"No s'hi pot accedir ({ex.Message})");
        }
        if (!Directory.Exists(folder)) return Unavailable("No es troba la carpeta");
        if (files.Count == 0 && mine.Any(t => t.FileState == FileState.Ok)) return Unavailable("La carpeta surt buida (potser no està disponible)");
        if (files.Count == 0 && mine.Count > 0 && _unavailableScans.GetValueOrDefault(sub) > 0) return Unavailable("La carpeta surt buida (potser no està disponible)");

        // Torna a estar disponible: les pistes que s'havien marcat es recuperen més avall (amb una sola línia al registre)
        bool cameBack = _unavailableScans.Remove(sub);
        NoteProblem(settings, null);
        int recovered = 0;

        bool changed = false;
        var known = _library.TracksInFolder(folder).ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        var defaults = new ImportDefaults(sub, settings.DefaultType, settings.DefaultTags, settings.DefaultVariableDuration);
        var now = _clock();

        foreach (string path in files)
        {
            cancel.ThrowIfCancellationRequested();
            FileInfo info;
            try { info = new FileInfo(path); if (!info.Exists) continue; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            if (known.TryGetValue(path, out var track))
            {
                if (track.SubcategoryId != sub) continue; // és d'una altra subcategoria: no es toca
                if (cameBack && track.FileState == FileState.Missing)
                {
                    _library.UpdateTrack(track with { FileState = FileState.Ok });
                    recovered++;
                    changed = true;
                    continue;
                }
                changed |= CheckKnown(settings, track, info, now);
                continue;
            }

            if (!Settled(info, now)) continue; // encara s'està copiant
            if (_failed.TryGetValue(path, out var failed) && failed == (info.Length, info.LastWriteTimeUtc)) continue;

            var (outcome, _, error) = FolderImporter.ImportFile(_library, path, defaults, _thresholds, cancel);
            switch (outcome)
            {
                case ImportOutcome.Added:
                    Log(settings, path, FolderAction.Imported);
                    changed = true;
                    break;
                case ImportOutcome.Reactivated:
                    Log(settings, path, FolderAction.Recovered, "pista que faltava amb el mateix nom de fitxer");
                    changed = true;
                    break;
                case ImportOutcome.Unreadable:
                    _failed[path] = (info.Length, info.LastWriteTimeUtc);
                    Log(settings, path, FolderAction.Error, error);
                    break;
            }
        }

        if (recovered > 0) Log(settings, folder, FolderAction.Recovered, $"la carpeta torna a ser-hi: {recovered} pistes recuperades");

        // Desaparicions: només si falten en dues revisions seguides
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var before = _absent.GetValueOrDefault(sub) ?? [];
        var absentNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in mine.Where(t => t.FileState == FileState.Ok && !present.Contains(t.Path)))
        {
            if (!before.Contains(track.Path))
            {
                absentNow.Add(track.Path);
                continue;
            }
            bool inEvents = _library.EventsUsingTrack(track.Id).Count > 0;
            if (settings.Renewable && !inEvents)
            {
                _library.DeleteTrack(track.Id);
                Log(settings, track.Path, FolderAction.Deleted, "ha sortit de la bossa renovable");
            }
            else
            {
                _library.UpdateTrack(track with { FileState = FileState.Missing });
                Log(settings, track.Path, FolderAction.Missing, inEvents ? "la fa servir un esdeveniment" : null);
            }
            changed = true;
        }
        _absent[sub] = absentNow;

        if (changed) mine = _library.TracksInSubcategory(sub).Where(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return (Result(files.Count, null), changed);
    }

    /// <summary>Una pista que ja és de la subcategoria: si torna a tenir el fitxer, es recupera; si el fitxer ha canviat, s'actualitza.</summary>
    private bool CheckKnown(FolderSettings settings, Track track, FileInfo info, DateTimeOffset now)
    {
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (track.FileState != FileState.Ok)
        {
            _library.UpdateTrack(track with { FileState = FileState.Ok, FileSize = info.Length, FileModified = modified });
            Log(settings, track.Path, FolderAction.Recovered, "el fitxer torna a ser-hi");
            return true;
        }
        if (track.FileSize is null || track.FileModified is null)
        {
            // Pista d'abans de vigilar la carpeta (p. ex. de RadioDJ): es pren nota de com és ara, sense més
            _library.UpdateTrack(track with { FileSize = info.Length, FileModified = modified });
            return false;
        }
        bool differs = info.Length != track.FileSize || Math.Abs((modified - track.FileModified.Value).TotalSeconds) > 2;
        if (!differs || !Settled(info, now)) return false;

        // Fitxer substituït (mateix nom): la mateixa pista; durada nova i punts recalculats en segon pla (els manuals no es toquen)
        long? duration = track.DurationMs;
        try { duration = (long)TrackInfo.ReadMetadata(track.Path).Duration.TotalMilliseconds; }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException) { }
        _library.UpdateTrack(track with
        {
            FileSize = info.Length,
            FileModified = modified,
            DurationMs = duration,
            CueSource = track.CueSource == CueSource.Manual ? CueSource.Manual : null,
        });
        Log(settings, track.Path, FolderAction.Updated, "el fitxer s'ha substituït");
        return true;
    }

    /// <summary>S'ha acabat de copiar: fa una estona que no canvia i es pot obrir (ningú no hi està escrivint).</summary>
    private static bool Settled(FileInfo info, DateTimeOffset now)
    {
        if (now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) < SettleTime) return false;
        try
        {
            using var stream = new FileStream(info.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Log(FolderSettings settings, string path, FolderAction action, string? detail = null)
    {
        try { _library.AddFolderLog(settings.SubcategoryId, settings.Name, path, action, detail); }
        catch (DatabaseException ex) { _log?.Invoke($"Carpetes vigilades: no s'ha pogut anotar al registre ({ex.Message})"); }
        _log?.Invoke($"Carpeta vigilada {settings.Name}: {action} {path}{(detail is null ? "" : $" ({detail})")}");
    }

    /// <summary>Anota al registre un problema de la carpeta (o que s'ha resolt), només quan canvia.</summary>
    private void NoteProblem(FolderSettings settings, string? problem)
    {
        _lastProblem.TryGetValue(settings.SubcategoryId, out var last);
        if (last == problem) return;
        _lastProblem[settings.SubcategoryId] = problem;
        if (problem is null && last is null) return;
        Log(settings, settings.Folder ?? "", FolderAction.Folder, problem ?? "la carpeta torna a estar disponible");
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
        _poke.Dispose();
    }
}
