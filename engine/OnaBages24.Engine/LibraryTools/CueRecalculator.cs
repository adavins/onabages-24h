using OnaBages.Audio;
using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>
/// Torna a calcular en segon pla els punts d'encadenament amb el nostre analitzador (D-042), perquè el que sona i
/// els punts surtin del mateix descodificador (els de RadioDJ estan desplaçats fins a 39 ms, D-041).
/// - Una pista a la vegada, amb un fil de prioritat baixa i una pausa entre pistes: l'emissió va primer.
/// - Després de cada pista descansa el triple del que ha tardat a analitzar-la (D-069): així no ocupa un nucli sencer
///   durant hores (es queda en una quarta part), a canvi de tardar més, cosa que no importa perquè es fa una vegada per pista.
/// - Mentre una pista no s'ha recalculat, es fan servir els punts de RadioDJ. Els manuals no es toquen mai.
/// - Si el fitxer no es pot llegir, es deixa com estava i es torna a provar a la volta següent.
/// - Quan ha acabat totes, espera una estona i torna a mirar si n'hi ha de noves.
/// </summary>
public sealed class CueRecalculator : IDisposable
{
    private readonly Library _library;
    private readonly CueThresholds _thresholds;
    private readonly TimeSpan _pause;
    private readonly TimeSpan _idleWait;
    private readonly double _restFactor;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private int _done, _failed;

    /// <param name="pause">Pausa entre pistes (per no carregar el disc).</param>
    /// <param name="idleWait">Espera quan no queda cap pista per analitzar.</param>
    /// <param name="restFactor">Descans després de cada pista, en vegades el temps que ha tardat l'anàlisi (3 = treballa una quarta part del temps; 0 = sense descans).</param>
    public CueRecalculator(Library library, CueThresholds? thresholds = null, TimeSpan? pause = null, TimeSpan? idleWait = null, Action<string>? log = null,
        double restFactor = 3)
    {
        _restFactor = Math.Max(0, restFactor);
        _library = library;
        _thresholds = thresholds ?? CueThresholds.Default;
        _pause = pause ?? TimeSpan.FromMilliseconds(200);
        _idleWait = idleWait ?? TimeSpan.FromMinutes(10);
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Recàlcul de punts", Priority = ThreadPriority.Lowest };
    }

    /// <summary>Pistes recalculades des que ha arrencat.</summary>
    public int Done => Volatile.Read(ref _done);

    /// <summary>Pistes que no s'han pogut llegir (es tornaran a provar).</summary>
    public int Failed => Volatile.Read(ref _failed);

    public void Start() => _thread.Start();

    /// <summary>Analitza i desa una pista. Retorna false si no s'ha pogut llegir. Pública per a les proves.</summary>
    public bool Recalculate(Track track, CancellationToken cancel = default)
    {
        try
        {
            var cues = CueAnalyzer.AnalyzeFile(track.Path, _thresholds, cancel);
            // Es torna a llegir just abans de desar: si mentrestant s'ha editat a mà, no es trepitja.
            var latest = _library.GetTrack(track.Id);
            if (latest is null || latest.CueSource == CueSource.Manual) return true;
            _library.UpdateTrack(latest with
            {
                StartMs = cues is null ? null : (long)Math.Round(cues.Start.TotalMilliseconds),
                MixMs = cues is null ? null : (long)Math.Round(cues.Mix.TotalMilliseconds),
                EndMs = cues is null ? null : (long)Math.Round(cues.End.TotalMilliseconds),
                CueSource = CueSource.Analyzer, // també si és tota silenci: no cal tornar-la a analitzar
            });
            Interlocked.Increment(ref _done);
            return true;
        }
        catch (Exception ex) when (ex is AudioFileException or IOException or UnauthorizedAccessException or DatabaseException)
        {
            Interlocked.Increment(ref _failed);
            return false;
        }
    }

    private void Run()
    {
        var token = _stop.Token;
        long afterId = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var batch = _library.TracksNeedingCueAnalysis(afterId, 50);
                if (batch.Count == 0)
                {
                    if (afterId > 0) _log?.Invoke($"Recàlcul de punts: volta acabada ({Done} pistes recalculades, {Failed} no s'han pogut llegir).");
                    afterId = 0; // a la volta següent, des del principi (pistes noves o que abans no es podien llegir)
                    token.WaitHandle.WaitOne(_idleWait);
                    continue;
                }
                foreach (var track in batch)
                {
                    if (token.IsCancellationRequested) return;
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    Recalculate(track, token);
                    afterId = track.Id;
                    token.WaitHandle.WaitOne(RestAfter(System.Diagnostics.Stopwatch.GetElapsedTime(started)));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Un error inesperat no pot afectar l'emissió: s'atura el recàlcul i queda al registre.
            _log?.Invoke($"Recàlcul de punts aturat per un error: {ex.Message}");
        }
    }

    /// <summary>Quant es descansa després d'una anàlisi que ha tardat <paramref name="elapsed"/>: com a mínim, la pausa entre pistes.</summary>
    public TimeSpan RestAfter(TimeSpan elapsed)
    {
        var rest = elapsed * _restFactor;
        return rest > _pause ? rest : _pause;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }
}
