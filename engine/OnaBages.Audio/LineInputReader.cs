using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OnaBages.Audio;

/// <summary>En quin punt és un <see cref="LineInputReader"/>.</summary>
public enum LineInputState
{
    /// <summary>Obrint l'entrada i esperant el primer so.</summary>
    Opening,
    /// <summary>Rebent àudio.</summary>
    Receiving,
    /// <summary>S'ha aturat (<see cref="LineInputReader.Dispose"/>).</summary>
    Stopped,
    /// <summary>No s'ha pogut obrir, no envia so o s'ha desconnectat: <see cref="LineInputReader.Error"/> diu per què.</summary>
    Failed,
}

/// <summary>
/// Escolta una entrada de línia de Windows (entrades de línia, D-185) en un fil propi, com <see cref="RadioStreamReader"/>
/// amb els streams: el so capturat (WASAPI, mode compartit) es passa a estèreo a la freqüència demanada (la de la sortida)
/// i es deixa a <see cref="Buffer"/> perquè el reprodueixi qui toqui. Mesura, al seu fil, el nivell i el silenci, i quantes
/// mostres per segon envia de debò l'entrada (<see cref="ClockPpm"/>: la targeta d'entrada i la de sortida no van mai
/// exactament a la mateixa velocitat). No toca mai el fil d'àudio.
///
/// El fil de captura de Windows només copia les mostres a una memòria (sense crear objectes ni esperar); la conversió es fa
/// al fil del lector. Si la memòria és plena, l'àudio nou es perd: ningú no s'espera mai.
/// </summary>
public sealed class LineInputReader : IDisposable
{
    /// <summary>Per sota d'aquest nivell es considera silenci (D-030), com als streams.</summary>
    public const double DefaultSilenceDb = RadioStreamReader.DefaultSilenceDb;

    private readonly Func<IAudioInput> _open;
    private readonly int _sampleRate;
    private readonly TimeSpan _timeout;
    private readonly double _silenceDb;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly object _sync = new();

    private IAudioInput? _input;
    private SampleRingBuffer? _raw;      // el que captura Windows, amb els canals i la freqüència de l'entrada
    private long _capturedFrames;        // a la freqüència de l'entrada
    private long _discontinuities;
    private Exception? _stoppedWith;
    private int _inputStopped;

    private int _state = (int)LineInputState.Opening;
    private string? _error;
    private string? _format;
    private long _receivedFrames;        // a la freqüència de sortida
    private long _droppedFrames;
    private long _silentFrames;          // seguides, per sota del llindar
    private double _peakDb = double.NegativeInfinity;
    private double _maxPeakDb = double.NegativeInfinity;
    // Rellotge de l'entrada (només el fil de captura hi escriu; el resultat, amb Interlocked)
    private static readonly double TicksPerSecond = Stopwatch.Frequency;
    private const double ClockBlockSeconds = 2;
    private long _clockStart;            // quan ha arribat el primer paquet (ticks de Stopwatch)
    private double _blockMin = double.MaxValue, _firstBlockMin = double.NaN, _firstBlockAt;
    private int _block = -1;
    private long _clockPpmBits = BitConverter.DoubleToInt64Bits(double.NaN);

