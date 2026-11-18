using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Supervision;

/// <summary>Un silenci a la sortida que dura (per a la incidència): el text i si és greu.</summary>
public sealed record SilenceProblem(string Text, IncidentSeverity Severity);

/// <summary>Una pista que sonava en silenci i s'ha passat a la següent.</summary>
public sealed record SilenceSkip(QueueItem Item, TimeSpan Position);

/// <summary>
/// Silenci a la sortida (fase 5, pas 3; D-158). Mesura el so que surt del mesclador (el pic, sense tocar els mesuradors de
/// la pantalla) i, si fa <see cref="Seconds"/> que és per sota de <see cref="ThresholdDb"/>:
/// - si sona una pista de la cua, es passa a la següent (incidència d'un moment, recuperada);
/// - si sona un stream (pot tenir pauses llargues, D-030), només avisa;
/// - si no sona res o ja s'han saltat <see cref="MaxSkips"/> pistes seguides i continua, avisa (incidència greu).
/// La pausa de la cua és un silenci volgut: no compta (ja té la seva incidència). Si el mesclador no avança (sortida
/// encallada), no es mesura res: d'això se n'ocupa <see cref="OutputManager"/>.
/// Fil de control (<see cref="Tick"/>, unes 4 vegades per segon).
/// </summary>
public sealed class SilenceDetector(Mixer mixer, QueuePlayer queue)
{
    public const double StandardThresholdDb = -50;
    public const double StandardSeconds = 15;

    /// <summary>Si ja s'han saltat aquestes pistes seguides i continua el silenci, no se'n salten més: el problema és un altre.</summary>
    public const int MaxSkips = 3;

    private static readonly TimeSpan SkipFade = TimeSpan.FromMilliseconds(200);

    private long _lastFrames = -1;
    private long? _silentSince;   // fotograma des del qual tot és silenci
    private int _skips;           // pistes saltades seguides sense que torni el so
    private volatile SilenceProblem? _problem;
    private double _thresholdDb = StandardThresholdDb;
    private double _seconds = StandardSeconds;

    /// <summary>Nivell per sota del qual és silenci (dBFS).</summary>
    public double ThresholdDb
    {
        get => Volatile.Read(ref _thresholdDb);
        set => Volatile.Write(ref _thresholdDb, value);
    }

    /// <summary>Quants segons de silenci seguits calen per fer res.</summary>
    public double Seconds
    {
        get => Volatile.Read(ref _seconds);
        set => Volatile.Write(ref _seconds, value);
    }

    /// <summary>El silenci que dura ara, o null. Es pot llegir des de qualsevol fil.</summary>
    public SilenceProblem? Problem => _problem;

    /// <summary>S'ha passat a la següent una pista que sonava en silenci (fil de control: qui escolta no ha d'esperar).</summary>
    public event Action<SilenceSkip>? Skipped;

    public void Tick()
    {
        var (left, right) = mixer.SilenceMeter.Take();
        long frames = mixer.FramesRendered;
        if (frames == _lastFrames) return; // la sortida no demana so: no hi ha res a mesurar
        long previous = _lastFrames < 0 ? frames : _lastFrames;
        _lastFrames = frames;

        double peakDb = PeakMeter.ToDb(Math.Max(left, right), -120);
        if (peakDb >= ThresholdDb)
        {
            _silentSince = null;
            _skips = 0;
            _problem = null;
            return;
        }
        if (queue.IsPaused)
        {
            // Silenci volgut (la pausa té la seva incidència): es torna a comptar quan s'acabi
            _silentSince = null;
            _problem = null;
            return;
        }

        _silentSince ??= previous;
        double silentFor = (frames - _silentSince.Value) / (double)mixer.SampleRate;
        if (silentFor < Seconds) return;

        var current = queue.CurrentStart;
        if (current is { } c && !c.Item.IsStream && _skips < MaxSkips)
        {
            var position = TimeSpan.FromSeconds(Math.Max(0, frames - c.StartFrame) / (double)mixer.SampleRate);
            queue.Skip(SkipFade);
            _skips++;
            _silentSince = frames; // la següent té els seus segons
            Skipped?.Invoke(new SilenceSkip(c.Item, position));
            return;
        }

        _problem = current switch
        {
            { Item.IsStream: true } s => new SilenceProblem($"{s.Item.SourceName()} arriba en silenci (sense so des de fa més de {Seconds:0} s)", IncidentSeverity.Warning),
            not null => new SilenceProblem($"Silenci a la sortida: s'han saltat {_skips} pistes seguides que sonaven en silenci i continua sense so", IncidentSeverity.Danger),
            null => new SilenceProblem("Silenci a la sortida: no sona res", IncidentSeverity.Danger),
        };
    }
}
