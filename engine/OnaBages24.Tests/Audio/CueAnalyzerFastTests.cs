using OnaBages.Audio;

namespace OnaBages24.Tests;

/// <summary>
/// Anàlisi ràpida dels punts (fase 3, pas 7b; D-089): en un MP3 llarg només es descodifiquen el principi i el final, i el
/// resultat ha de ser exactament el mateix que descodificant-lo sencer. Si els punts no hi són, es descodifica sencer.
/// </summary>
public class CueAnalyzerFastTests : IDisposable
{
    private const int Rate = 44100;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "OnaBagesTests", "punts-" + Guid.NewGuid().ToString("N"));

    public CueAnalyzerFastTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// MP3 de <paramref name="seconds"/> s: 1 s de silenci, un to (amplitud <paramref name="level"/>) i, als últims
    /// <paramref name="fadeSeconds"/> s, una baixada fins a silenci; al final, 3 s de silenci.
    /// </summary>
    private string Mp3(string name, double seconds, float level, double fadeSeconds)
    {
        string path = Path.Combine(_folder, name);
        using var encoder = new LameEncoder(Rate);
        using var file = File.Create(path);
        int total = (int)(seconds * Rate);
        int toneEnd = total - 3 * Rate;
        int fadeStart = toneEnd - (int)(fadeSeconds * Rate);
        var block = new float[Rate / 10 * 2];
        for (int start = 0; start < total; start += block.Length / 2)
        {
            for (int i = 0; i < block.Length / 2; i++)
            {
                int t = start + i;
                double gain = t < Rate || t >= toneEnd ? 0 : t < fadeStart ? 1 : (double)(toneEnd - t) / (toneEnd - fadeStart);
                block[i * 2] = block[i * 2 + 1] = (float)(level * gain * Math.Sin(2 * Math.PI * 440 * t / Rate));
            }
            file.Write(encoder.Encode(block, block.Length));
        }
        file.Write(encoder.Flush());
        return path;
    }

    [Fact]
    public void En_un_MP3_llarg_la_rapida_dona_el_mateix_que_la_sencera()
    {
        string path = Mp3("llarg.mp3", 150, 0.5f, fadeSeconds: 5);

        Assert.True(CueAnalyzer.TryAnalyzeEnds(path, CueThresholds.Default, default, out var fast));
        var whole = CueAnalyzer.AnalyzeWhole(path);

        Assert.NotNull(fast);
        Assert.Equal(whole, fast);
        Assert.Equal(whole, CueAnalyzer.AnalyzeFile(path));
    }

    [Fact]
    public void Si_el_final_es_massa_fluix_es_descodifica_sencer()
    {
        // To a 0,2 (−14 dB) que baixa durant 100 s: als últims 60 s cap mostra no arriba al llindar de mescla (−15 dB = 0,178)
        string path = Mp3("final-llarg.mp3", 160, 0.2f, fadeSeconds: 100);

        Assert.False(CueAnalyzer.TryAnalyzeEnds(path, CueThresholds.Default, default, out _));
        Assert.Equal(CueAnalyzer.AnalyzeWhole(path), CueAnalyzer.AnalyzeFile(path));
    }

    [Fact]
    public void Un_MP3_curt_es_descodifica_sencer()
    {
        string path = Mp3("curt.mp3", 60, 0.5f, fadeSeconds: 2);
        Assert.False(CueAnalyzer.TryAnalyzeEnds(path, CueThresholds.Default, default, out _));
        Assert.NotNull(CueAnalyzer.AnalyzeFile(path));
    }
}
