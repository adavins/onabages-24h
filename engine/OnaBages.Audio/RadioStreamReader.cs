using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OnaBages.Audio;

/// <summary>En quin punt és un <see cref="RadioStreamReader"/>.</summary>
public enum RadioStreamState
{
    /// <summary>Connectant-se i esperant la capçalera.</summary>
    Connecting,
    /// <summary>Rebent àudio.</summary>
    Receiving,
    /// <summary>S'ha aturat (<see cref="RadioStreamReader.Dispose"/>).</summary>
    Stopped,
    /// <summary>No s'ha pogut connectar o s'ha tallat: <see cref="RadioStreamReader.Error"/> diu per què.</summary>
    Failed,
}

/// <summary>
/// Escolta un stream de ràdio per internet en un fil propi (fase 2, pas 7a; D-175): es connecta
/// (<see cref="RadioStreamConnection"/>), descodifica el MP3 fotograma a fotograma (NLayer), el passa a estèreo a la
/// freqüència demanada i el deixa a <see cref="Buffer"/> perquè el reprodueixi qui toqui. No toca mai el fil d'àudio.
/// Mesura, al seu fil, el nivell i el silenci (D-030: «el nivell es mesura al fil que llegeix el stream») i quants segons
/// envia el servidor de cop en connectar-s'hi (<see cref="Burst"/>: el retard que es podria guanyar començant pel directe).
/// Si la memòria és plena, l'àudio nou es perd (<see cref="DroppedSeconds"/>): el lector no s'atura mai a esperar.
/// </summary>
public sealed class RadioStreamReader : IDisposable
{
    /// <summary>Per sota d'aquest nivell es considera silenci (D-030).</summary>
    public const double DefaultSilenceDb = -50;
    /// <summary>Durant quant de temps, després de connectar-se, es mira quant s'avança el servidor (la ràfega inicial).</summary>
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(5);

    private readonly Uri _url;
    private readonly int _sampleRate;
    private readonly TimeSpan _timeout;
    private readonly double _silenceDb;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly object _sync = new();
    private Stream? _stream;

    private int _state = (int)RadioStreamState.Connecting;
    private string? _error;
    private RadioStreamInfo? _info;
    private string? _title;
    private string? _format;
    private long _receivedFrames;  // a la freqüència de sortida
    private long _droppedFrames;
    private long _silentFrames;    // seguides, per sota del llindar
    private double _peakDb = double.NegativeInfinity;
    private double _maxPeakDb = double.NegativeInfinity;
    private double _burstSeconds;
    private double _leadSeconds;
    private TimeSpan? _connectTime;

    /// <param name="url">L'adreça del stream.</param>
    /// <param name="sampleRate">La freqüència de sortida (la de la targeta).</param>
    /// <param name="buffer">Quants segons hi caben a la memòria.</param>
    /// <param name="timeout">Temps màxim per connectar-se, i sense rebre dades abans de donar-lo per tallat.</param>
    public RadioStreamReader(Uri url, int sampleRate, TimeSpan buffer, TimeSpan timeout, double silenceDb = DefaultSilenceDb)
    {
        _url = url;
        _sampleRate = sampleRate;
        _timeout = timeout;
        _silenceDb = silenceDb;
        Buffer = new SampleRingBuffer(Math.Max(2, (int)(buffer.TotalSeconds * sampleRate)) * 2);
        _thread = new Thread(Run) { IsBackground = true, Name = "Stream " + url.Host };
        _thread.Start();
    }

    /// <summary>L'àudio rebut: estèreo intercalat, a la freqüència de sortida.</summary>
    public SampleRingBuffer Buffer { get; }

    public RadioStreamState State => (RadioStreamState)Volatile.Read(ref _state);

    /// <summary>Per què ha fallat (per a l'operador), o null.</summary>
    public string? Error { get { lock (_sync) return _error; } }

    /// <summary>El que ha dit el servidor en connectar-s'hi, o null si encara no.</summary>
    public RadioStreamInfo? Info { get { lock (_sync) return _info; } }

    /// <summary>L'últim títol que ha enviat el servidor (p. ex. «Artista - Cançó»), o null.</summary>
    public string? Title { get { lock (_sync) return _title; } }

    /// <summary>El format de l'àudio rebut (p. ex. «MP3 128 kbps, 44.100 Hz, estèreo»), o null si encara no n'ha arribat.</summary>
    public string? Format { get { lock (_sync) return _format; } }

