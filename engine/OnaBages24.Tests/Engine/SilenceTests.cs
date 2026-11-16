using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Silenci a la sortida (fase 5, pas 3; D-158), amb fitxers WAV reals i sense targeta de so.</summary>
public sealed class SilenceTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly QueuePlayer _player;
    private readonly SilenceDetector _silence;
    private readonly List<QueueStarted> _started = [];
    private readonly List<SilenceSkip> _skipped = [];

    public SilenceTests()
    {
        _player = new QueuePlayer(_mixer);
        _player.Started += _started.Add;
        _silence = new SilenceDetector(_mixer, _player);
        _silence.Skipped += _skipped.Add;
    }

    public void Dispose()
    {
        _player.Dispose();
        _temp.Dispose();
    }

    /// <summary>Un WAV fet de trossos: (segons, valor de les mostres). Valor 0 = silenci; 0,1 = −20 dB.</summary>
    private string Wav(string name, params (double Seconds, float Value)[] parts)
    {
        string path = _temp.File(name + ".wav");
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        foreach (var (seconds, value) in parts)
        {
            var samples = new float[(int)Math.Round(seconds * Rate) * 2];
            Array.Fill(samples, value);
            writer.WriteSamples(samples, 0, samples.Length);
        }
        return path;
    }

    private static QueueItem Item(string path, string title, TrackType type = TrackType.Music) =>
        new() { Path = path, Title = title, Type = type, Origin = QueueOrigin.AutoDj };

    /// <summary>Fa sonar com el motor: la cua, el detector i el mesclador, en blocs de 10 ms.</summary>
    private void Run(double seconds)
    {
        const int block = 480;
        var bytes = new byte[block * 2 * sizeof(float)];
        int frames = (int)(seconds * Rate);
        int blocks = 0;
        for (int done = 0; done < frames; done += block)
        {
            _player.Tick();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (_player.IsPreparing && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
                _player.Tick();
            }
            _silence.Tick();
            _mixer.Read(bytes);
            if (++blocks % 10 == 0) Thread.Sleep(1); // perquè el fil de lectura tingui temps d'omplir la memòria
        }
    }

    [Fact]
    public void Una_pista_que_sona_en_silenci_15_s_passa_a_la_seguent()
    {
        _player.Enqueue(Item(Wav("a", (3, 0.1f), (30, 0f), (3, 0.1f)), "A"), Item(Wav("b", (5, 0.1f)), "B"));

        Run(17.5);
        Assert.Empty(_skipped);
        Run(1);

        var skip = Assert.Single(_skipped);
        Assert.Equal("A", skip.Item.Title);
        Assert.InRange(skip.Position.TotalSeconds, 17.9, 18.3); // 3 s de so i 15 de silenci
        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        Assert.Null(_silence.Problem);
    }

    [Fact]
    public void Una_pausa_curta_dins_de_la_pista_no_la_salta()
    {
        _player.Enqueue(Item(Wav("a", (2, 0.1f), (8, 0f), (3, 0.1f)), "A"));

        Run(14);

        Assert.Empty(_skipped);
        Assert.Null(_silence.Problem);
    }

    [Fact]
    public void Un_so_fluix_per_sobre_del_llindar_no_es_silenci()
    {
        _silence.Seconds = 5;
        _player.Enqueue(Item(Wav("a", (10, 0.005f)), "A")); // −46 dB

        Run(8);

        Assert.Empty(_skipped);
    }

    [Fact]
    public void Un_stream_en_silenci_nomes_avisa()
    {
        _silence.Seconds = 5;
        _player.Enqueue(Item(Wav("radio", (1, 0.1f), (20, 0f)), "Ràdio", TrackType.Stream));

        Run(5.5);
        Assert.Null(_silence.Problem);
        Run(1);

        Assert.Empty(_skipped);
        Assert.Equal(IncidentSeverity.Warning, _silence.Problem?.Severity);
        Assert.Contains("stream «Ràdio»", _silence.Problem?.Text);
    }

    [Fact]
    public void Si_no_sona_res_avisa_i_l_avis_marxa_quan_torna_el_so()
    {
        _silence.Seconds = 5;
        Run(5.5);
        Assert.Equal(new SilenceProblem("Silenci a la sortida: no sona res", IncidentSeverity.Danger), _silence.Problem);

        _player.Enqueue(Item(Wav("a", (3, 0.1f)), "A"));
        Run(0.5);

        Assert.Null(_silence.Problem);
    }

    [Fact]
    public void Despres_de_tres_pistes_seguides_en_silenci_no_en_salta_mes_i_avisa()
    {
        _silence.Seconds = 3;
        for (int i = 1; i <= 5; i++) _player.Enqueue(Item(Wav($"s{i}", (30, 0f)), $"S{i}"));

        Run(14);

        Assert.Equal(["S1", "S2", "S3"], _skipped.Select(s => s.Item.Title));
        Assert.Equal(IncidentSeverity.Danger, _silence.Problem?.Severity);
        Assert.Contains("3 pistes seguides", _silence.Problem?.Text);
    }

    [Fact]
    public void La_pausa_de_la_cua_no_es_silenci()
    {
        _silence.Seconds = 3;
        _player.Enqueue(Item(Wav("a", (20, 0.1f)), "A"));
        Run(1);
        Assert.Null(_player.PauseCurrent());

        Run(6);

        Assert.Empty(_skipped);
        Assert.Null(_silence.Problem);
    }

    [Fact]
    public void Si_la_sortida_no_demana_so_no_es_mesura_res()
    {
        _silence.Seconds = 3;
        // Sense cap bloc mesclat (sortida encallada): el detector no compta res, per moltes vegades que es cridi
        for (int i = 0; i < 100; i++) _silence.Tick();
        Assert.Null(_silence.Problem);
    }

    [Fact]
    public void El_text_de_la_incidencia()
    {
        Assert.Equal("«A - B» sonava en silenci a 2:05 (més de 15 s sense so) i s'ha passat a la següent",
            IncidentRules.SilenceSkipText("A - B", TimeSpan.FromSeconds(125), 15));
    }
}
