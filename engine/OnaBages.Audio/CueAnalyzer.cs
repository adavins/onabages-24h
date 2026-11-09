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
    /// <summary>Analitza un fitxer sencer, a la seva freqüència original.</summary>
    /// <returns>Els punts, o null si la pista és tota silenci (cap mostra arriba al llindar de final).</returns>
    /// <exception cref="AudioFileException">Si el fitxer no es pot llegir.</exception>
    public static TrackCuePoints? AnalyzeFile(string path, CueThresholds? thresholds = null, CancellationToken cancel = default)
    {
        using var source = AudioFileSource.Open(path, 0); // 0 = la freqüència del fitxer
        var scanner = new CueScanner(source.SampleRate, thresholds ?? CueThresholds.Default);
        var buffer = new float[source.SampleRate * 2]; // mig segon d'estèreo
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            scanner.Push(buffer.AsSpan(0, read));
        }
        return scanner.Result();
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

    public CueScanner(int sampleRate, CueThresholds thresholds)
    {
        _sampleRate = sampleRate;
        _start = ToLinear(thresholds.StartDb);
        _mix = ToLinear(thresholds.MixDb);
        _end = ToLinear(thresholds.EndDb);
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