    /// <param name="open">Obre l'entrada (la de debò: <see cref="OpenDevice"/>; a les proves, una de falsa). Es crida al fil del lector.</param>
    /// <param name="sampleRate">La freqüència de sortida (la de la targeta).</param>
    /// <param name="buffer">Quants segons hi caben a la memòria.</param>
    /// <param name="timeout">Temps màxim sense rebre so (en obrir-la, o rebent) abans de donar-la per perduda.</param>
    public LineInputReader(Func<IAudioInput> open, int sampleRate, TimeSpan buffer, TimeSpan timeout, double silenceDb = DefaultSilenceDb)
    {
        _open = open;
        _sampleRate = sampleRate;
        _timeout = timeout;
        _silenceDb = silenceDb;
        Buffer = new SampleRingBuffer(Math.Max(2, (int)(buffer.TotalSeconds * sampleRate)) * 2);
        _thread = new Thread(Run) { IsBackground = true, Name = "Entrada de línia", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Obre l'entrada de Windows amb aquest identificador i, si no hi és (p. ex. s'ha canviat de port USB i Windows li ha
    /// donat un altre identificador), la primera entrada activa amb el mateix nom (com la sortida, D-038).
    /// </summary>
    /// <exception cref="AudioDeviceException">Si no hi és ni pel nom.</exception>
    public static IAudioInput OpenDevice(string? deviceId, string? deviceName)
    {
        AudioDeviceException? first = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try { return WasapiAudioInput.Open(deviceId); }
            catch (AudioDeviceException ex) { first = ex; }
        }
        if (!string.IsNullOrEmpty(deviceName))
        {
            var byName = DeviceList.GetAll().FirstOrDefault(d => d.Kind == DeviceKind.Input && d.Status == DeviceStatus.Active
                && string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase) && d.Id != deviceId);
            if (byName is not null) return WasapiAudioInput.Open(byName.Id);
        }
        throw first ?? new AudioDeviceException($"Entrada no disponible: no es troba «{deviceName}» a Windows.");
    }

    /// <summary>L'àudio rebut: estèreo intercalat, a la freqüència de sortida.</summary>
    public SampleRingBuffer Buffer { get; }

    public LineInputState State => (LineInputState)Volatile.Read(ref _state);

    /// <summary>Per què ha fallat (per a l'operador), o null.</summary>
    public string? Error { get { lock (_sync) return _error; } }

    /// <summary>El format de l'entrada (p. ex. «48.000 Hz, estèreo»), o null si encara no s'ha obert.</summary>
    public string? Format { get { lock (_sync) return _format; } }

    /// <summary>Quant d'àudio ha rebut.</summary>
    public TimeSpan Received => Seconds(Interlocked.Read(ref _receivedFrames));

    /// <summary>Quant d'àudio s'ha perdut perquè la memòria era plena.</summary>
    public TimeSpan DroppedSeconds => Seconds(Interlocked.Read(ref _droppedFrames));

    /// <summary>Quantes vegades Windows ha dit que s'havien perdut dades de l'entrada.</summary>
    public long Discontinuities => Interlocked.Read(ref _discontinuities);

    /// <summary>Quant fa (en temps d'àudio) que arriba en silenci, seguit. Zero si l'últim tros tenia so.</summary>
    public TimeSpan SilentFor => Seconds(Interlocked.Read(ref _silentFrames));

    /// <summary>El pic de l'últim tros, en dBFS (−∞ = silenci absolut).</summary>
    public double PeakDb { get { lock (_sync) return _peakDb; } }

    /// <summary>El pic més alt des que s'ha obert, en dBFS.</summary>
    public double MaxPeakDb { get { lock (_sync) return _maxPeakDb; } }

