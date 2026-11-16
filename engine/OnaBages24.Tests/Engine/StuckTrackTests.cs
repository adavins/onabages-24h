using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Pista encallada i talls (fase 5, pas 2; D-157): el fitxer de la pista que sona deixa de llegir-se a mitja pista (com
/// un disc o una xarxa que no responen). Amb fitxers WAV reals i sense targeta de so.
/// </summary>
public sealed class StuckTrackTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueEnded> _ended = [];
    private readonly List<QueueStuck> _stuck = [];

    public void Dispose() => _temp.Dispose();

    private QueuePlayer NewPlayer()
    {
        var player = new QueuePlayer(_mixer);
        player.Started += _started.Add;
        player.Ended += _ended.Add;
        player.Stuck += _stuck.Add;
        return player;
    }

    private string Wav(string name, double seconds, float value)
    {
        string path = _temp.File(name + ".wav");
        var samples = new float[(int)Math.Round(seconds * Rate) * 2];
        Array.Fill(samples, value);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static QueueItem Item(string path, string title) => new() { Path = path, Title = title, Origin = QueueOrigin.AutoDj };

    /// <summary>
    /// Fa sonar fotogrames com el motor: el fil de control i després el mesclador. Cada 10 blocs, una pausa curta perquè el
    /// fil de lectura tingui temps d'omplir la memòria (si no, el mesclador aniria molt més de pressa que el disc).
    /// </summary>
    private float[] Run(QueuePlayer player, int frames, Func<bool>? stopWhen = null, int block = 480)
    {
        var all = new float[frames * 2];
        var bytes = new byte[block * 2 * sizeof(float)];
        int blocks = 0;
        for (int done = 0; done < frames; done += block)
        {
            if (stopWhen?.Invoke() == true) break;
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
            if (++blocks % 10 == 0) Thread.Sleep(2);
        }
        player.Tick();
        return all;
    }

    private MixerVoice PlayingVoice() => _mixer.Voices.First(v => v.State == VoiceState.Playing);

    [Fact]
    public void Si_el_fitxer_deixa_de_llegir_se_als_5_s_sense_dades_passa_a_la_seguent()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 60, 0.1f), "A"), Item(Wav("b", 2, 0.5f), "B"));
        Run(player, Rate * 2);

        // El fitxer d'A deixa de llegir-se: sona el que hi ha a la memòria i després, silenci
        player.SetCurrentReadingForTests(false);
        var voice = PlayingVoice();
        Run(player, Rate * 10, stopWhen: () => voice.Buffer.Count == 0);
        long emptySince = _mixer.FramesRendered;
        Run(player, Rate * 7);

        var stuck = Assert.Single(_stuck);
        Assert.Equal(("A", (string?)null), (stuck.Item.Title, stuck.Error));
        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        // B entra uns 5 s després que A es quedi sense dades (no al final d'A, al cap d'un minut)
        Assert.InRange(_started[1].StartFrame - emptySince, Rate * 5 - Rate / 10, Rate * 5 + Rate / 2);
        var a = _ended.First(e => e.Item.Title == "A");
        Assert.Equal((false, 0), (a.Complete, a.Dropouts)); // els talls de l'encallada no compten com a talls
    }

    [Fact]
    public void Un_tall_curt_no_salta_la_pista_pero_es_compta()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 20, 0.1f), "A"));
        Run(player, Rate * 2);

        // Uns 2 s sense dades (menys dels 5 que fan saltar la pista) i el fitxer torna
        player.SetCurrentReadingForTests(false);
        var voice = PlayingVoice();
        Run(player, Rate * 10, stopWhen: () => voice.Buffer.Count == 0);
        Run(player, Rate * 2);
        player.SetCurrentReadingForTests(true);
        Run(player, Rate * 20);

        Assert.Empty(_stuck);
        var a = Assert.Single(_ended);
        Assert.True(a.Dropouts > 0);
        Assert.False(a.Complete); // s'ha acabat a l'hora prevista, però amb 2 s de silenci al mig
    }

    [Fact]
    public void Una_veu_sense_dades_durant_el_fos_final_s_acaba_igualment_i_queda_lliure()
    {
        var player = NewPlayer();
        // A: 8 s, amb la mescla als 6 s. Es queda sense dades als 5 s (la memòria és de 5 s): la detecció dels 5 s no
        // arriba a temps abans de la mescla, i el fos final sonaria sense dades
        player.Enqueue(Item(Wav("a", 8, 0.1f), "A") with { MixMs = 6000 }, Item(Wav("b", 6, 0.5f), "B"));
        Run(player, Rate / 10);
        player.SetCurrentReadingForTests(false);

        Run(player, Rate * 11);

        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        Assert.Contains(_ended, e => e.Item.Title == "A"); // abans no s'acabava mai
        Assert.Equal(1, _mixer.Voices.Count(v => v.State != VoiceState.Idle)); // només la de B
    }

    [Fact]
    public void Sense_talls_no_hi_ha_res_a_avisar()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 8, 0.1f), "A"), Item(Wav("b", 1, 0.5f), "B"));

        Run(player, Rate * 10);

        Assert.Empty(_stuck);
        Assert.All(_ended, e => Assert.Equal((true, 0), (e.Complete, e.Dropouts)));
    }

    [Fact]
    public void Els_textos_de_les_incidencies()
    {
        Assert.Equal("«A - B» s'ha encallat a 2:05: el fitxer ha deixat de llegir-se (disc o xarxa) i s'ha passat a la següent",
            IncidentRules.StuckText("A - B", TimeSpan.FromSeconds(125), null));
        Assert.Contains("1:02:03", IncidentRules.StuckText("A", new TimeSpan(1, 2, 3), "error de xarxa"));
        Assert.Contains("ha fallat la lectura del fitxer (error de xarxa)", IncidentRules.StuckText("A", TimeSpan.Zero, "error de xarxa"));
        Assert.Contains("(1 vegada sense dades)", IncidentRules.DropoutsText("A", 1));
        Assert.Contains("(12 vegades sense dades)", IncidentRules.DropoutsText("A", 12));
    }
}