    /// <summary>Quant ha trigat a connectar-se i rebre la capçalera.</summary>
    public TimeSpan? ConnectTime { get { lock (_sync) return _connectTime; } }

    /// <summary>Quant d'àudio ha rebut.</summary>
    public TimeSpan Received => Seconds(Interlocked.Read(ref _receivedFrames));

    /// <summary>Quant d'àudio s'ha perdut perquè la memòria era plena.</summary>
    public TimeSpan DroppedSeconds => Seconds(Interlocked.Read(ref _droppedFrames));

    /// <summary>Quant fa (en temps d'àudio) que arriba en silenci, seguit. Zero si l'últim tros tenia so.</summary>
    public TimeSpan SilentFor => Seconds(Interlocked.Read(ref _silentFrames));

    /// <summary>El pic de l'últim tros (uns 100 ms), en dB.</summary>
    public double PeakDb { get { lock (_sync) return _peakDb; } }

    /// <summary>El pic més alt des que s'ha connectat, en dB.</summary>
    public double MaxPeakDb { get { lock (_sync) return _maxPeakDb; } }

    /// <summary>
    /// Quants segons d'àudio ha enviat el servidor de cop en connectar-s'hi (el màxim que s'ha avançat al rellotge durant
    /// els primers <see cref="BurstWindow"/>). És el retard que afegeix el servidor i que es podria guanyar començant pel
    /// directe.
    /// </summary>
    public TimeSpan Burst { get { lock (_sync) return TimeSpan.FromSeconds(_burstSeconds); } }

    /// <summary>Quant va avançat ara el que ha arribat respecte del rellotge des de la connexió.</summary>
    public TimeSpan Lead { get { lock (_sync) return TimeSpan.FromSeconds(_leadSeconds); } }

    private TimeSpan Seconds(long frames) => TimeSpan.FromSeconds((double)frames / _sampleRate);

