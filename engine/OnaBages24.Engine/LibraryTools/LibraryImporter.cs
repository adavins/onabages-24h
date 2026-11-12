using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Estat d'una importació des de la pantalla (fase 3, pas 7b).</summary>
public enum ImportState
{
    /// <summary>Buscant els fitxers de la carpeta (encara no se sap quants n'hi ha).</summary>
    Listing,
    Running,
    Done,
    Cancelled,
    /// <summary>No s'ha pogut fer (p. ex. la carpeta no existeix).</summary>
    Failed,
}

/// <summary>Com va (o com ha anat) una importació. Una foto: no canvia.</summary>
public sealed record ImportStatus(
    string Folder, string SubcategoryName, ImportState State, int Total, int Done,
    int Added, int AlreadyThere, int Reactivated, int WithoutSound, IReadOnlyList<string> Unreadable,
    DateTimeOffset Started, DateTimeOffset? Finished, string? Error)
{
    public bool Running => State is ImportState.Listing or ImportState.Running;

    /// <summary>S'ha demanat buidar la subcategoria (les pistes que no són a la carpeta).</summary>
    public bool EmptySubcategory { get; init; }

    /// <summary>En buidar-la: quantes s'han esborrat i quantes han quedat retirades (les fan servir esdeveniments).</summary>
    public int Removed { get; init; }
    public int RetiredInstead { get; init; }

    /// <summary>Un avís que no és un error (p. ex. «no s'ha buidat la subcategoria perquè…»), o null.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Importa una carpeta en segon pla, des de la pantalla «Biblioteca» (fase 3, pas 7b; D-088). Una sola importació alhora,
/// en un fil propi de prioritat baixa i un fitxer cada vegada: l'anàlisi dels punts descodifica el fitxer sencer i
/// l'emissió va primer (com el recàlcul de punts, D-069). Avisa del progrés unes 2 vegades per segon i en acabar.
/// Es pot aturar: el que ja s'ha importat es queda.
/// </summary>
public sealed class LibraryImporter : IDisposable
{
    /// <summary>Cada quant s'avisa del progrés, com a molt.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);

    private readonly Library _library;
    private readonly CueThresholds? _thresholds;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private ImportStatus? _status;
    private CancellationTokenSource? _cancel;
    private Thread? _thread;

    public LibraryImporter(Library library, CueThresholds? thresholds = null, Action<string>? log = null)
    {
        _library = library;
        _thresholds = thresholds;
        _log = log;
    }

    /// <summary>La importació en curs o l'última (null si encara no se n'ha fet cap des que ha arrencat el motor).</summary>
    public ImportStatus? Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Com va la importació (des del fil de la importació). L'últim avís és el del final.</summary>
    public event Action<ImportStatus>? Progress;

    /// <summary>
    /// Comença a importar <paramref name="folder"/> amb els valors per defecte. Torna de seguida; la feina es fa en segon pla.
    /// </summary>
    /// <exception cref="InvalidOperationException">Si ja n'hi ha una en curs.</exception>
    public ImportStatus Start(string folder, ImportDefaults defaults, string subcategoryName)
    {
        lock (_lock)
        {
            if (_status is { Running: true }) throw new InvalidOperationException("Ja s'està important una carpeta: espera que acabi o atura-la.");
            _cancel?.Dispose();
            _cancel = new CancellationTokenSource();
            _status = new ImportStatus(folder, subcategoryName, ImportState.Listing, 0, 0, 0, 0, 0, 0, [], DateTimeOffset.UtcNow, null, null)
            {
                EmptySubcategory = defaults.EmptySubcategory,
            };
            var token = _cancel.Token;
            _thread = new Thread(() => Run(folder, defaults, token)) { IsBackground = true, Name = "Importació de carpeta", Priority = ThreadPriority.Lowest };
            _thread.Start();
            return _status;
        }
    }

    /// <summary>Atura la importació en curs (el que ja s'ha importat es queda). Retorna false si no n'hi havia cap.</summary>
    public bool Cancel()
    {
        lock (_lock)
        {
            if (_status is not { Running: true }) return false;
            _cancel?.Cancel();
            return true;
        }
    }

    /// <summary>Espera que acabi la importació en curs (per a les proves). Retorna false si no ha acabat a temps.</summary>
    public bool Wait(TimeSpan timeout) => _thread?.Join(timeout) ?? true;

    private void Run(string folder, ImportDefaults defaults, CancellationToken cancel)
    {
        var report = new FolderImportReport();
        int total = 0, done = 0, removed = 0, retired = 0;
        string? warning = null;
        long lastProgress = 0;
        void Publish(ImportState state, string? error = null, bool force = false)
        {
            long now = Environment.TickCount64;
            if (!force && now - lastProgress < ProgressInterval.TotalMilliseconds) return;
            lastProgress = now;
            ImportStatus status;
            lock (_lock)
            {
                status = _status! with
                {
                    State = state, Total = total, Done = done, Added = report.Added, AlreadyThere = report.AlreadyThere,
                    Reactivated = report.Reactivated, WithoutSound = report.WithoutSound, Unreadable = report.Unreadable.ToArray(),
                    Finished = state is ImportState.Done or ImportState.Cancelled or ImportState.Failed ? DateTimeOffset.UtcNow : null,
                    Error = error,
                    Removed = removed,
                    RetiredInstead = retired,
                    Warning = warning,
                };
                _status = status;
            }
            try { Progress?.Invoke(status); }
            catch (Exception ex) { _log?.Invoke($"Importació: no s'ha pogut avisar del progrés ({ex.Message})"); }
        }

        try
        {
            _log?.Invoke($"Importació de {folder}: buscant fitxers");
            Publish(ImportState.Listing, force: true);
            var files = FolderImporter.ListAudioFiles(folder);
            total = files.Count;
            Publish(ImportState.Running, force: true);
            foreach (string path in files)
            {
                if (cancel.IsCancellationRequested) break;
                var (outcome, withoutSound, error) = FolderImporter.ImportFile(_library, path, defaults, _thresholds, cancel);
                FolderImporter.Count(report, path, outcome, withoutSound, error);
                done++;
                Publish(ImportState.Running);
            }
            var end = cancel.IsCancellationRequested ? ImportState.Cancelled : ImportState.Done;

            // Buidar la subcategoria (com RadioDJ): només si la importació ha acabat bé, perquè una importació aturada o
            // fallada no deixi la bossa sense música. Amb una carpeta sense àudio no es buida (seria esborrar-ho tot).
            if (defaults.EmptySubcategory && end == ImportState.Done)
            {
                if (files.Count == 0)
                    warning = "La carpeta no té cap fitxer d'àudio: no s'ha buidat la subcategoria.";
                else
                {
                    (removed, retired) = FolderImporter.RemoveOthers(_library, defaults.SubcategoryId, files);
                    _log?.Invoke($"Importació de {folder}: subcategoria buidada ({removed} pistes esborrades, {retired} retirades perquè les fan servir esdeveniments)");
                }
            }
            else if (defaults.EmptySubcategory)
                warning = "La importació no ha acabat: no s'ha buidat la subcategoria.";

            Publish(end, force: true);
            _log?.Invoke($"Importació de {folder} {(end == ImportState.Cancelled ? "aturada" : "acabada")}: {report.ToString().Split('\n')[0]}");
        }
        catch (OperationCanceledException)
        {
            Publish(ImportState.Cancelled, force: true);
        }
        catch (Exception ex)
        {
            // Un error inesperat no pot afectar l'emissió: la importació s'atura i ho diu
            Publish(ImportState.Failed, ex.Message, force: true);
            _log?.Invoke($"Importació de {folder} aturada per un error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _cancel?.Dispose();
    }
}
