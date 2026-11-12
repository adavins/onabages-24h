using OnaBages.Audio;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>
/// Forma d'ona per a l'editor de punts (fase 3, pas 7e; D-093): el pic de cada tros, en una escala de dB (de −60 a 0 dB →
/// de 0 a 255), perquè es vegin els llindars de l'analitzador (−25, −15 i −30 dB; en escala lineal, −30 dB és gairebé
/// res). Només es descodifica el tros demanat (amb el salt exacte dels MP3), no tot el fitxer.
/// </summary>
public static class CueWaveform
{
    /// <summary>Per sota d'aquest nivell, la forma d'ona és buida.</summary>
    public const double FloorDb = -60;

    /// <summary>Com a molt, tants trossos (el que cap en una pantalla).</summary>
    public const int MaxBuckets = 2000;

    /// <summary>
    /// Pics de <paramref name="from"/> a <paramref name="to"/>, en <paramref name="buckets"/> trossos iguals (cada un, de 0 a
    /// 255 en l'escala de dB). La lectura comença exactament a <paramref name="from"/>.
    /// </summary>
    /// <exception cref="AudioFileException">Si el fitxer no es pot llegir.</exception>
    public static byte[] Compute(string path, TimeSpan from, TimeSpan to, int buckets, CancellationToken cancel = default)
    {
        buckets = Math.Clamp(buckets, 1, MaxBuckets);
        var peaks = new float[buckets];
        using var source = AudioFileSource.Open(path, 0, from, exactStart: true);
        int rate = source.SampleRate;
        long total = Math.Max(1, (long)Math.Round((to - from).TotalSeconds * rate));
        var buffer = new float[rate]; // mig segon d'estèreo
        long frame = 0;
        int read;
        while (frame < total && (read = source.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            for (int i = 0; i + 1 < read && frame < total; i += 2, frame++)
            {
                float level = Math.Max(Math.Abs(buffer[i]), Math.Abs(buffer[i + 1]));
                int b = (int)(frame * buckets / total);
                if (level > peaks[b]) peaks[b] = level;
            }
        }
        var result = new byte[buckets];
        for (int b = 0; b < buckets; b++) result[b] = ToByte(peaks[b]);
        return result;
    }

    /// <summary>Un pic lineal a l'escala de la forma d'ona: −60 dB o menys → 0; 0 dB → 255.</summary>
    public static byte ToByte(float peak)
    {
        if (peak <= 0) return 0;
        double db = 20 * Math.Log10(peak);
        return (byte)Math.Clamp((int)Math.Round((db - FloorDb) / -FloorDb * 255), 0, 255);
    }
}
