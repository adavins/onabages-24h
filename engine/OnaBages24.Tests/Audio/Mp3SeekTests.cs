using OnaBages.Audio;
using Xunit.Abstractions;

namespace OnaBages24.Tests;

/// <summary>
/// Salt exacte dins d'un MP3 (fase 5, pas 34; D-155). Media Foundation, en un MP3 de bitrate variable, s'equivoca fins a
/// més de 2 s en saltar a mig fitxer; amb l'índex de fotogrames, el salt ha de caure a la mostra exacta respecte de llegir
/// el fitxer seguit des del principi (que és com sona quan es fa sonar sencer).
/// </summary>
public class Mp3SeekTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private const int Rate = 48000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "salt-" + Guid.NewGuid().ToString("N"));

    /// <summary>MP3 real de bitrate variable (el de D-154). Si no hi és, la prova que el fa servir no fa res.</summary>
    private const string RealFile = @"D:\proves\SALUT SALLENT.mp3";

    public Mp3SeekTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static double[] Mono(float[] stereo)
    {
        var mono = new double[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = stereo[i * 2] + stereo[i * 2 + 1];
        return mono;
    }

    private static float[] ReadFrames(AudioFileSource source, int frames)
    {
        var buffer = new float[frames * 2];
        int got = 0, n;
        while (got < buffer.Length && (n = source.Read(buffer.AsSpan(got))) > 0) got += n;
        return buffer[..got];
    }

    /// <summary>
    /// Llegeix el fitxer seguit des del principi una sola vegada i en guarda, per a cada punt, des de 100 ms abans fins a
    /// 300 ms després (la referència: com sona quan es fa sonar sencer).
    /// </summary>
    private static Dictionary<double, double[]> Reference(string path, double[] seconds)
    {
        var result = new Dictionary<double, double[]>();
        using var source = AudioFileSource.Open(path, Rate);
        long position = 0; // mostres per canal llegides
        var chunk = new float[Rate * 2];
        foreach (double s in seconds.Order())
        {
            long from = (long)Math.Round((s - 0.1) * Rate);
            while (position + chunk.Length / 2 <= from)
            {
                int n = source.Read(chunk);
                if (n == 0) break;
                position += n / 2;
            }
            if (from > position) position += source.Read(chunk.AsSpan(0, (int)(from - position) * 2)) / 2;
            result[s] = Mono(ReadFrames(source, Rate * 4 / 10));
            position += Rate * 4 / 10;
        }
        return result;
    }

    /// <summary>Error (mostres) del salt a <paramref name="seconds"/>: on cauen les primeres mostres dins de la referència.</summary>
    private static (int Error, double Score, long OpenMs) SeekError(string path, double seconds, double[] reference)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var source = AudioFileSource.Open(path, Rate, TimeSpan.FromSeconds(seconds));
        var probe = Mono(ReadFrames(source, 4096));
        long openMs = watch.ElapsedMilliseconds; // obrir i llegir les primeres mostres (el que espera el reproductor)
        double ex = probe.Sum(v => v * v);
        int best = 0; double bestScore = -1;
        for (int lag = 0; lag + probe.Length <= reference.Length; lag++)
        {
            double dot = 0, ey = 0;
            for (int i = 0; i < probe.Length; i++) { dot += probe[i] * reference[lag + i]; ey += reference[lag + i] * reference[lag + i]; }
            double score = ey > 0 && ex > 0 ? dot / Math.Sqrt(ex * ey) : 0;
            if (score > bestScore) { bestScore = score; best = lag; }
        }
        return (best - Rate / 10, bestScore, openMs);
    }

    /// <summary>
    /// MP3 de bitrate variable sintètic: 60 s que alternen 3 s fàcils (un to fluix: bitrate baix) i 3 s difícils
    /// (soroll fort amb tons: bitrate alt). Sense etiqueta Xing, com els enregistraments de l'aplicació.
    /// </summary>
    private string VbrFile()
    {
        string path = Path.Combine(_folder, "vbr.mp3");
        var random = new Random(7);
        using var encoder = new LameEncoder(Rate, vbr: true);
        using var file = File.Create(path);
        var block = new float[Rate / 10 * 2];
        for (int b = 0; b < 600; b++)
        {
            bool hard = b / 30 % 2 == 1;
            for (int i = 0; i < block.Length / 2; i++)
            {
                int t = b * (Rate / 10) + i;
                double v = hard
                    ? 0.4 * (random.NextDouble() - 0.5) + 0.2 * Math.Sin(2 * Math.PI * 3100 * t / Rate)
                    : 0.05 * Math.Sin(2 * Math.PI * 330 * t / Rate);
                block[i * 2] = block[i * 2 + 1] = (float)v;
            }
            file.Write(encoder.Encode(block, block.Length));
        }
        file.Write(encoder.Flush());
        return path;
    }

    [Fact]
    public void Amb_un_MP3_de_bitrate_variable_el_salt_cau_a_la_mostra_i_es_rapid()
    {
        string path = VbrFile();
        var analysis = Mp3Frames.Analyze(path);
        Assert.True(analysis.Bitrates.Count >= 3, $"no és VBR: {string.Join(",", analysis.Bitrates)}");

        double[] points = [7.3, 22.15, 41.7, 55.55];
        var reference = Reference(path, points);
        foreach (double s in points)
        {
            var (error, score, ms) = SeekError(path, s, reference[s]);
            _output.WriteLine($"Salt a {s} s: error {error} mostres, correlació {score:F4}, obrir {ms} ms");
            Assert.InRange(error, -1, 1);
            Assert.True(score > 0.99, $"correlació {score}");
        }
    }

    [Fact]
    public void Despres_del_salt_el_fitxer_es_pot_reanomenar_i_esborrar_mentre_sona_com_abans()
    {
        // Com amb Media Foundation (D-091, FileLockTests): es pot treure de la carpeta mentre sona, però no escriure-hi.
        string path = VbrFile();
        using var source = AudioFileSource.Open(path, Rate, TimeSpan.FromSeconds(20));
        ReadFrames(source, Rate);
        Assert.Throws<IOException>(() => File.OpenWrite(path).Dispose());
        string moved = path + ".vell";
        File.Move(path, moved);
        File.Delete(moved);
        Assert.Equal(Rate * 2 * 2, ReadFrames(source, Rate * 2).Length); // continua sonant sencer
    }

    /// <summary>
    /// MP3 real en què llegir des d'un fotograma dona el so 1.152 mostres més endavant que llegir des del principi (D-089).
    /// Només a l'ordinador del desenvolupador; si no hi és, la prova no fa res.
    /// </summary>
    private const string RealFileWithDelay = @"O:\musica\BOSSA 9 ADULT CONTEMPORARY_edit\Aerosmith - Dream On.mp3";

    [Fact]
    public void La_diferencia_del_calibratge_es_compensa_començant_abans()
    {
        // Si des del fotograma el so surt 1.152 mostres més endavant, per tenir el de la mostra 100.000 cal començar a 98.848
        Assert.Equal(98_848, Mp3Seek.TargetSample(100_000, 1152));
        Assert.Equal(100_000, Mp3Seek.TargetSample(100_000, 0));
    }

    [Fact]
    public void Amb_un_MP3_real_amb_diferencia_de_calibratge_el_salt_cau_a_la_mostra()
    {
        if (!File.Exists(RealFileWithDelay)) return; // només a l'ordinador del desenvolupador
        double[] points = [20, 100, 200];
        var reference = Reference(RealFileWithDelay, points);
        foreach (double s in points)
        {
            var (error, score, _) = SeekError(RealFileWithDelay, s, reference[s]);
            _output.WriteLine($"Salt a {s} s: error {error} mostres, correlació {score:F4}");
            Assert.InRange(error, -1, 1); // abans: +2.304 (52 ms)
            Assert.True(score > 0.99, $"correlació {score}");
        }
    }

    [Fact]
    public void Amb_un_MP3_real_de_bitrate_variable_el_salt_cau_a_la_mostra()
    {
        if (!File.Exists(RealFile)) return; // només a l'ordinador de proves
        double[] points = [30, 125, 301, 617, 850];
        var reference = Reference(RealFile, points);
        foreach (double s in points)
        {
            var (error, score, ms) = SeekError(RealFile, s, reference[s]);
            _output.WriteLine($"Salt a {s} s: error {error} mostres ({error * 1000.0 / Rate:F2} ms), correlació {score:F4}, obrir {ms} ms");
            Assert.InRange(error, -1, 1);
            Assert.True(score > 0.99, $"correlació {score}");
        }
    }
}
