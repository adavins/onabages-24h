using System.Collections.Concurrent;
using System.Diagnostics;

namespace OnaBages.Audio;

/// <summary>Com ha de sonar una entrada de línia a la cua (D-185).</summary>
public sealed record InputOptions
{
    /// <summary>Quant ha de sonar com a molt (el final previst; l'acaba la cua amb un fos).</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// El so que es deixa guardat (el retard de l'entrada): prou per als paquets de Windows (10 ms) i per corregir la deriva
    /// dels rellotges, i prou poc perquè no es noti.
    /// </summary>
    public TimeSpan Margin { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Sense rebre so aquesta estona, l'entrada es dona per tallada i es torna a obrir.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Quant s'espera entre dos intents d'obrir-la.</summary>
    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Abans de començar: quanta estona es torna a provar d'obrir-la abans de donar-la per caiguda (D-030).</summary>
    public TimeSpan ConnectFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per sota d'aquest nivell es considera silenci (D-030).</summary>
    public double SilenceDb { get; init; } = LineInputReader.DefaultSilenceDb;

    /// <summary>Cap amunt o cap avall del marge, quanta diferència es deixa estar abans de corregir la deriva dels rellotges.</summary>
    public TimeSpan DriftTolerance { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Guany fix (dB) que s'aplica al so de l'entrada (D-188). 0 = tal com arriba.</summary>
    public double GainDb { get; init; }

    /// <summary>
    /// Anivellador automàtic (AGC, D-188): iguala el volum mentre sona (<see cref="Leveler"/>). Mira 1 s endavant: l'entrada
    /// sona aquesta estona més tard. Null = no.
    /// </summary>
    public (double TargetLufs, double MaxBoostDb)? Agc { get; init; }
}

/// <summary>
/// Omple una veu del mesclador amb una entrada de línia (D-185), en un fil propi (mai el fil d'àudio). Com
/// <see cref="StreamVoiceReader"/>, però amb una entrada de Windows (<see cref="LineInputReader"/>):
/// - S'obre amb la cua, uns segons abans, i només en guarda el més recent (<see cref="InputOptions.Margin"/>) fins a l'hora:
///   l'espera no hi afegeix retard.
/// - **Deriva dels rellotges:** la targeta d'entrada i la de sortida no van mai exactament a la mateixa velocitat (unes
///   desenes de ppm: uns quants segons al dia). Mentre sona, es mira quant so hi ha guardat (al lector i a la veu) i
///   <see cref="DriftControl"/> diu quantes mostres cal treure o repetir, d'una en una, perquè es mantingui a prop del marge.
///   Si de cop n'hi ha molt de més (Windows s'ha encallat i després n'ha enviat de cop), es torna al marge.
/// - Si es desconnecta o deixa d'enviar so, es torna a obrir tota l'estona (ho avisa a <see cref="TakeNotices"/>); qui
///   decideix quan es dona per perduda és la cua (pista encallada o reconnexió, D-031).
/// - No s'acaba sola: l'acaba la cua (durada, silenci o «Següent»).
/// </summary>
public sealed class InputVoiceReader : ILiveVoiceReader
{
    /// <summary>Quant abans de l'hora d'inici es comença a omplir la veu.</summary>
    public static readonly TimeSpan FillBefore = StreamVoiceReader.FillBefore;
    /// <summary>Si sonant n'hi ha tant de més (Windows s'ha encallat i després n'ha enviat de cop), es torna al marge de cop.</summary>
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromMilliseconds(500);

    private readonly Func<IAudioInput> _open;
    private readonly int _sampleRate;
    private readonly InputOptions _options;
    private readonly Func<long> _now;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _reading = new(true);
    private readonly ConcurrentQueue<StreamNotice> _notices = new();
    private readonly Thread _thread;
    private readonly object _voiceGate = new();
    private bool _detached;
    private int _state = (int)ReaderState.Opening;
    private string? _error;
    private string? _lastError;
    private LineInputReader? _input;
    private long _dropped, _repeated;
    private DriftControl? _drift; // de la connexió d'ara (es torna a crear en tornar a obrir l'entrada)
    private Leveler? _leveler;    // l'AGC de la connexió d'ara, o null
    private long _intoLeveler, _outOfLeveler; // mostres que hi han entrat i que n'han sortit (el que té dins)
    private int _repeatPending;   // mostres (fotogrames) per repetir a la propera lectura (deriva)
    private readonly float _gain;
    private float[] _levelerBlock = [];

    /// <param name="open">Obre l'entrada (la de debò: <see cref="LineInputReader.OpenDevice"/>; a les proves, una de falsa).</param>
    /// <param name="now">El fotograma del mesclador ara (per saber quan comença la veu).</param>
    public InputVoiceReader(MixerVoice voice, Func<IAudioInput> open, int sampleRate, InputOptions options, Func<long> now)
    {
        Voice = voice;
        _open = open;
        _sampleRate = sampleRate;
        _options = options;
        _now = now;
        _gain = (float)Math.Pow(10, options.GainDb / 20);
        _thread = new Thread(Run) { IsBackground = true, Name = $"Entrada veu {voice.Index}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public MixerVoice Voice { get; }

    public ReaderState State => (ReaderState)Volatile.Read(ref _state);

    public string? Error => Volatile.Read(ref _error);

    /// <summary>La durada prevista (una entrada no en té: és fins on ha de sonar).</summary>
    public TimeSpan? FileDuration => _options.Duration;

    public TimeSpan SilentFor => Volatile.Read(ref _input)?.SilentFor ?? TimeSpan.Zero;

    /// <summary>Una entrada no envia títol.</summary>
    public string? Title => null;

    public bool Connected => Volatile.Read(ref _input)?.State == LineInputState.Receiving;

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Quantes mostres s'han tret i quantes s'han repetit per la deriva (per a les proves i el diagnòstic).</summary>
    public (long Dropped, long Repeated) DriftCorrections => (Interlocked.Read(ref _dropped), Interlocked.Read(ref _repeated));

    /// <summary>El so guardat ara (a l'entrada, a l'AGC i a la veu): el retard que hi afegeix el motor. Ha de quedar a prop del que cal.</summary>
    public TimeSpan Buffered => TimeSpan.FromSeconds(((Volatile.Read(ref _input)?.Buffer.Count ?? 0) + Interlocked.Read(ref _intoLeveler) - Interlocked.Read(ref _outOfLeveler) + Voice.Buffer.Count) / 2.0 / _sampleRate);

    public IReadOnlyList<StreamNotice> TakeNotices()
    {
        var list = new List<StreamNotice>();
        while (_notices.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    public void PauseReading() => _reading.Reset();

    public void ResumeReading() => _reading.Set();

    private bool Started => Voice.ActualStartFrame >= 0;

    private long Frames(TimeSpan t) => (long)(t.TotalSeconds * _sampleRate);

    private void Run()
    {
        var token = _stop.Token;
        var chunk = new float[_sampleRate / 10 * 2]; // 100 ms
        var connecting = Stopwatch.StartNew();
        Stopwatch? cutSince = null;
        try
        {
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                var input = Connect(token);
                if (input is null)
                {
                    // No s'ha pogut obrir. Abans de començar: es torna a provar fins a ConnectFor; després, falla.
                    if (!Started && State == ReaderState.Opening && connecting.Elapsed >= _options.ConnectFor)
                    {
                        Volatile.Write(ref _error, _lastError ?? "No s'ha pogut obrir l'entrada.");
                        Volatile.Write(ref _state, (int)ReaderState.Failed);
                        MarkVoiceFinished();
                        return;
                    }
                    token.WaitHandle.WaitOne(_options.RetryEvery);
                    continue;
                }
                if (cutSince is { } since)
                {
                    _notices.Enqueue(new StreamNotice(false, $"S'ha recuperat després de {Seconds(since.Elapsed)} sense senyal.", since.Elapsed));
                    cutSince = null;
                }

                // Rebent: fins que es talla (si ja sonava, s'acaba de fer sonar el que havia arribat) o s'atura
                _drift = null;
                _repeatPending = 0;
                _intoLeveler = _outOfLeveler = 0;
                _leveler = _options.Agc is { } agc
                    ? new Leveler(dest => ReadSource(input.Buffer, dest), _sampleRate, agc.TargetLufs, agc.MaxBoostDb, startGainDb: Started ? 0 : null)
                    : null;
                while (input.State == LineInputState.Receiving || (Started && input.Buffer.Count > 0))
                {
                    token.ThrowIfCancellationRequested();
                    _reading.Wait(token);
                    Pump(input, chunk);
                    if (State == ReaderState.Opening && input.Buffer.Count + Voice.Buffer.Count >= Keep)
                        Volatile.Write(ref _state, (int)ReaderState.Ready);
                    token.WaitHandle.WaitOne(10);
                }

                // S'ha tallat: es torna a obrir (si ja sonava, s'avisa; la cua decideix quan es dona per perduda)
                string reason = input.Error ?? "L'entrada s'ha aturat.";
                Volatile.Write(ref _input, null);
                input.Dispose();
                Volatile.Write(ref _lastError, reason);
                if (State != ReaderState.Opening)
                {
                    cutSince ??= Stopwatch.StartNew();
                    _notices.Enqueue(new StreamNotice(true, reason, TimeSpan.Zero));
                }
                else connecting.Restart();
            }
        }
        catch (OperationCanceledException)
        {
            MarkVoiceFinished();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _error, $"Error en llegir l'entrada: {ex.Message}");
            Volatile.Write(ref _state, (int)ReaderState.Failed);
            MarkVoiceFinished();
        }
        finally
        {
            Volatile.Read(ref _input)?.Dispose();
            Volatile.Write(ref _input, null);
        }
    }

    /// <summary>Obre l'entrada i espera el primer so. Null si no s'ha pogut (el motiu queda a <see cref="LastError"/>).</summary>
    private LineInputReader? Connect(CancellationToken token)
    {
        var input = new LineInputReader(_open, _sampleRate, TimeSpan.FromSeconds(5), _options.Timeout, _options.SilenceDb);
        while (input.State == LineInputState.Opening)
        {
            if (token.WaitHandle.WaitOne(10))
            {
                input.Dispose();
                token.ThrowIfCancellationRequested();
            }
        }
        if (input.State != LineInputState.Receiving)
        {
            Volatile.Write(ref _lastError, input.Error);
            input.Dispose();
            return null;
        }
        Volatile.Write(ref _input, input);
        return input;
    }

    /// <summary>
    /// El so que es guarda (mostres): el marge i, amb l'AGC, el que l'anivellador ha de tenir per endavant (1 s, i un tros de
    /// 100 ms del que entra i un del que surt, perquè es passa a trossos).
    /// </summary>
    private long Keep => Frames(_options.Margin) * 2 + (_options.Agc is null ? 0 : Frames(TimeSpan.FromSeconds(Leveler.LookaheadSeconds + 0.2)) * 2);

    /// <summary>El que té dins l'anivellador ara (mostres).</summary>
    private long LevelerHeld => _intoLeveler - _outOfLeveler;

    /// <summary>
    /// Passa so de l'entrada a la veu, guardant només el que cal (<see cref="Keep"/>), i corregeix la deriva dels rellotges.
    /// El camí és sempre el mateix: l'entrada (amb les mostres repetides de la deriva) → el guany → l'AGC, si n'hi ha → la veu.
    /// </summary>
    private void Pump(LineInputReader input, float[] chunk)
    {
        var buffer = input.Buffer;
        long keep = Keep;
        long total = buffer.Count + LevelerHeld + Voice.Buffer.Count;
        if (!Started)
        {
            // Abans de començar, només el més recent: l'espera no ha d'afegir retard
            long extra = total - keep;
            if (extra > 0) buffer.Skip((int)Math.Min(extra, buffer.Count) & ~1);
            long start = Voice.ScheduledStartFrame;
            if (start < 0 || start - _now() > Frames(FillBefore)) return;
        }
        else
        {
            _drift ??= new DriftControl(keep / 2.0, Frames(_options.DriftTolerance), _sampleRate);
            if (total > keep + Frames(MaxBacklog) * 2)
            {
                // N'hi ha molt de més (Windows s'ha encallat i n'ha enviat de cop): es torna al que cal
                buffer.Skip((int)Math.Min(total - keep, buffer.Count) & ~1);
                _drift.Reset();
            }
            else if (buffer.Count >= 2)
            {
                // La deriva dels rellotges: unes poques mostres de menys (l'entrada va més de pressa) o repetides (més a poc a poc)
                int correction = _drift.Update(total / 2.0, buffer.Count / 2);
                if (correction > 0)
                {
                    int skipped = buffer.Skip(Math.Min(correction, buffer.Count / 2 - 1) * 2) / 2;
                    Interlocked.Add(ref _dropped, skipped);
                }
                else if (correction < 0)
                {
                    _repeatPending += -correction;
                    Interlocked.Add(ref _repeated, -correction);
                }
            }
        }

        if (_leveler is null)
        {
            int n;
            while ((n = ReadSource(buffer, chunk)) > 0)
            {
                // Sense AGC, un guany fix pot passar de ±1: es retalla aquí (el processador de la sortida pot estar desactivat)
                if (_gain != 1f) for (int i = 0; i < n; i++) chunk[i] = Math.Clamp(chunk[i], -1f, 1f);
                if (!Write(chunk.AsSpan(0, n))) throw new OperationCanceledException();
            }
            return;
        }

        // Amb l'AGC: l'anivellador demana el so a trossos de 100 ms i, si no el troba sencer, es pensa que s'ha acabat. Només
        // se li demana un tros quan el que necessita ja hi és (la primera vegada, 1 s per endavant i el tros).
        int block = _sampleRate / 10 * 2;
        if (_levelerBlock.Length != block) _levelerBlock = new float[block];
        for (;;)
        {
            long needed = LevelerHeld == 0 ? (long)(Leveler.LookaheadSeconds * 10 + 1) * block : block;
            if (buffer.Count < needed) break;
            int n = _leveler.Read(_levelerBlock);
            if (n <= 0) break;
            _outOfLeveler += n;
            if (!Write(_levelerBlock.AsSpan(0, n))) throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Llegeix so de l'entrada amb les mostres que cal repetir per la deriva i el guany fix. Al fil del lector.
    /// </summary>
    private int ReadSource(SampleRingBuffer buffer, Span<float> destination)
    {
        int written = 0;
        if (_repeatPending > 0 && destination.Length >= 4 && buffer.Count >= 2)
        {
            // Una mostra que es repeteix: es llegeix i s'escriu (1 + les que toquin) vegades
            Span<float> frame = stackalloc float[2];
            buffer.Read(frame);
            int times = Math.Min(_repeatPending + 1, destination.Length / 2);
            for (int i = 0; i < times; i++) frame.CopyTo(destination[(i * 2)..]);
            _repeatPending -= times - 1;
            written = times * 2;
        }
        written += buffer.Read(destination[written..]);
        if (_gain != 1f) for (int i = 0; i < written; i++) destination[i] *= _gain;
        if (_leveler is not null) _intoLeveler += written;
        return written;
    }

    /// <summary>Escriu a la veu, si encara és nostra (si no, false: s'ha deslligat).</summary>
    private bool Write(ReadOnlySpan<float> samples)
    {
        lock (_voiceGate)
        {
            if (_detached) return false;
            int written = 0;
            while (written < samples.Length)
            {
                int n = Voice.Buffer.Write(samples[written..]);
                if (n == 0) break; // plena: el que no hi cap es perd (no passa: només hi ha el marge)
                written += n;
            }
            return true;
        }
    }

    private void MarkVoiceFinished()
    {
        lock (_voiceGate)
        {
            if (!_detached) Voice.MarkSourceFinished();
        }
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    public void Cancel()
    {
        lock (_voiceGate) _detached = true;
        _stop.Cancel();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _reading.Set(); // si estava aturat per a una prova, que se n'adoni
        if (!_thread.Join(TimeSpan.FromSeconds(2))) lock (_voiceGate) _detached = true;
        _stop.Dispose();
        _reading.Dispose();
    }
}
