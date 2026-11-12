namespace OnaBages.Audio;

/// <summary>
/// Calcula els punts d'encadenament d'una pista (<see cref="TrackCuePoints"/>) a partir del nivell de les mostres:
/// - inici: la <b>primera</b> mostra que supera el llindar d'inici (−25 dB);
/// - mescla: l'<b>última</b> mostra que supera el llindar de mescla (−15 dB);
/// - final: l'<b>última</b> mostra que supera el llindar de final (−30 dB).
///
/// Es mira el valor absolut de cada mostra de qualsevol dels dos canals (nivell de pic).
/// La lectura del fitxer va al fil que el cridi (mai el fil d'àudio): normalment, el d'anàlisi en segon pla.
/// </summary>
public static class CueAnalyzer
{
    /// <summary>Segons del principi que mira l'anàlisi ràpida (l'inici sol ser als primers segons).</summary>
    public const double HeadSeconds = 30;

    /// <summary>Segons del final que mira l'anàlisi ràpida (la mescla i el final hi són gairebé sempre).</summary>
    public const double TailSeconds = 60;

    /// <summary>
    /// Analitza un fitxer, a la seva freqüència original. Amb un MP3 prou llarg, només en descodifica el principi i el final
    /// (Ona Bages 24h, fase 3, pas 7b; D-089): el resultat és exactament el mateix que descodificant-lo sencer, perquè l'inici
    /// és la <i>primera</i> mostra per sobre del llindar i la mescla i el final les <i>últimes</i>. Si no es troben dins dels
    /// trossos mirats (p. ex. una cançó amb un final molt llarg i fluix), es descodifica sencer.
    /// </summary>
    /// <returns>Els punts, o null si la pista és tota silenci (cap mostra arriba al llindar de final).</returns>
    /// <exception cref="AudioFileException">Si el fitxer no es pot llegir.</exception>
    public static TrackCuePoints? AnalyzeFile(string path, CueThresholds? thresholds = null, CancellationToken cancel = default)
    {
        var t = thresholds ?? CueThresholds.Default;
        return TryAnalyzeEnds(path, t, cancel, out var cues) ? cues : AnalyzeWhole(path, t, cancel);
    }

    /// <summary>Analitza un fitxer sencer, descodificant-lo de cap a peus (el càlcul de sempre).</summary>
    public static TrackCuePoints? AnalyzeWhole(string path, CueThresholds? thresholds = null, CancellationToken cancel = default)
    {
        using var source = AudioFileSource.Open(path, 0); // 0 = la freqüència del fitxer
        var scanner = new CueScanner(source.SampleRate, thresholds ?? CueThresholds.Default);
        Scan(source, scanner, long.MaxValue, stopWhenStartFound: false, cancel);
        return scanner.Result();
    }