    private void Run()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var (audio, info) = RadioStreamConnection.Open(_url, _timeout, title => { lock (_sync) _title = title; }, _stop.Token);
            lock (_sync)
            {
                _stream = audio;
                _info = info;
                _connectTime = watch.Elapsed;
            }
            if (_stop.IsCancellationRequested) return;
            if (info.IsAac)
                throw new RadioStreamException($"Aquest stream és AAC ({info.ContentType}) i encara només es poden fer sonar els MP3.");
            Volatile.Write(ref _state, (int)RadioStreamState.Receiving);
            Decode(new LookbackStream(audio), Stopwatch.StartNew());
            if (!_stop.IsCancellationRequested) Fail("El servidor ha tancat el stream.");
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // Aturat a mitja lectura: no és un error
        }
        catch (RadioStreamException ex)
        {
            Fail(ex.Message);
        }
        catch (IOException ex)
        {
            Fail($"El stream s'ha tallat: fa més de {_timeout.TotalSeconds:0} s que no arriben dades ({ex.Message}).");
        }
        catch (Exception ex)
        {
            Fail($"Error en llegir el stream: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _stream?.Dispose();
                _stream = null;
            }
            if (State is RadioStreamState.Connecting or RadioStreamState.Receiving) Volatile.Write(ref _state, (int)RadioStreamState.Stopped);
        }
    }

    private void Fail(string message)
    {
        lock (_sync) _error = message;
        Volatile.Write(ref _state, (int)RadioStreamState.Failed);
    }

    /// <summary>Fotograma a fotograma: descodifica, converteix i escriu a la memòria, i va mesurant.</summary>
    private void Decode(Stream input, Stopwatch sinceConnected)
    {
        NLayer.NAudioSupport.Mp3FrameDecompressor? decoder = null;
        BufferedWaveProvider? decoded = null;
        ISampleProvider? output = null;
        (int Rate, int Channels) current = default;
        var pcm = new byte[1152 * 2 * 4 * 2];
        var samples = new float[_sampleRate / 5 * 2]; // 200 ms en estèreo
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var frame = Mp3Frame.LoadFromStream(input);
                if (frame is null) return; // s'ha tancat
                int channels = frame.ChannelMode == ChannelMode.Mono ? 1 : 2;
                if (decoder is null || current != (frame.SampleRate, channels))
                {
                    // Primer fotograma, o el format ha canviat a mig stream: es torna a muntar tot
                    decoder?.Dispose();
                    current = (frame.SampleRate, channels);
                    decoder = new NLayer.NAudioSupport.Mp3FrameDecompressor(new Mp3WaveFormat(frame.SampleRate, channels, frame.FrameLength, frame.BitRate));
                    decoded = new BufferedWaveProvider(decoder.OutputFormat, TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true, ReadFully = false };
                    ISampleProvider stereo = decoded.ToSampleProvider();
                    if (channels == 1) stereo = new MonoToStereoSampleProvider(stereo);
                    output = frame.SampleRate == _sampleRate ? stereo : new WdlResamplingSampleProvider(stereo, _sampleRate);
                }
                lock (_sync) _format = $"MP3 {frame.BitRate / 1000} kbps, {frame.SampleRate:N0} Hz, {(channels == 1 ? "mono" : "estèreo")}";

                int bytes = decoder.DecompressFrame(frame, pcm, 0);
                decoded!.AddSamples(pcm, 0, bytes);
                int read;
                while ((read = output!.Read(samples)) > 0)
                {
                    Deliver(samples.AsSpan(0, read));
                    Measure(samples.AsSpan(0, read), sinceConnected.Elapsed);
                }
            }
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    private void Deliver(ReadOnlySpan<float> samples)
    {
        int written = Buffer.Write(samples);
        Interlocked.Add(ref _receivedFrames, samples.Length / 2);
        if (written < samples.Length) Interlocked.Add(ref _droppedFrames, (samples.Length - written) / 2);
    }

    private void Measure(ReadOnlySpan<float> samples, TimeSpan elapsed)
    {
        float peak = 0;
        foreach (float s in samples) peak = Math.Max(peak, Math.Abs(s));
        double db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        if (db < _silenceDb) Interlocked.Add(ref _silentFrames, samples.Length / 2);
        else Interlocked.Exchange(ref _silentFrames, 0);

        double lead = Received.TotalSeconds - elapsed.TotalSeconds;
        lock (_sync)
        {
            _peakDb = db;
            if (db > _maxPeakDb) _maxPeakDb = db;
            _leadSeconds = lead;
            if (elapsed <= BurstWindow && lead > _burstSeconds) _burstSeconds = lead;
        }
    }

    /// <summary>Atura la lectura i tanca la connexió (espera el fil com a molt 2 s).</summary>
    public void Dispose()
    {
        _stop.Cancel();
        lock (_sync) _stream?.Dispose(); // desbloqueja una lectura que espera la xarxa
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>
    /// El flux del stream amb una mica de memòria: <see cref="Mp3Frame.LoadFromStream(Stream)"/> salta endavant per comprovar
    /// que els dos fotogrames següents encaixen i després torna enrere, i un stream de xarxa no ho permet. Guarda els últims
    /// <see cref="History"/> bytes: saltar enrere hi torna, i saltar endavant llegeix (i guarda) el que falta. També llegeix fins a omplir el que es demana (una lectura de xarxa pot
    /// tornar menys bytes dels demanats, i el fotograma s'ha de llegir sencer).
    /// </summary>
    private sealed class LookbackStream(Stream inner) : Stream
    {
        private const int History = 64 * 1024;
        private readonly byte[] _history = new byte[History];
        private long _read;      // bytes llegits del flux de debò
        private long _position;  // on som (pot ser enrere, dins de la memòria)

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        /// <summary>Un stream no té mida: «molt llarg» (NAudio la mira per saber si hi cap una etiqueta ID3).</summary>
        public override long Length => long.MaxValue;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => throw new NotSupportedException(),
            };
            if (target < _read - History || target - _read > History / 2) throw new NotSupportedException("Un stream no es pot moure tan lluny.");
            // Endavant: es llegeix (i es guarda) fins a arribar-hi; si el stream s'acaba abans, es queda al final
            while (_read < target)
            {
                var chunk = new byte[(int)Math.Min(target - _read, 8192)];
                int got = inner.Read(chunk);
                if (got == 0) break;
                Remember(chunk.AsSpan(0, got));
            }
            _position = Math.Min(target, _read);
            return _position;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                if (_position < _read)
                {
                    // Primer, el que ja s'havia llegit (s'ha tornat enrere)
                    int index = (int)(_position % History);
                    int n = (int)Math.Min(Math.Min(_read - _position, History - index), buffer.Length - total);
                    _history.AsSpan(index, n).CopyTo(buffer[total..]);
                    _position += n;
                    total += n;
                    continue;
                }
                int got = inner.Read(buffer[total..]);
                if (got == 0) break;
                Remember(buffer.Slice(total, got));
                _position += got;
                total += got;
            }
            return total;
        }

        private void Remember(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++) _history[(int)((_read + i) % History)] = bytes[i];
            _read += bytes.Length;
        }
    }
}
