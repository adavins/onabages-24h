using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// Salt exacte dins d'un MP3 (fase 5, pas 34; D-155). El salt de Media Foundation (<c>CurrentTime</c>) fa servir la taula
/// aproximada de l'etiqueta Xing (o el bitrate mitjà): en un MP3 de bitrate variable s'equivoca fins a més de 2 s. Aquí,
/// en canvi, l'índex de fotogrames (<see cref="Mp3FrameIndex"/>, el de l'editor) diu on comença cada fotograma: es
/// descodifica des d'uns quants fotogrames abans del punt (perquè el descodificador s'assenti) i es descarten les mostres
/// fins a arribar-hi.
///
/// Media Foundation, llegint el fitxer des del principi, no dona exactament les mateixes mostres al mateix temps que
/// llegint-lo des d'un fotograma del mig (el retard del descodificador i el de l'etiqueta LAME). Aquesta diferència és
/// fixa per a cada fitxer: es mesura una vegada comparant les dues lectures en un tros amb so (<see cref="Calibrate"/>) i
/// es guarda amb l'índex. Si no es pot mesurar, no es fa servir aquest camí (es fa el salt de sempre).
/// </summary>
public static class Mp3Seek
{
    /// <summary>Fotogrames que es descodifiquen abans del punt (descodificador i «bit reservoir»). Uns 200 ms.</summary>
    private const int PrerollFrames = 8;

    /// <summary>Índexs dels últims fitxers on s'ha saltat (amb la diferència mesurada). Pocs: un per reproductor més o menys.</summary>
    private const int CacheSize = 8;

    private sealed record Entry(string Path, long Size, DateTime LastWrite, Mp3FrameIndex Index, int? DelayFrames);

    private static readonly Lock CacheLock = new();
    private static readonly List<Entry> Cache = [];

    /// <summary>
    /// Obre <paramref name="path"/> per llegir-lo a partir de <paramref name="startAt"/>, a la mostra exacta respecte de
    /// llegir-lo des del principi. Null si no es pot (no és un MP3 que l'índex entengui, o no s'ha pogut calibrar).
    /// </summary>
    public static AudioFileSource? TryOpen(string path, int targetSampleRate, TimeSpan startAt)
    {
        var entry = Get(path);
        if (entry?.DelayFrames is not { } delay) return null;
        return OpenAt(entry.Index, path, targetSampleRate, startAt, delay);
    }

    private static readonly HashSet<string> Preparing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prepara en segon pla l'índex i la calibració d'un MP3 (la primera vegada triguen uns 0,3 s; després, saltar és
    /// cosa de mil·lisegons). El reproductor la crida en preparar una pista: quan es salti, ja hi serà.
    /// </summary>
    public static void Prepare(string path)
    {
        if (!path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return;
        lock (CacheLock)
        {
            if (Cache.Exists(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) || !Preparing.Add(path)) return;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Get(path); }
            catch { /* es tornarà a provar en saltar */ }
            finally { lock (CacheLock) Preparing.Remove(path); }
        });
    }

    /// <summary>
    /// Lectura des d'uns quants fotogrames abans de <paramref name="startAt"/>, descartant fins al punt.
    /// <paramref name="delayFrames"/>: mostres (a la freqüència del fitxer) que cal saltar de més respecte del temps del
    /// fotograma (la diferència fixa amb la lectura des del principi).
    /// </summary>
    private static AudioFileSource? OpenAt(Mp3FrameIndex index, string path, int targetSampleRate, TimeSpan startAt, int delayFrames)
    {
        int fileRate = index.SampleRate;
        long target = (long)Math.Round(startAt.TotalSeconds * fileRate) + delayFrames;
        if (target < 0 || index.FrameCount == 0) return null;
        int frame = (int)Math.Min(index.FrameCount - 1, target / index.SamplesPerFrame);
        int first = Math.Max(0, frame - PrerollFrames);
        long skipFileFrames = target - (long)first * index.SamplesPerFrame;

        // La durada és la de sempre (la de Media Foundation per a tot el fitxer): la resta del programa hi compta.
        TimeSpan duration;
        using (var whole = new MediaFoundationReader(path)) duration = whole.TotalTime;

        long end = index.Id3v1Offset > 0 ? index.Id3v1Offset : index.FileSize;
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024);
        try
        {
            var part = new PartStream(file, index.Offset(first), end);
            var reader = new StreamMediaFoundationReader(part);
            return AudioFileSource.FromReader(reader, targetSampleRate, TimeSpan.FromSeconds((double)skipFileFrames / fileRate), duration);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static Entry? Get(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); } catch { return null; }
        if (!info.Exists) return null;

