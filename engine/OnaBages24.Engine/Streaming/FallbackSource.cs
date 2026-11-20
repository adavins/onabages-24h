using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages24.Engine.Streaming;

/// <summary>Què envia ara una <see cref="FallbackSource"/>.</summary>
public enum SourceMode
{
    /// <summary>L'entrada triada (la sortida de la taula, amb els directes).</summary>
    Input,
    /// <summary>La mescla del motor: l'entrada fa estona que no porta so (o no hi és), o és la font triada.</summary>
    Mix,
}

/// <summary>Com està ara una <see cref="FallbackSource"/> (una fotografia).</summary>
public sealed record SourceStatus(SourceMode Mode, DateTimeOffset? MixSince, string? Reason);

/// <summary>
/// La font d'un emissor per internet amb reserva per silenci (fase 5, pas 13b; D-202): fa d'entrada de l'emissor
/// (<see cref="IAudioInput"/>) i, per dins, escolta alhora l'entrada de Windows triada (<see cref="LineInputReader"/>,
/// que la passa a estèreo a la freqüència del mesclador) i una còpia de la mescla del motor (<see cref="Mixer.AddTap"/>).
/// Envia l'entrada mentre porta so; si fa <c>silenceAfter</c> que arriba en silenci, o no hi és (desconnectada, no
/// envia res), envia la mescla del motor; i quan l'entrada torna a portar so seguit durant <c>returnAfter</c>, hi torna.
/// No s'emet el directe, però no hi ha silenci. Sense entrada (<c>openInput</c> null), sempre la mescla del motor.
///
/// Tot passa en un fil propi (cada 10 ms): el fil d'àudio només escriu la còpia de la mescla, i l'emissor rep el so com
/// d'una entrada qualsevol. Si l'entrada falla, es torna a obrir cada <see cref="ReopenDelay"/>. L'emissor no veu mai
/// cap error d'entrada: sempre té so.
/// </summary>
public sealed class FallbackSource : IAudioInput
{
    /// <summary>Quant s'espera per tornar a obrir l'entrada quan ha fallat.</summary>
    public static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(10);

    /// <summary>Si l'entrada fa aquesta estona que no envia gens de so, es dona per perduda (i es passa a la mescla).</summary>
    public static readonly TimeSpan InputTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Un silenci més curt que això no trenca el «so seguit» de la tornada (les pauses de la parla).</summary>
    public static readonly TimeSpan PauseTolerance = TimeSpan.FromSeconds(1);

    private readonly Func<IAudioInput>? _openInput;
    private readonly string _inputName;
    private readonly Action<SampleRingBuffer> _addTap, _removeTap;
    private readonly TimeSpan _silenceAfter, _returnAfter;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _sync = new();
    private Thread? _thread;
    private volatile bool _stop;
    private SourceStatus _status;

    /// <param name="openInput">Obre l'entrada de Windows; null = la font és la mescla del motor.</param>
    /// <param name="inputName">El nom de l'entrada, per als textos.</param>
    /// <param name="sampleRate">La freqüència del mesclador (la de la còpia de la mescla, i la que es dona a l'emissor).</param>
    /// <param name="addTap">Registra una còpia de la mescla (<see cref="Mixer.AddTap"/>); <paramref name="removeTap"/>, la treu.</param>
    public FallbackSource(Func<IAudioInput>? openInput, string inputName, int sampleRate,
        Action<SampleRingBuffer> addTap, Action<SampleRingBuffer> removeTap,
        TimeSpan silenceAfter, TimeSpan returnAfter, Func<DateTimeOffset>? clock = null)
    {
        _openInput = openInput;
        _inputName = inputName;
        SampleRate = sampleRate;
        _addTap = addTap;
        _removeTap = removeTap;
        _silenceAfter = silenceAfter;
        _returnAfter = returnAfter;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _status = openInput is null
            ? new SourceStatus(SourceMode.Mix, null, null)
            : new SourceStatus(SourceMode.Input, null, null);
    }

