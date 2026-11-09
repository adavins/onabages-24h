using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Reproductor de la cua (fase 1, pas 7b; docs/disseny-motor.md, secció 4), amb fitxers WAV reals i sense targeta de so.
/// Cada pista té un valor constant diferent: a la sortida es veu quina sona i on comença la següent, mostra a mostra.
/// </summary>
public class QueuePlayerTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueEnded> _ended = [];
    private readonly List<QueueFailed> _failed = [];

    public void Dispose() => _temp.Dispose();

    private QueuePlayer NewPlayer(QueueSettings? settings = null)
    {
        var player = new QueuePlayer(_mixer, settings);
        player.Started += _started.Add;
        player.Ended += _ended.Add;
        player.Failed += _failed.Add;
        return player;
    }

    /// <summary>WAV a 48 kHz fet de trossos: (segons, valor constant).</summary>
    private string Wav(string name, params (double Seconds, float Value)[] parts)
    {
        string path = _temp.File(name + ".wav");
        var samples = new List<float>();
        foreach (var (seconds, value) in parts)
            for (int i = 0; i < (int)Math.Round(seconds * Rate); i++) { samples.Add(value); samples.Add(value); }
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
        return path;
    }

    private static QueueItem Item(string path, long? startMs = null, long? mixMs = null, long? endMs = null,
        QueueOrigin origin = QueueOrigin.AutoDj, string title = "") =>
        new() { Path = path, StartMs = startMs, MixMs = mixMs, EndMs = endMs, Origin = origin, Title = title };

    /// <summary>
    /// Fa sonar <paramref name="frames"/> fotogrames: a cada bloc, primer el Tick (fil de control) i després el
    /// mesclador (fil d'àudio). Si hi ha un fitxer obrint-se, l'espera (en temps real), com faria la targeta amb 20 s de marge.
    /// </summary>
    private float[] Run(QueuePlayer player, int frames, int block = 480)
    {
        var all = new float[frames * 2];
        var bytes = new byte[block * 2 * sizeof(float)];
        for (int done = 0; done < frames; done += block)
        {
            player.Tick();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (player.IsPreparing && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
                player.Tick();
            }
            int n = Math.Min(block, frames - done);
            _mixer.Read(bytes.AsSpan(0, n * 2 * sizeof(float)));
            Buffer.BlockCopy(bytes, 0, all, done * 2 * sizeof(float), n * 2 * sizeof(float));
        }
        player.Tick();
        return all;
    }

    private static int FirstAtLeast(float[] stereo, float value, int fromFrame = 0)
    {
        for (int f = fromFrame; f < stereo.Length / 2; f++)
            if (stereo[f * 2] >= value - 1e-4f) return f;
        return -1;
    }

    [Fact]
    public void La_seguent_comenca_exactament_al_punt_de_mescla()
    {
        var player = NewPlayer();
        string a = Wav("a", (3, 0.1f));
        string b = Wav("b", (2, 0.5f));
        player.Enqueue(Item(a, startMs: 0, mixMs: 2500, endMs: 3000, title: "A"), Item(b, title: "B"));

        var output = Run(player, Rate * 6);

        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        long startA = _started[0].StartFrame, startB = _started[1].StartFrame;
        Assert.Equal(Rate * 2500 / 1000, startB - startA);           // 2,5 s exactes
        Assert.Equal(startB, FirstAtLeast(output, 0.5f));             // a la sortida, B entra just allà
        Assert.InRange(output[(startB - 1) * 2], 0.099f, 0.1001f);    // l'última mostra d'A abans del fos, sencera
        Assert.InRange(output[(startB + Rate / 4) * 2], 0.5f, 0.5f + 0.02f); // durant el solapament, A baixa (fos)
    }

    [Fact]
    public void Comenca_al_punt_d_inici_i_salta_el_silenci()
    {
        var player = NewPlayer();
        string a = Wav("a", (0.5, 0f), (1, 0.3f)); // mig segon de silenci al principi
        player.Enqueue(Item(a, startMs: 500, mixMs: 1500, endMs: 1500));

        var output = Run(player, Rate * 2);

        long start = _started[0].StartFrame;
        Assert.Equal(0.3f, output[start * 2], 4);       // la primera mostra que sona ja és so
        Assert.Equal(0f, output[(start - 1) * 2]);
        Assert.Equal((int)start + Rate - 1, Array.FindLastIndex(output, s => s != 0) / 2); // 1 s just
    }

    [Fact]
    public void El_solapament_no_passa_del_maxim()
    {
        var player = NewPlayer(new QueueSettings { MaxOverlap = TimeSpan.FromSeconds(1) });
        string a = Wav("a", (3, 0.1f));
        string b = Wav("b", (1, 0.5f));
        // Final que s'apaga a poc a poc: mescla a 0,5 s i final a 3 s (2,5 s de solapament)
        player.Enqueue(Item(a, 0, 500, 3000), Item(b));

        Run(player, Rate * 5);

        Assert.Equal(Rate * 2, _started[1].StartFrame - _started[0].StartFrame); // final − màxim = 2 s
    }

    [Fact]
    public void Sense_punts_sona_sencera_i_la_seguent_entra_al_final()
    {
        var player = NewPlayer();
        string a = Wav("a", (1, 0.1f));
        string b = Wav("b", (1, 0.5f));
        player.Enqueue(Item(a), Item(b));

        var output = Run(player, Rate * 3);

        Assert.Equal(Rate, _started[1].StartFrame - _started[0].StartFrame);
        Assert.Equal(_started[1].StartFrame, FirstAtLeast(output, 0.5f));
        // Cap forat: just abans de B encara sona A
        Assert.Equal(0.1f, output[(_started[1].StartFrame - 1) * 2], 4);
    }

    [Fact]
    public void Un_fitxer_que_falta_se_salta_i_sona_el_seguent()
    {
        var player = NewPlayer();
        string a = Wav("a", (1, 0.1f));
        string c = Wav("c", (1, 0.5f));
        player.Enqueue(Item(a, title: "A"), Item(_temp.File("no-existeix.mp3"), title: "B"), Item(c, title: "C"));

        var output = Run(player, Rate * 3);

        Assert.Equal("B", Assert.Single(_failed).Item.Title);
        Assert.Contains("No es troba", _failed[0].Message);
        Assert.Equal(["A", "C"], _started.Select(s => s.Item.Title));
        // C entra al final d'A, sense silenci entremig
        Assert.Equal(Rate, _started[1].StartFrame - _started[0].StartFrame);
        Assert.Equal(0.1f, output[(_started[1].StartFrame - 1) * 2], 4);
    }

    [Fact]
    public void En_acabar_avisa_i_allibera_totes_les_veus()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", (1, 0.1f)), title: "A"), Item(Wav("b", (1, 0.2f)), title: "B"));

        Run(player, Rate * 3);

        Assert.Equal(["A", "B"], _ended.Select(e => e.Item.Title));
        Assert.All(_ended, e => Assert.True(e.Complete));
        Assert.All(_ended, e => Assert.InRange(e.Played.TotalMilliseconds, 990, 1010));
        Assert.Null(player.Current);
        Assert.All(_mixer.Voices, v => Assert.Equal(VoiceState.Idle, v.State));
    }

    [Fact]
    public void Saltar_passa_a_la_seguent_de_seguida()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", (10, 0.1f)), title: "A"), Item(Wav("b", (1, 0.5f)), title: "B"));
        Run(player, Rate);

        player.Skip(TimeSpan.FromMilliseconds(100));
        Run(player, Rate * 2);

        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        Assert.InRange(_started[1].StartFrame, Rate, Rate + Rate / 10); // de seguida, no al cap de 10 s
        var a = _ended.First(e => e.Item.Title == "A");
        Assert.False(a.Complete);
    }

    [Fact]
    public void Trenta_pistes_seguides_sense_cap_mostra_de_silenci()
    {
        var player = NewPlayer();
        // Pistes de 3 s; la meitat amb punts (solapament de 0,5 s) i la meitat sense (encadenades al final)
        for (int i = 0; i < 30; i++)
        {
            string path = Wav($"p{i}", (3, 0.1f + 0.01f * (i % 5)));
            player.Enqueue(i % 2 == 0 ? Item(path, 0, 2500, 3000, title: $"P{i}") : Item(path, title: $"P{i}"));
        }

        var output = Run(player, Rate * 95, block: 441); // blocs que no quadren amb les pistes

        Assert.Equal(30, _started.Count);
        Assert.Empty(_failed);
        int first = (int)_started[0].StartFrame;
        int last = Array.FindLastIndex(output, s => s != 0) / 2;
        Assert.True(last - first > Rate * 70, "Han de sonar més de 70 s");
        for (int f = first; f <= last; f++)
            Assert.True(output[f * 2] != 0, $"Silenci a la mostra {f} ({(double)(f - first) / Rate:0.000} s)");
        Assert.All(_mixer.Voices, v => Assert.Equal(0, v.Underruns));
    }

    [Fact]
    public void La_cua_es_pot_canviar_sense_perdre_veus()
    {
        var player = NewPlayer();
        string a = Wav("a", (1, 0.1f));
        var b = Item(Wav("b", (1, 0.2f)), title: "B", origin: QueueOrigin.AutoDj);
        var c = Item(Wav("c", (1, 0.3f)), title: "C", origin: QueueOrigin.Manual);
        var d = Item(Wav("d", (1, 0.4f)), title: "D", origin: QueueOrigin.AutoDj);
        player.Enqueue(Item(a, title: "A"), b, c, d);
        Run(player, Rate / 2); // sona A i B ja s'està preparant

        // «Regenera la cua» (D-034): treu les de l'AutoDJ que no han començat, també la que es preparava
        Assert.Equal(2, player.RemoveWhere(i => i.Origin == QueueOrigin.AutoDj));
        Assert.Equal(["C"], player.Upcoming.Select(i => i.Title));

        // Una inserció al principi passa per davant de la que es preparava
        var e = Item(Wav("e", (1, 0.5f)), title: "E");
        player.Insert(0, e);
        Assert.Equal(["E", "C"], player.Upcoming.Select(i => i.Title));

        Assert.True(player.Remove(e.Id));
        Run(player, Rate * 3);

        Assert.Equal(["A", "C"], _started.Select(s => s.Item.Title));
        Assert.All(_mixer.Voices, v => Assert.Equal(VoiceState.Idle, v.State));
    }
}
