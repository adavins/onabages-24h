using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages24.Tests;

/// <summary>
/// Punts d'encadenament (fase 1, pas 3). Es fan servir senyals sintètics amb un nivell conegut a cada moment,
/// de manera que se sap exactament on ha de caure cada punt.
/// </summary>
public class CueAnalyzerTests
{
    private const int Rate = 44100;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// Senyal de prova: tros a tros, amb el nivell en dB de cada tros (null = silenci). Dins de cada tros el nivell
    /// pot anar d'un valor a un altre en línia recta (en dB), com un fos. Les mostres alternen el signe
    /// (+a, −a…), perquè el pic de cada mostra sigui exactament el nivell demanat.
    /// </summary>
    private static float[] Signal(params (double Seconds, double? FromDb, double? ToDb)[] parts)
    {
        var samples = new List<float>();
        foreach (var (seconds, fromDb, toDb) in parts)
        {
            int frames = (int)Math.Round(seconds * Rate);
            for (int i = 0; i < frames; i++)
            {
                float a = 0;
                if (fromDb is double from)
                {
                    double to = toDb ?? from;
                    double db = from + (to - from) * i / frames;
                    a = (float)Math.Pow(10, db / 20.0);
                }
                float v = (samples.Count / 2) % 2 == 0 ? a : -a;
                samples.Add(v);
                samples.Add(v);
            }
        }
        return samples.ToArray();
    }

    private static TrackCuePoints? Scan(float[] stereo, CueThresholds? thresholds = null)
    {
        var scanner = new CueScanner(Rate, thresholds ?? CueThresholds.Default);
        // A trossos irregulars, com arriben de la lectura d'un fitxer
        for (int pos = 0; pos < stereo.Length; pos += 1234 * 2)
            scanner.Push(stereo.AsSpan(pos, Math.Min(1234 * 2, stereo.Length - pos)));
        return scanner.Result();
    }

    private static void Near(double expectedSeconds, TimeSpan actual) =>
        Assert.InRange(actual, TimeSpan.FromSeconds(expectedSeconds) - Tolerance, TimeSpan.FromSeconds(expectedSeconds) + Tolerance);

    [Fact]
    public void Silenci_inicial_cos_i_fos_final_donen_els_tres_punts()
    {
        // 1 s de silenci, 10 s a −6 dB, fos de −6 a −40 dB en 5 s, 1 s de silenci
        var cues = Scan(Signal((1, null, null), (10, -6, -6), (5, -6, -40), (1, null, null)));

        Assert.NotNull(cues);
        Near(1.0, cues.Start);
        // El fos baixa 34 dB en 5 s: passa per −15 dB al cap de 9/34 · 5 s, i per −30 dB al cap de 24/34 · 5 s
        Near(11 + 9.0 / 34 * 5, cues.Mix);
        Near(11 + 24.0 / 34 * 5, cues.End);
        Near(24.0 / 34 * 5 - 9.0 / 34 * 5, cues.FadeOut);
    }

    [Fact]
    public void Una_pista_tota_en_silenci_no_te_punts()
    {
        Assert.Null(Scan(Signal((5, null, null))));
        Assert.Null(Scan(Signal((5, -45, -45)))); // per sota del llindar de final
    }

    [Fact]
    public void Una_pista_que_acaba_de_cop_no_se_solapa()
    {
        var cues = Scan(Signal((0.5, null, null), (8, -3, -3)));

        Assert.NotNull(cues);
        Near(0.5, cues.Start);
        Near(8.5, cues.End);
        Assert.Equal(cues.End, cues.Mix);
        Assert.Equal(TimeSpan.Zero, cues.FadeOut);
    }

    [Fact]
    public void Una_pista_molt_fluixa_comenca_on_hi_ha_so_i_no_se_solapa()
    {
        // −28 dB: per sota de l'inici (−25) i de la mescla (−15), però per sobre del final (−30)
        var cues = Scan(Signal((2, null, null), (6, -28, -28), (1, null, null)));

        Assert.NotNull(cues);
        Near(2.0, cues.Start);
        Near(8.0, cues.End);
        Assert.Equal(cues.End, cues.Mix);
    }

    [Fact]
    public void Una_entrada_suau_comenca_quan_supera_el_llindar_d_inici()
    {
        // Entrada de −60 a −6 dB en 3 s: passa per −25 dB al cap de 35/54 · 3 s
        var cues = Scan(Signal((3, -60, -6), (5, -6, -6)));

        Assert.NotNull(cues);
        Near(35.0 / 54 * 3, cues.Start);
        Near(8.0, cues.End);
    }

    [Fact]
    public void Els_llindars_es_poden_canviar()
    {
        var signal = Signal((10, -6, -6), (5, -6, -40));

        var perDefecte = Scan(signal)!;
        var mesclaMesTard = Scan(signal, new CueThresholds(MixDb: -20))!;

        // Amb −20 dB, el punt de mescla arriba més tard dins del fos (14/34 · 5 s en lloc de 9/34 · 5 s)
        Near(10 + 14.0 / 34 * 5, mesclaMesTard.Mix);
        Assert.True(mesclaMesTard.Mix > perDefecte.Mix);
        Assert.Equal(perDefecte.End, mesclaMesTard.End);
    }

    [Fact]
    public void Un_cop_curt_al_mig_d_un_silenci_final_allarga_el_final()
    {
        // Comportament esperat (igual que el càlcul de RadioDJ): un soroll per sobre de −30 dB després del fos compta com a so
        var cues = Scan(Signal((5, -6, -6), (2, null, null), (0.1, -20, -20), (1, null, null)))!;

        Near(5.0, cues.Mix);
        Near(7.1, cues.End);
    }

    [Fact]
    public void Analitza_un_fitxer_WAV()
    {
        string path = Path.Combine(Path.GetTempPath(), "OnaBagesTests", $"punts-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var samples = Signal((1, null, null), (10, -6, -6), (5, -6, -40), (1, null, null));
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
            writer.WriteSamples(samples, 0, samples.Length);

        try
        {
            var cues = CueAnalyzer.AnalyzeFile(path);

            Assert.NotNull(cues);
            Near(1.0, cues.Start);
            Near(11 + 9.0 / 34 * 5, cues.Mix);
            Near(11 + 24.0 / 34 * 5, cues.End);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Un_fitxer_que_no_existeix_dona_un_error_d_audio()
    {
        Assert.Throws<AudioFileException>(() => CueAnalyzer.AnalyzeFile(@"C:\no-existeix\pista.mp3"));
    }

    [Fact]
    public void L_analisi_es_pot_cancel_lar()
    {
        string path = TestAudio.CreateWav(Rate, 2, 3);
        try
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => CueAnalyzer.AnalyzeFile(path, cancel: cancel.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