    public int SampleRate { get; }
    public int Channels => 2;
    public event SamplesCapturedHandler? DataAvailable;
    public event Action<Exception?>? Stopped;

    /// <summary>Què envia ara, des de quan envia la mescla i per què.</summary>
    public SourceStatus Status { get { lock (_sync) return _status; } }

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "Emissió per internet: font", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void Run()
    {
        // Prou per a uns segons de cada font; es buiden cada 10 ms
        var tap = new SampleRingBuffer(SampleRate * 2 * 2);
        var block = new float[SampleRate * 2 / 10]; // 100 ms
        LineInputReader? reader = null;
        DateTimeOffset reopenAt = DateTimeOffset.MinValue;
        bool everReceived = false;
        var soundFor = Stopwatch.StartNew(); // des de quan l'entrada porta so seguit
        bool hadSound = false;
        _addTap(tap);
        try
        {
            while (!_stop)
            {
                var now = _clock();
                // L'entrada: s'obre, i si falla es torna a obrir al cap d'una estona
                if (_openInput is not null && (reader is null || reader.State is LineInputState.Failed or LineInputState.Stopped) && now >= reopenAt)
                {
                    reader?.Dispose();
                    reader = new LineInputReader(_openInput, SampleRate, TimeSpan.FromSeconds(2), InputTimeout);
                    reopenAt = now + ReopenDelay;
                    hadSound = false;
                }

                // Com està l'entrada
                bool receiving = reader?.State == LineInputState.Receiving;
                var silentFor = receiving ? reader!.SilentFor : TimeSpan.MaxValue;
                bool sound = receiving && silentFor < PauseTolerance;
                if (sound && !hadSound) soundFor.Restart();
                hadSound = sound;

                if (_openInput is not null)
                {
                    if (Status.Mode == SourceMode.Input)
                    {
                        // A la mescla: si l'entrada arriba en silenci massa estona, o si no envia so (en obrir-la, quan
                        // LineInputReader s'hi dona per vençut; després, de seguida)
                        string? reason = receiving
                            ? silentFor >= _silenceAfter ? $"l'entrada «{_inputName}» fa {_silenceAfter.TotalSeconds:0} s que arriba en silenci" : null
                            : everReceived || reader?.State == LineInputState.Failed
                                ? $"l'entrada «{_inputName}» no envia so ({reader?.Error ?? "s'està tornant a obrir"})"
                                : null;
                        if (reason is not null) Set(new SourceStatus(SourceMode.Mix, now, reason));
                    }
                    // A l'entrada: quan porta so seguit prou estona (la primera vegada que funciona, de seguida)
                    else if (sound && (!everReceived || soundFor.Elapsed >= _returnAfter))
                    {
                        Set(new SourceStatus(SourceMode.Input, null, null));
                    }
                    if (receiving) everReceived = true;
                }

                // El so: es buiden totes dues fonts (perquè no s'acumuli el que no s'envia) i s'envia la que toca
                bool fromMix = Status.Mode == SourceMode.Mix;
                Drain(tap, block, send: fromMix);
                if (reader is not null) Drain(reader.Buffer, block, send: !fromMix);
                Thread.Sleep(10);
            }
        }
        catch (Exception ex)
        {
            Stopped?.Invoke(ex); // no hauria de passar: l'emissor s'atura i el motor el torna a engegar
            return;
        }
        finally
        {
            _removeTap(tap);
            reader?.Dispose();
        }
        Stopped?.Invoke(null);
    }

    private void Drain(SampleRingBuffer buffer, float[] block, bool send)
    {
        int n;
        while ((n = buffer.Read(block)) > 0)
        {
            if (send) DataAvailable?.Invoke(block.AsSpan(0, n), false, false);
        }
    }

    private void Set(SourceStatus status)
    {
        lock (_sync) _status = status;
    }
}
