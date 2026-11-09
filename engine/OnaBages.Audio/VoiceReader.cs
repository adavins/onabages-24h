namespace OnaBages.Audio;

/// <summary>Estat d'un <see cref="VoiceReader"/>.</summary>
public enum ReaderState
{
    /// <summary>Obrint el fitxer.</summary>
    Opening,
    /// <summary>Ja hi ha prou dades a la veu per començar a sonar (o totes, si la pista és curta).</summary>
    Ready,
    /// <summary>Ha escrit totes les dades a la veu.</summary>
    Finished,
    /// <summary>No s'ha pogut obrir o llegir el fitxer (<see cref="VoiceReader.Error"/>).</summary>
    Failed,
}

/// <summary>
/// Llegeix un fitxer d'àudio en un fil propi i omple la memòria d'una veu del mesclador (docs/disseny-motor.md, 2):
/// - comença al punt d'inici de la pista (salt exacte, també en MP3: D-155 de Play) i s'atura al punt final;
/// - si el tros que ha de sonar dura menys de <see cref="MemoryLimit"/>, el llegeix sencer a memòria i tanca el
///   fitxer de seguida (D-018: així es pot substituir mentre sona);
/// - si la memòria de la veu és plena, espera i torna a provar. No toca mai el fil d'àudio.
/// Un error en aquest fitxer no afecta cap altra veu: el lector queda a <see cref="ReaderState.Failed"/>.
/// </summary>
public sealed class VoiceReader : IDisposable
{
    /// <summary>Les pistes més curtes que això es llegeixen senceres a memòria (D-018).</summary>
    public static readonly TimeSpan MemoryLimit = TimeSpan.FromMinutes(3);

    /// <summary>Dades mínimes a la veu per considerar-la a punt (o totes, si n'hi ha menys).</summary>
    public static readonly TimeSpan ReadyAfter = TimeSpan.FromSeconds(1);

    private readonly MixerVoice _voice;
    private readonly string _path;
    private readonly int _sampleRate;
    private readonly TimeSpan _startAt;
    private readonly TimeSpan? _endAt;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private int _state = (int)ReaderState.Opening;
    private string? _error;
    private long _durationTicks = -1;

    /// <summary>Comença a llegir en un fil nou.</summary>
    /// <param name="startAt">Punt del fitxer on comença (normalment, l'inici de la pista).</param>
    /// <param name="endAt">Punt del fitxer on s'acaba (null = fins al final del fitxer).</param>
    public VoiceReader(MixerVoice voice, string path, int sampleRate, TimeSpan startAt, TimeSpan? endAt)
    {
        _voice = voice;
        _path = path;
        _sampleRate = sampleRate;
        _startAt = startAt < TimeSpan.Zero ? TimeSpan.Zero : startAt;
        _endAt = endAt;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Lectura veu {voice.Index}",
            Priority = ThreadPriority.AboveNormal, // per davant de l'anàlisi; mai el fil d'àudio
        };
        _thread.Start();
    }

    public MixerVoice Voice => _voice;

    public string Path => _path;

    public ReaderState State => (ReaderState)Volatile.Read(ref _state);

    /// <summary>Per què ha fallat (en català), o null.</summary>
    public string? Error => Volatile.Read(ref _error);

    /// <summary>Durada del fitxer sencer, quan ja s'ha obert (null abans).</summary>
    public TimeSpan? FileDuration
    {
        get
        {
            long ticks = Interlocked.Read(ref _durationTicks);
            return ticks < 0 ? null : TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>Espera (com a molt <paramref name="timeout"/>) que estigui a punt, acabat o fallat. Per a les proves i les eines.</summary>
    public bool WaitUntilReady(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (State == ReaderState.Opening && DateTime.UtcNow < until)
            Thread.Sleep(2);
        return State != ReaderState.Opening;
    }

    private void Run()
    {
        try
        {
            using var source = AudioFileSource.Open(_path, _sampleRate, _startAt, exactStart: true);
            Interlocked.Exchange(ref _durationTicks, source.Duration.Ticks);

            TimeSpan end = _endAt is { } e && e < source.Duration ? e : source.Duration;
            long totalSamples = (long)Math.Max(0, Math.Round((end - _startAt).TotalSeconds * _sampleRate)) * 2;
            long readySamples = Math.Min(totalSamples, (long)(ReadyAfter.TotalSeconds * _sampleRate) * 2);

            if (end - _startAt <= MemoryLimit)
            {
                // Pista curta: tota a memòria i el fitxer, tancat (D-018)
                var all = new float[totalSamples];
                int filled = 0;
                while (filled < all.Length)
                {
                    _stop.Token.ThrowIfCancellationRequested();
                    int read = source.Read(all.AsSpan(filled, Math.Min(65536, all.Length - filled)));
                    if (read == 0) break;
                    filled += read;
                }
                source.Dispose();
                Feed(all.AsSpan(0, filled), readySamples);
            }
            else
            {
                Stream(source, totalSamples, readySamples);
            }

            _voice.MarkSourceFinished();
            Volatile.Write(ref _state, (int)ReaderState.Finished);
        }
        catch (OperationCanceledException)
        {
            _voice.MarkSourceFinished(); // ja no hi haurà més dades: que la veu s'acabi, si sonava
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _error, ex is AudioFileException ? ex.Message : $"No s'ha pogut llegir {System.IO.Path.GetFileName(_path)}: {ex.Message}");
            _voice.MarkSourceFinished();
            Volatile.Write(ref _state, (int)ReaderState.Failed);
        }
    }

    /// <summary>Escriu les dades de memòria a la veu, a mesura que hi caben.</summary>
    private void Feed(ReadOnlySpan<float> samples, long readySamples)
    {
        int written = 0;
        while (written < samples.Length)
        {
            _stop.Token.ThrowIfCancellationRequested();
            written += _voice.Buffer.Write(samples[written..]);
            MarkReadyIf(written >= readySamples);
            if (written < samples.Length) Thread.Sleep(10); // la memòria de la veu és plena: que en consumeixi
        }
        MarkReadyIf(true);
    }

    /// <summary>Pista llarga: es va llegint del fitxer a mesura que la veu en consumeix.</summary>
    private void Stream(AudioFileSource source, long totalSamples, long readySamples)
    {
        var chunk = new float[_sampleRate / 10 * 2]; // 100 ms
        long done = 0;
        while (done < totalSamples)
        {
            _stop.Token.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(chunk.Length, totalSamples - done);
            int read = source.Read(chunk.AsSpan(0, wanted));
            if (read == 0) break;
            int written = 0;
            while (written < read)
            {
                _stop.Token.ThrowIfCancellationRequested();
                written += _voice.Buffer.Write(chunk.AsSpan(written, read - written));
                if (written < read) Thread.Sleep(10);
            }
            done += read;
            MarkReadyIf(done >= readySamples);
        }
        MarkReadyIf(true);
    }

    private void MarkReadyIf(bool condition)
    {
        if (condition) Interlocked.CompareExchange(ref _state, (int)ReaderState.Ready, (int)ReaderState.Opening);
    }

    /// <summary>Atura la lectura (si encara llegia) i espera que el fil acabi. Després, la veu es pot alliberar.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