        lock (CacheLock)
        {
            var cached = Cache.Find(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (cached is not null && cached.Size == info.Length && cached.LastWrite == info.LastWriteTimeUtc) return cached;
        }

        Entry entry;
        try
        {
            var index = Mp3FrameIndex.Build(path);
            entry = new Entry(path, info.Length, info.LastWriteTimeUtc, index, Calibrate(index, path));
        }
        catch
        {
            entry = new Entry(path, info.Length, info.LastWriteTimeUtc, null!, null);
        }

        lock (CacheLock)
        {
            Cache.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            Cache.Add(entry);
            if (Cache.Count > CacheSize) Cache.RemoveAt(0);
        }
        return entry;
    }

    /// <summary>
    /// Diferència fixa (mostres del fitxer) entre llegir des del principi i llegir des d'un fotograma: es busca en un
    /// tros amb so dels primers minuts, comparant les dues lectures. Null si no s'ha trobat cap tros prou clar.
    /// </summary>
    private static int? Calibrate(Mp3FrameIndex index, string path)
    {
        int rate = index.SampleRate;
        foreach (double seconds in new[] { 5.0, 20, 45, 90 })
        {
            if (seconds + 3 > index.Duration.TotalSeconds) break;
            // Referència: des del principi (com quan la pista sona sencera).
            var reference = Mono(Read(AudioFileSource.OpenWithMediaFoundation(path, 0, TimeSpan.FromSeconds(seconds - 0.05), exactStart: true), rate / 5));
            // Des del fotograma, sense cap correcció: on cau dins de la referència?
            var part = OpenAt(index, path, 0, TimeSpan.FromSeconds(seconds), 0);
            if (part is null) return null;
            var probe = Mono(Read(part, rate / 20));
            if (Locate(probe, reference) is { } lag) return lag - rate / 20; // la referència començava 50 ms abans
        }
        return null;
    }

    private static float[] Read(AudioFileSource source, int frames)
    {
        using (source)
        {
            var buffer = new float[frames * 2];
            int got = 0, n;
            while (got < buffer.Length && (n = source.Read(buffer.AsSpan(got))) > 0) got += n;
            return buffer[..got];
        }
    }

    private static double[] Mono(float[] stereo)
    {
        var mono = new double[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = stereo[i * 2] + stereo[i * 2 + 1];
        return mono;
    }

    /// <summary>On cau <paramref name="x"/> dins de <paramref name="y"/>, si s'hi assembla prou (correlació ≥ 0,98).</summary>
    private static int? Locate(double[] x, double[] y)
    {
        if (x.Length == 0 || y.Length < x.Length) return null;
        double ex = 0;
        foreach (double v in x) ex += v * v;
        if (ex < 1e-4) return null; // silenci: no es pot comparar
        double best = -1;
        int bestLag = 0;
        for (int lag = 0; lag + x.Length <= y.Length; lag++)
        {
            double dot = 0, ey = 0;
            for (int i = 0; i < x.Length; i++) { dot += x[i] * y[lag + i]; ey += y[lag + i] * y[lag + i]; }
            double score = ey > 0 ? dot / Math.Sqrt(ex * ey) : 0;
            if (score > best) { best = score; bestLag = lag; }
        }
        return best >= 0.98 ? bestLag : null;
    }

    /// <summary>Un tros d'un fitxer (de <c>start</c> a <c>end</c>) com si fos un fitxer sencer. Només lectura.</summary>
    private sealed class PartStream(FileStream file, long start, long end) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => end - start;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, Length);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, Length - _position);
            if (n <= 0) return 0;
            file.Position = start + _position;
            int read = file.Read(buffer, offset, n);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => Length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) file.Dispose();
            base.Dispose(disposing);
        }
    }
}
