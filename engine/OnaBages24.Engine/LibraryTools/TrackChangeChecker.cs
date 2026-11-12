using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>
/// Pistes de durada variable (fase 4, pas 1; D-012, planificador.md 11): els programes, les promos i les falques se
/// substitueixen sense canviar la programació (la pista és una ruta fixa). Cada pocs segons, per a cada pista de la cua que
/// encara no sona, es mira si el fitxer ha canviat (mida i data). Si ha canviat, se'n torna a llegir la durada i els punts
/// i la cua els fa servir: així un programa nou més llarg no es talla al final de l'antic, i les hores previstes quadren.
/// - Un fitxer que s'està escrivint (modificat fa menys de <see cref="SettleTime"/>, o que no es pot obrir) no es toca fins
///   que acaba: mentrestant, la cua conserva els punts de l'última versió bona.
/// - Si la pista ja s'està preparant per sonar, es torna a preparar amb el fitxer nou només si hi ha temps
///   (<see cref="QueuePlayer.RefreshTrack"/>); si no, sona la versió que ja tenia a punt.
/// - Els punts manuals no es toquen (només la durada).
/// - També aplica a la cua els punts que han canviat a la biblioteca per una altra via (editor de punts, carpeta vigilada)
///   i analitza de seguida les pistes de la cua que encara no en tenen.
/// Treballa en un fil propi de prioritat baixa: mai al fil d'àudio ni al de control.
/// </summary>
public sealed class TrackChangeChecker : IDisposable
{
    /// <summary>Un fitxer modificat fa menys d'això encara es pot estar escrivint.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    private readonly Library _library;
    private readonly QueuePlayer _queue;
    private readonly CueThresholds _thresholds;
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    /// <summary>Els fitxers que no s'han pogut llegir (per no repetir l'avís a cada volta): ruta → data de modificació.</summary>
    private readonly Dictionary<string, DateTimeOffset> _unreadable = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="interval">Cada quant es repassa la cua (per defecte, 5 s).</param>
    /// <param name="clock">Rellotge (les proves en fan servir un de fals).</param>
    public TrackChangeChecker(Library library, QueuePlayer queue, CueThresholds? thresholds = null, TimeSpan? interval = null,
        Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        _library = library;
        _queue = queue;
        _thresholds = thresholds ?? CueThresholds.Default;
        _interval = interval ?? TimeSpan.FromSeconds(5);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pistes de durada variable", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Una pista de la biblioteca ha canviat (el fitxer o els punts): per avisar les interfícies.</summary>
    public event Action<long>? TrackUpdated;

    public void Start() => _thread.Start();

    /// <summary>Repassa la cua una vegada. Retorna quantes pistes de la biblioteca ha actualitzat. Pública per a les proves.</summary>
    public int CheckNow(CancellationToken cancel = default)
    {
        int updated = 0;
        var trackIds = _queue.Upcoming.Select(i => i.TrackId).OfType<long>().Distinct().ToList();
        foreach (long id in trackIds)
        {
            cancel.ThrowIfCancellationRequested();
            if (_library.GetTrack(id) is not { FileState: FileState.Ok } track) continue;
            if (Check(track, cancel) is { } fresh)
            {
                updated++;
                track = fresh;
                TrackUpdated?.Invoke(id);
            }
            // Les pistes de la cua amb punts o durada diferents dels de la biblioteca els prenen (només si n'hi ha)
            _queue.RefreshTrack(track);
        }
        return updated;
    }

    /// <summary>Si el fitxer ha canviat (o la pista no té punts), la torna a llegir i la desa. Retorna la pista nova, o null.</summary>
    private Track? Check(Track track, CancellationToken cancel)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(track.Path);
            if (!info.Exists) return null; // falta: en preparar-la se saltarà amb un avís (mai silenci)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        bool changed = track.FileSize is { } size && track.FileModified is { } before
            && (info.Length != size || Math.Abs((modified - before).TotalSeconds) > 2);
        bool needsCues = track.CueSource is null;
        if (!changed && !needsCues)
        {
            // Pista d'abans (p. ex. de RadioDJ) sense mida ni data: se'n pren nota per detectar els canvis d'ara endavant
            if (track.FileSize is null || track.FileModified is null)
                _library.UpdateTrack(track with { FileSize = info.Length, FileModified = modified });
            return null;
        }
        if (_clock() - modified < SettleTime) return null; // s'està escrivint: a la volta següent

        TimeSpan duration;
        TrackCuePoints? cues;
        try
        {
            duration = TrackInfo.Probe(track.Path).Duration; // l'obre amb el mateix descodificador que el reproductor
            cues = CueAnalyzer.AnalyzeFile(track.Path, _thresholds, cancel);
        }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException)
        {
            // No es pot llegir (a mig copiar, bloquejat…): la cua es queda amb l'última versió bona; s'avisa una vegada
            if (!_unreadable.TryGetValue(track.Path, out var seen) || seen != modified)
            {
                _unreadable[track.Path] = modified;
                _log?.Invoke($"No es pot llegir el fitxer nou de «{Name(track)}» ({ex.Message}): es fa servir l'última versió bona fins que es pugui.");
            }
            return null;
        }
        _unreadable.Remove(track.Path);

        // Es torna a llegir just abans de desar: si mentrestant s'ha editat a mà, no es trepitja
        var latest = _library.GetTrack(track.Id);
        if (latest is null) return null;
        bool manual = latest.CueSource == CueSource.Manual;
        var fresh = latest with
        {
            FileSize = info.Length,
            FileModified = modified,
            DurationMs = (long)Math.Round(duration.TotalMilliseconds),
            StartMs = manual ? latest.StartMs : cues is null ? null : (long)Math.Round(cues.Start.TotalMilliseconds),
            MixMs = manual ? latest.MixMs : cues is null ? null : (long)Math.Round(cues.Mix.TotalMilliseconds),
            EndMs = manual ? latest.EndMs : cues is null ? null : (long)Math.Round(cues.End.TotalMilliseconds),
            CueSource = manual ? CueSource.Manual : CueSource.Analyzer,
        };
        _library.UpdateTrack(fresh);
        if (changed)
            _log?.Invoke($"S'ha substituït el fitxer de «{Name(track)}»: durada {Format(track.DurationMs)} → {Format(fresh.DurationMs)}" +
                         (manual ? " (punts manuals: no es toquen)" : ", punts recalculats"));
        return fresh;
    }

    private static string Name(Track t) => string.IsNullOrEmpty(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";

    private static string Format(long? ms) => ms is { } v ? TimeSpan.FromMilliseconds(v).ToString(v >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss") : "?";

    private void Run()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                CheckNow(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Un error inesperat no pot afectar l'emissió: queda al registre i es torna a provar a la volta següent
                _log?.Invoke($"Pistes de durada variable: error en revisar la cua ({ex.Message})");
            }
            token.WaitHandle.WaitOne(_interval);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }
}
