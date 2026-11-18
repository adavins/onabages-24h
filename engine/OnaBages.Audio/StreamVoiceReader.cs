using System.Collections.Concurrent;
using System.Diagnostics;

namespace OnaBages.Audio;

/// <summary>Com ha de sonar un stream a la cua (fase 2, pas 7b; D-030, D-175).</summary>
public sealed record StreamOptions
{
    /// <summary>Quant ha de sonar com a molt (el final previst; l'acaba la cua amb un fos).</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// «Comença pel directe» (D-175): es llença el que el servidor envia de cop en connectar-s'hi i el que arriba mentre
    /// s'espera l'hora, i es comença pel més recent amb només <see cref="Margin"/> de marge. Sense, es comença amb el que
    /// envia el servidor (com un reproductor normal), però sense afegir-hi l'espera.
    /// </summary>
    public bool LiveEdge { get; init; } = true;

    /// <summary>El marge que es deixa contra els talls de la xarxa amb <see cref="LiveEdge"/>.</summary>
    public TimeSpan Margin { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Temps màxim per connectar-se, i sense rebre dades abans de donar-lo per tallat i tornar-s'hi a connectar. Curt: quan
    /// es talla la xarxa, la connexió no es tanca (només deixen d'arribar dades), i com abans es torni a connectar, més
    /// marge queda abans que la cua passi a la següent (provat pel desenvolupador: amb 5 s, un tall de 3 s ja saltava).
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Quant s'espera entre dos intents de connexió.</summary>
    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Abans de començar: quanta estona es torna a provar de connectar abans de donar-lo per caigut (D-030).</summary>
    public TimeSpan ConnectFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per sota d'aquest nivell es considera silenci (D-030).</summary>
    public double SilenceDb { get; init; } = RadioStreamReader.DefaultSilenceDb;
}

/// <summary>Una cosa que ha passat amb un stream mentre sonava (per a les incidències).</summary>
/// <param name="Cut">true = s'ha tallat (i es prova de reconnectar); false = s'ha recuperat.</param>
/// <param name="Message">Per què s'ha tallat, o quant ha durat el tall.</param>
public sealed record StreamNotice(bool Cut, string Message, TimeSpan Gap);

/// <summary>
/// Omple una veu del mesclador amb un stream de ràdio (fase 2, pas 7b; D-030, D-175), en un fil propi (mai el fil d'àudio).
/// - Abans de començar es connecta (amb la cua, uns segons abans: la connexió anticipada) i va guardant el que arriba,
///   però **no l'escriu a la veu fins just abans de l'hora** (<see cref="FillBefore"/>): així el que sona és el més recent
///   i l'espera no hi afegeix retard. Amb <see cref="StreamOptions.LiveEdge"/> en guarda només el marge.
/// - És a punt quan té prou àudio (el marge). Si no es pot connectar en <see cref="StreamOptions.ConnectFor"/>, falla (la
///   cua el salta).
/// - Mentre sona, si es talla, torna a provar de connectar-se tota l'estona (ho avisa a <see cref="TakeNotices"/>); qui
///   decideix quan es dona per perdut és la cua (pista encallada).
/// - No s'acaba sol: l'acaba la cua (durada, silenci o «Següent»).
/// </summary>
public sealed class StreamVoiceReader : ILiveVoiceReader
{
    /// <summary>Quant abans de l'hora d'inici es comença a omplir la veu.</summary>
    public static readonly TimeSpan FillBefore = TimeSpan.FromMilliseconds(500);
    /// <summary>Quant d'àudio es deixa com a molt a la veu (la resta espera al lector, on es pot retallar).</summary>
    private static readonly TimeSpan VoiceAhead = TimeSpan.FromSeconds(1);
    /// <summary>Amb «Comença pel directe», si s'acumula més d'això per sobre del marge (la xarxa s'ha encallat), es retalla.</summary>
    private static readonly TimeSpan MaxBacklog = TimeSpan.FromSeconds(2);