    /// <summary>
    /// L'anàlisi ràpida: el principi (fins a trobar l'inici, com a molt <see cref="HeadSeconds"/>) i els últims
    /// <see cref="TailSeconds"/>, amb el salt exacte dels MP3 (<see cref="Mp3Seek"/>). False si no es pot fer o si els punts
    /// no hi són (llavors cal descodificar-lo sencer).
    /// </summary>
    public static bool TryAnalyzeEnds(string path, CueThresholds thresholds, CancellationToken cancel, out TrackCuePoints? cues)
    {
        cues = null;
        if (!Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return false;

        using var head = AudioFileSource.Open(path, 0);
        int rate = head.SampleRate;
        double seconds = head.Duration.TotalSeconds;
        if (seconds < HeadSeconds + TailSeconds + 10) return false; // curta: sencera ja és ràpid

        var headScanner = new CueScanner(rate, thresholds);
        Scan(head, headScanner, (long)(HeadSeconds * rate), stopWhenStartFound: true, cancel);
        if (headScanner.FirstStart < 0) return false; // comença molt fluix: sencera

        // El tros final comença en una mostra exacta; el salt dels MP3 hi cau exactament (respecte de llegir des del principi)
        long tailStart = (long)Math.Round((seconds - TailSeconds) * rate);
        using var tail = Mp3Seek.TryOpen(path, 0, TimeSpan.FromSeconds((double)tailStart / rate));
        if (tail is null || tail.SampleRate != rate) return false;
        var tailScanner = new CueScanner(rate, thresholds, startFrame: tailStart);
        Scan(tail, tailScanner, long.MaxValue, stopWhenStartFound: false, cancel);
        if (tailScanner.LastMix < 0 || tailScanner.LastEnd < 0) return false; // la mescla és abans: sencera

        cues = CueScanner.Combine(rate, headScanner.FirstStart, tailScanner.LastMix, tailScanner.LastEnd);
        return true;
    }

    /// <summary>Passa les mostres d'una font a l'analitzador, com a molt <paramref name="maxFrames"/>.</summary>
    private static void Scan(AudioFileSource source, CueScanner scanner, long maxFrames, bool stopWhenStartFound, CancellationToken cancel)
    {
        var buffer = new float[source.SampleRate * 2]; // mig segon d'estèreo
        long frames = 0;
        int read;
        while (frames < maxFrames && (read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            scanner.Push(buffer.AsSpan(0, read));
            frames += read / 2;
            if (stopWhenStartFound && scanner.FirstStart >= 0) return;
        }
    }
}

/// <summary>
/// La part de càlcul de <see cref="CueAnalyzer"/>: rep les mostres (estèreo intercalat) a trossos i
/// recorda on ha vist cada llindar. Separat de la lectura del fitxer perquè les proves hi puguin
/// passar mostres sintètiques directament.
/// </summary>
public sealed class CueScanner
{
    private readonly int _sampleRate;
    private readonly float _start, _mix, _end; // llindars en valor lineal (0..1)
    private long _frame;                         // fotogrames (mostres per canal) vistos fins ara
    private long _firstStart = -1;               // primer fotograma per sobre del llindar d'inici
    private long _firstEnd = -1;                 // primer per sobre del de final (per a pistes molt fluixes)
    private long _lastMix = -1, _lastEnd = -1;   // últims per sobre dels llindars de mescla i de final

    /// <param name="startFrame">Fotograma del fitxer on comencen les mostres que s'hi passaran (0 = el principi; l'anàlisi
    /// ràpida passa un tros del final).</param>
    public CueScanner(int sampleRate, CueThresholds thresholds, long startFrame = 0)
    {
        _sampleRate = sampleRate;
        _start = ToLinear(thresholds.StartDb);
        _mix = ToLinear(thresholds.MixDb);
        _end = ToLinear(thresholds.EndDb);
        _frame = startFrame;
    }

    /// <summary>Primer fotograma per sobre del llindar d'inici (−1 si encara no n'hi ha cap).</summary>
    public long FirstStart => _firstStart;

    /// <summary>Últim fotograma per sobre del llindar de mescla (−1 si no n'hi ha cap).</summary>
    public long LastMix => _lastMix;

    /// <summary>Últim fotograma per sobre del llindar de final (−1 si no n'hi ha cap).</summary>
    public long LastEnd => _lastEnd;

    /// <summary>Els punts a partir del primer fotograma d'inici i els últims de mescla i de final (els trobats a l'anàlisi ràpida).</summary>
    public static TrackCuePoints Combine(int sampleRate, long firstStart, long lastMix, long lastEnd)
    {
        long end = lastEnd + 1;
        long mix = Math.Clamp(lastMix + 1, firstStart, end);
        TimeSpan Time(long frame) => TimeSpan.FromSeconds((double)frame / sampleRate);
        return new TrackCuePoints(Time(firstStart), Time(mix), Time(end));
    }

    /// <summary>Afegeix un tros de mostres estèreo intercalades (esquerra, dreta, esquerra…).</summary>
    public void Push(ReadOnlySpan<float> stereo)
    {
        for (int i = 0; i + 1 < stereo.Length; i += 2)
        {
            float level = Math.Max(Math.Abs(stereo[i]), Math.Abs(stereo[i + 1]));
            if (level >= _end)
            {
                if (_firstEnd < 0) _firstEnd = _frame;
                _lastEnd = _frame;
                if (level >= _start && _firstStart < 0) _firstStart = _frame;
                if (level >= _mix) _lastMix = _frame;
            }
            _frame++;
        }
    }

    /// <summary>Els punts calculats amb el que s'ha vist fins ara, o null si tot és silenci.</summary>
    public TrackCuePoints? Result()
    {
        if (_lastEnd < 0) return null; // res no arriba ni al llindar de final

        // Pista molt fluixa (res no arriba al llindar d'inici): comença on comença el so.
        long start = _firstStart >= 0 ? _firstStart : _firstEnd;
        long end = _lastEnd + 1; // el final és just després de l'última mostra amb so
        // Res no arriba al llindar de mescla: la pista següent entra al final, sense solapament.
        long mix = _lastMix >= 0 ? _lastMix + 1 : end;

        mix = Math.Clamp(mix, start, end);
        return new TrackCuePoints(ToTime(start), ToTime(mix), ToTime(end));
    }

    private TimeSpan ToTime(long frame) => TimeSpan.FromSeconds((double)frame / _sampleRate);

    private static float ToLinear(double db) => (float)Math.Pow(10, db / 20.0);
}
