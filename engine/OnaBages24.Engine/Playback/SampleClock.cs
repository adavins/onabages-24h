using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Rellotge de mostres (fase 2, pas 4; docs/planificador.md, secció 7): converteix una hora del rellotge en el fotograma
/// del mesclador que sonarà en aquell moment, i al revés. Així un TALLA es programa a la mostra exacta, amb el mateix
/// rellotge que l'encadenament, i no depèn de quan passa el fil de control.
///
/// Com es calcula: el mesclador diu quants fotogrames havia mesclat en acabar l'últim bloc i quan. Aquell últim
/// fotograma no surt pels altaveus fins al cap de la latència de la sortida (la memòria de Windows, uns 100 ms).
/// A partir d'aquí, cada segon són <c>SampleRate</c> fotogrames més.
/// </summary>
public sealed class SampleClock
{
    /// <summary>Si fa més d'això que el mesclador no ha mesclat res, és que no hi ha sortida: no es pot calcular.</summary>
    private static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(1);

    private readonly int _sampleRate;
    private readonly Func<(long Frames, long Timestamp)> _snapshot;
    private readonly Func<long> _timestamp;
    private readonly Func<DateTimeOffset> _wallClock;

    /// <param name="latency">Quant tarda un fotograma mesclat a sortir pels altaveus.</param>
    public SampleClock(Mixer mixer, TimeSpan latency, Func<DateTimeOffset>? wallClock = null)
        : this(mixer.SampleRate, mixer.RenderSnapshot, Stopwatch.GetTimestamp, wallClock ?? (() => DateTimeOffset.Now), latency)
    {
    }

    /// <summary>Per a les proves: la parella del mesclador, la marca de temps actual i l'hora, tot fals.</summary>
    public SampleClock(int sampleRate, Func<(long Frames, long Timestamp)> snapshot, Func<long> timestamp,
        Func<DateTimeOffset> wallClock, TimeSpan latency)
    {
        _sampleRate = sampleRate;
        _snapshot = snapshot;
        _timestamp = timestamp;
        _wallClock = wallClock;
        Latency = latency;
    }

    public TimeSpan Latency { get; set; }

    /// <summary>El fotograma que sonarà a l'hora <paramref name="when"/>, o null si ara no hi ha sortida (el mesclador no mescla).</summary>
    public long? FrameAt(DateTimeOffset when)
    {
        if (Reference() is not var (frames, heardAt)) return null;
        return frames + (long)Math.Round((when - heardAt).TotalSeconds * _sampleRate);
    }

    /// <summary>L'hora en què sona (o ha sonat) el fotograma <paramref name="frame"/>, o null si no hi ha sortida.</summary>
    public DateTimeOffset? TimeOf(long frame)
    {
        if (Reference() is not var (frames, heardAt)) return null;
        return heardAt + TimeSpan.FromSeconds((double)(frame - frames) / _sampleRate);
    }

    /// <summary>L'últim fotograma mesclat i l'hora del rellotge en què sonarà.</summary>
    private (long Frames, DateTimeOffset HeardAt)? Reference()
    {
        var (frames, renderedAt) = _snapshot();
        if (renderedAt == 0) return null;
        long now = _timestamp();
        var sinceRender = TimeSpan.FromSeconds((double)(now - renderedAt) / Stopwatch.Frequency);
        if (sinceRender > MaxSilence) return null;
        return (frames, _wallClock() - sinceRender + Latency);
    }
}