    private readonly Uri _url;
    private readonly int _sampleRate;
    private readonly StreamOptions _options;
    private readonly Func<long> _now;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _reading = new(true);
    private readonly ConcurrentQueue<StreamNotice> _notices = new();
    private readonly Thread _thread;
    private readonly object _voiceGate = new();
    private bool _detached;
    private int _state = (int)ReaderState.Opening;
    private string? _error;
    private RadioStreamReader? _stream;

    /// <param name="now">El fotograma del mesclador ara (per saber quan comença la veu).</param>
    public StreamVoiceReader(MixerVoice voice, Uri url, int sampleRate, StreamOptions options, Func<long> now)
    {
        Voice = voice;
        _url = url;
        _sampleRate = sampleRate;
        _options = options;
        _now = now;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Stream veu {voice.Index}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public MixerVoice Voice { get; }

    public ReaderState State => (ReaderState)Volatile.Read(ref _state);

    public string? Error => Volatile.Read(ref _error);

    /// <summary>La durada prevista (un stream no en té: és fins on ha de sonar).</summary>
    public TimeSpan? FileDuration => _options.Duration;

    /// <summary>Quant fa que arriba en silenci, seguit (de la connexió d'ara).</summary>
    public TimeSpan SilentFor => Volatile.Read(ref _stream)?.SilentFor ?? TimeSpan.Zero;

    /// <summary>L'últim títol que ha enviat el servidor, o null.</summary>
    public string? Title => Volatile.Read(ref _stream)?.Title;

    /// <summary>Si ara està connectat i rebent.</summary>
    public bool Connected => Volatile.Read(ref _stream)?.State == RadioStreamState.Receiving;

    /// <summary>Els talls i les recuperacions des de l'última vegada (per a les incidències).</summary>
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
        string? cutReason = null;
        try
        {
            for (;;)
            {
                token.ThrowIfCancellationRequested();
                var stream = Connect(token);
                if (stream is null)
                {
                    // No s'ha pogut connectar. Abans de començar: es torna a provar fins a ConnectFor; després, falla.
                    if (!Started && State == ReaderState.Opening && connecting.Elapsed >= _options.ConnectFor)
                    {
                        Volatile.Write(ref _error, _lastError ?? "No s'ha pogut connectar al stream.");
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
                var arrival = new Arrival();
                _filled = false;
                while (stream.State == RadioStreamState.Receiving || (Started && stream.Buffer.Count > 0))
                {
                    token.ThrowIfCancellationRequested();
                    _reading.Wait(token);
                    arrival.Update(stream.Received);
                    Pump(stream, chunk);
                    // A punt quan ja s'ha rebut la ràfega del servidor (l'àudio arriba al ritme del directe) i hi ha el que cal
                    if (State == ReaderState.Opening && arrival.CaughtUp && stream.Buffer.Count >= Frames(Keep(stream)) * 2)
                        Volatile.Write(ref _state, (int)ReaderState.Ready);
                    token.WaitHandle.WaitOne(10);
                }

                // S'ha tallat: es torna a connectar (si ja sonava, s'avisa; la cua decideix quan es dona per perdut)
                cutReason = stream.Error ?? "El stream s'ha tallat.";
                Volatile.Write(ref _stream, null);
                stream.Dispose();
                _lastError = cutReason;
                if (State != ReaderState.Opening)
                {
                    cutSince ??= Stopwatch.StartNew();
                    _notices.Enqueue(new StreamNotice(true, cutReason, TimeSpan.Zero));
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
            Volatile.Write(ref _error, $"Error en llegir el stream: {ex.Message}");
            Volatile.Write(ref _state, (int)ReaderState.Failed);
            MarkVoiceFinished();
        }
        finally
        {
            Volatile.Read(ref _stream)?.Dispose();
            Volatile.Write(ref _stream, null);
        }
    }

    private string? _lastError;

    /// <summary>Per què ha fallat l'últim intent de connexió (per dir-ho si es dona per perdut abans que acabi de provar-ho), o null.</summary>
    public string? LastError => Volatile.Read(ref _lastError);
    /// <summary>Si ja s'ha començat a omplir la veu amb aquesta connexió.</summary>
    private bool _filled;

    /// <summary>
    /// Si l'àudio ja arriba al ritme del directe: en connectar-s'hi, el servidor n'envia uns segons de cop (la ràfega), molt
    /// més de pressa que el temps real. Es mira cada 250 ms quant n'ha arribat: menys d'1,5 vegades el temps passat (i
    /// almenys 300 ms després de connectar-se) vol dir que ja s'ha acabat la ràfega i el que arriba és el directe.
    /// </summary>
    private sealed class Arrival
    {
        private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private TimeSpan _markAt;
        private TimeSpan _markReceived;

        public bool CaughtUp { get; private set; }

        public void Update(TimeSpan received)
        {
            var now = _watch.Elapsed;
            if (now - _markAt < Window) return;
            double audio = (received - _markReceived).TotalSeconds;
            double elapsed = (now - _markAt).TotalSeconds;
            if (now >= TimeSpan.FromMilliseconds(300) && audio < elapsed * 1.5) CaughtUp = true;
            _markAt = now;
            _markReceived = received;
        }
    }

    /// <summary>Es connecta i espera la capçalera. Null si no s'ha pogut (el motiu queda a <c>_lastError</c>).</summary>
    private RadioStreamReader? Connect(CancellationToken token)
    {
        var stream = new RadioStreamReader(_url, _sampleRate, TimeSpan.FromSeconds(30), _options.Timeout, _options.SilenceDb);
        while (stream.State == RadioStreamState.Connecting)
        {
            if (token.WaitHandle.WaitOne(20))
            {
                stream.Dispose();
                token.ThrowIfCancellationRequested();
            }
        }
        if (stream.State != RadioStreamState.Receiving && stream.Buffer.Count == 0)
        {
            _lastError = stream.Error;
            stream.Dispose();
            return null;
        }
        Volatile.Write(ref _stream, stream);
        return stream;
    }

    /// <summary>Quant àudio es guarda abans de començar: el marge, o (sense «Comença pel directe») el que envia el servidor de cop.</summary>
    private TimeSpan Keep(RadioStreamReader stream) =>
        _options.LiveEdge ? _options.Margin : (stream.Burst > _options.Margin ? stream.Burst : _options.Margin);

    /// <summary>Passa àudio del lector a la veu, o el retalla mentre s'espera l'hora.</summary>
    private void Pump(RadioStreamReader stream, float[] chunk)
    {
        var buffer = stream.Buffer;
        long keep = Frames(Keep(stream)) * 2;
        if (!Started)
        {
            long start = Voice.ScheduledStartFrame;
            bool soon = start >= 0 && start - _now() <= Frames(FillBefore);
            if (!soon || !_filled)
            {
                // Encara no: només es guarda el més recent (l'espera no ha d'afegir retard). I just abans d'omplir la
                // veu per primera vegada, també: el que hi entra és el més recent.
                if (buffer.Count > keep) buffer.Skip((int)(buffer.Count - keep));
                if (!soon) return;
                _filled = true;
            }
        }
        else if (_options.LiveEdge && buffer.Count > keep + Frames(MaxBacklog) * 2)
        {
            // Sonant, s'ha acumulat massa (la xarxa s'havia encallat i després ha arribat de cop): es torna al directe
            buffer.Skip((int)(buffer.Count - keep));
        }

        // A la veu, com a molt VoiceAhead per davant
        long room = Frames(VoiceAhead) * 2 - Voice.Buffer.Count;
        while (room > 0 && buffer.Count > 0)
        {
            int n = buffer.Read(chunk.AsSpan(0, (int)Math.Min(chunk.Length, room)));
            if (n == 0) break;
            if (!Write(chunk.AsSpan(0, n))) throw new OperationCanceledException();
            room -= n;
        }
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
                if (n == 0) break; // plena: el que no hi cap es perd (no passa: s'hi escriu com a molt VoiceAhead)
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