    /// <summary>
    /// Quant més de pressa (positiu) o més a poc a poc (negatiu) que el rellotge de l'ordinador envia so l'entrada, en parts
    /// per milió, o null si encara fa poca estona que rep (calen uns segons per mesurar-ho bé). 100 ppm = 0,36 s per hora.
    /// </summary>
    public double? ClockPpm => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _clockPpmBits)) is var ppm && !double.IsNaN(ppm) ? ppm : null;

    private TimeSpan Seconds(long frames) => TimeSpan.FromSeconds((double)frames / _sampleRate);

    private void Run()
    {
        var token = _stop.Token;
        try
        {
            // Obrir l'entrada (al fil del lector: Windows pot tardar)
            IAudioInput input;
            try
            {
                input = _open();
            }
            catch (AudioDeviceException ex)
            {
                Fail(ex.Message);
                return;
            }
            lock (_sync)
            {
                _input = input;
                _format = string.Create(System.Globalization.CultureInfo.GetCultureInfo("ca-ES"),
                    $"{input.SampleRate:N0} Hz, {(input.Channels == 1 ? "mono" : input.Channels == 2 ? "estèreo" : $"{input.Channels} canals")}");
            }
            int channels = input.Channels;
            var raw = new SampleRingBuffer(input.SampleRate * channels * 2); // 2 s
            Volatile.Write(ref _raw, raw);
            input.DataAvailable += OnData;
            input.Stopped += ex =>
            {
                Volatile.Write(ref _stoppedWith, ex);
                Volatile.Write(ref _inputStopped, 1);
            };
            input.Start();

            // De l'entrada (els seus canals i la seva freqüència) a estèreo a la freqüència de sortida
            var staging = new StagingProvider(input.SampleRate);
            ISampleProvider output = input.SampleRate == _sampleRate ? staging : new WdlResamplingSampleProvider(staging, _sampleRate);
            var rawChunk = new float[input.SampleRate / 50 * channels]; // 20 ms
            var stereo = new float[input.SampleRate / 50 * 2];
            var samples = new float[_sampleRate / 50 * 2 + 64];

            var sinceData = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                if (Volatile.Read(ref _inputStopped) == 1)
                {
                    var ex = Volatile.Read(ref _stoppedWith);
                    Fail(ex is null ? "L'entrada s'ha aturat." : $"L'entrada s'ha desconnectat o ha deixat de funcionar ({AudioErrors.Message(ex)}).");
                    return;
                }

                bool got = false;
                int read;
                while ((read = raw.Read(rawChunk)) > 0)
                {
                    got = true;
                    int frames = read / channels;
                    ToStereo(rawChunk.AsSpan(0, read), channels, stereo.AsSpan(0, frames * 2));
                    staging.Add(stereo.AsSpan(0, frames * 2));
                    // Només el que es pot fer amb el que ja ha arribat: si se'n demana més, el convertidor de freqüència hi
                    // posaria trossos buits (talls). Se'n deixa una mica per a la memòria del convertidor.
                    for (;;)
                    {
                        long ready = input.SampleRate == _sampleRate ? staging.Frames
                            : (long)((staging.Frames - ResamplerReserve) * (double)_sampleRate / input.SampleRate);
                        if (ready <= 0) break;
                        int n = output.Read(samples.AsSpan(0, (int)Math.Min(samples.Length, ready * 2)));
                        if (n <= 0) break;
                        Deliver(samples.AsSpan(0, n));
                        Measure(samples.AsSpan(0, n));
                    }
                }

                if (got)
                {
                    sinceData.Restart();
                    if (State == LineInputState.Opening) Volatile.Write(ref _state, (int)LineInputState.Receiving);
                }
                else if (sinceData.Elapsed >= _timeout)
                {
                    Fail(State == LineInputState.Opening
                        ? $"L'entrada no envia so (en {_timeout.TotalSeconds:0} s no n'ha arribat gens)."
                        : $"L'entrada ha deixat d'enviar so (fa {_timeout.TotalSeconds:0} s que no n'arriba).");
                    return;
                }
                token.WaitHandle.WaitOne(10);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail($"Error en llegir l'entrada: {ex.Message}");
        }
        finally
        {
            IAudioInput? input;
            lock (_sync)
            {
                input = _input;
                _input = null;
            }
            if (input is not null)
            {
                try { input.Stop(); } catch (Exception) { /* ja estava aturada o desconnectada */ }
                input.Dispose();
            }
            if (State is LineInputState.Opening or LineInputState.Receiving) Volatile.Write(ref _state, (int)LineInputState.Stopped);
        }
    }

    /// <summary>Fil de captura de Windows: només copia les mostres i compta (sense crear objectes ni esperar).</summary>
    private void OnData(ReadOnlySpan<float> samples, bool silent, bool discontinuity)
    {
        var raw = Volatile.Read(ref _raw);
        if (raw is null) return;
        // Windows sovint marca el primer paquet com a discontinuïtat (no s'ha perdut res): no compta
        if (discontinuity && Interlocked.Read(ref _capturedFrames) > 0) Interlocked.Increment(ref _discontinuities);
        int channels = _input?.Channels ?? 2;
        MeasureClock(Interlocked.Read(ref _capturedFrames), _input?.SampleRate ?? _sampleRate);
        Interlocked.Add(ref _capturedFrames, samples.Length / Math.Max(1, channels));
        if (silent)
        {
            // Windows diu que és silenci: es posen zeros (el contingut del paquet no compta)
            int left = samples.Length;
            while (left > 0)
            {
                int n = Math.Min(left, Zeros.Length);
                if (raw.Write(Zeros.AsSpan(0, n)) < n) return;
                left -= n;
            }
            return;
        }
        raw.Write(samples); // si no hi cap, es perd: el fil del lector va endarrerit
    }

    private static readonly float[] Zeros = new float[4096];

    /// <summary>Fotogrames de l'entrada que es deixen sempre al convertidor de freqüència (en necessita uns quants per davant).</summary>
    private const int ResamplerReserve = 64;

    /// <summary>
    /// La velocitat del rellotge de l'entrada (fil de captura; sense crear objectes). Per a cada paquet, quant arriba més tard
    /// del que tocaria segons les mostres que ja han arribat (<c>ara − mostres / freqüència</c>). Els paquets arriben amb uns
    /// mil·lisegons de retard que varien, però mai abans d'hora: per això, de cada tros de 2 s, es pren el que ha arribat més
    /// aviat. Si la targeta va més de pressa, aquest valor baixa a poc a poc; comparant el primer tros amb l'últim se sap quant.
    /// </summary>
    private void MeasureClock(long framesBefore, int inputRate)
    {
        long now = Stopwatch.GetTimestamp();
        if (_clockStart == 0) _clockStart = now;
        double since = (now - _clockStart) / TicksPerSecond;
        double offset = since - (double)framesBefore / inputRate;
        int block = (int)(since / ClockBlockSeconds);
        if (block != _block)
        {
            if (_block == 0)
            {
                _firstBlockMin = _blockMin;
                _firstBlockAt = ClockBlockSeconds / 2;
            }
            else if (_block > 0 && !double.IsNaN(_firstBlockMin))
            {
                // Del primer tros a l'últim acabat: si el retard ha baixat, l'entrada envia més mostres de les que tocaria
                double blockAt = (_block + 0.5) * ClockBlockSeconds;
                double ppm = -(_blockMin - _firstBlockMin) / (blockAt - _firstBlockAt) * 1e6;
                Interlocked.Exchange(ref _clockPpmBits, BitConverter.DoubleToInt64Bits(ppm));
            }
            _block = block;
            _blockMin = double.MaxValue;
        }
        if (offset < _blockMin) _blockMin = offset;
    }

    /// <summary>Dels canals de l'entrada a estèreo: mono als dos costats; amb més de 2 canals, els dos primers.</summary>
    private static void ToStereo(ReadOnlySpan<float> source, int channels, Span<float> destination)
    {
        int frames = source.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            float left = source[f * channels];
            float right = channels == 1 ? left : source[f * channels + 1];
            destination[f * 2] = left;
            destination[f * 2 + 1] = right;
        }
    }

    private void Deliver(ReadOnlySpan<float> samples)
    {
        int written = Buffer.Write(samples);
        Interlocked.Add(ref _receivedFrames, samples.Length / 2);
        if (written < samples.Length) Interlocked.Add(ref _droppedFrames, (samples.Length - written) / 2);
    }

    private void Measure(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        if (db < _silenceDb) Interlocked.Add(ref _silentFrames, samples.Length / 2);
        else Interlocked.Exchange(ref _silentFrames, 0);
        lock (_sync)
        {
            _peakDb = db;
            if (db > _maxPeakDb) _maxPeakDb = db;
        }
    }

    private void Fail(string message)
    {
        lock (_sync) _error = message;
        Volatile.Write(ref _state, (int)LineInputState.Failed);
    }

    /// <summary>Atura la captura (espera el fil com a molt 2 s).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>L'estèreo a la freqüència de l'entrada, per al convertidor de freqüència: dona el que hi ha (pot ser res).</summary>
    private sealed class StagingProvider(int sampleRate) : ISampleProvider
    {
        private readonly SampleRingBuffer _buffer = new(sampleRate * 2); // 1 s

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        public void Add(ReadOnlySpan<float> samples) => _buffer.Write(samples);

        /// <summary>Quants fotogrames (estèreo) hi ha per convertir.</summary>
        public long Frames => _buffer.Count / 2;

        public int Read(Span<float> buffer) => _buffer.Read(buffer);
    }
}
