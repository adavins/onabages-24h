using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Engine.Playback;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Pausa i salt dins de la pista que sona (D-103), amb fitxers WAV reals i sense targeta de so. La pista A té un valor
/// diferent a cada segon (0,1 el primer, 0,2 el segon…): a la sortida es veu per on va.
/// </summary>
public sealed class PauseSeekTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueEnded> _ended = [];

    public void Dispose() => _temp.Dispose();

    private QueuePlayer NewPlayer(QueueSettings? settings = null)
    {
        var player = new QueuePlayer(_mixer, settings);
        player.Started += _started.Add;
        player.Ended += _ended.Add;
        return player;
    }

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

    /// <summary>A: 5 s (un valor per segon) i B: 1 s a 0,9. A, sense punts: B entra al final d'A.</summary>
    private (QueueItem A, QueueItem B) Queue(QueuePlayer player)
    {
        var a = new QueueItem { Path = Wav("a", (1, 0.1f), (1, 0.2f), (1, 0.3f), (1, 0.4f), (1, 0.5f)), Title = "A" };
        var b = new QueueItem { Path = Wav("b", (1, 0.9f)), Title = "B" };
        player.Enqueue(a, b);
        return (a, b);
    }

    /// <summary>Fa sonar uns fotogrames (Tick i mesclador a cada bloc; espera si s'està obrint un fitxer).</summary>
    private float[] Run(QueuePlayer player, double seconds, int block = 480)
    {
        int frames = (int)(seconds * Rate);
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

    /// <summary>El valor que sona al final d'un tros de sortida.</summary>
    private static float Last(float[] output) => output[^2];

    [Fact]
    public void La_pausa_fa_silenci_i_continuar_segueix_on_era_amb_una_sola_emissio()
    {
        var player = NewPlayer();
        Queue(player);
        Run(player, 1.5);

        Assert.Null(player.PauseCurrent());
        var paused = Run(player, 1);
        Assert.All(paused[(Rate / 2 * 2)..], s => Assert.Equal(0f, s)); // passat el fos, silenci
        Assert.True(player.IsPaused);
        var position = player.Position!;
        Assert.True(position.Paused);
        Assert.InRange(position.Elapsed.TotalSeconds, 1.5, 1.6);
        Assert.InRange(player.Forecast().CurrentEndsIn!.Value.TotalSeconds, 3.4, 3.5); // la previsió no corre durant la pausa
        Assert.Equal("A", player.Current!.Title);
        Assert.Empty(_ended);

        Assert.Null(player.ResumePaused());
        var resumed = Run(player, 1);
        Assert.False(player.IsPaused);
        Assert.InRange(Last(resumed), 0.29f, 0.31f); // 1,55 s + 1 s → el tercer segon d'A
        Run(player, 3);

        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title)); // A no ha tornat a «començar»
        var a = _ended.Single(e => e.Item.Title == "A");
        Assert.True(a.Complete);
        Assert.InRange(a.Played.TotalSeconds, 4.9, 5.4);
    }

    [Fact]
    public void Anar_a_un_altre_punt_canvia_on_sona_i_on_entra_la_seguent()
    {
        var player = NewPlayer();
        var (a, _) = Queue(player);
        Run(player, 0.5);

        Assert.Null(player.Seek(a.Id, TimeSpan.FromSeconds(3)));
        var after = Run(player, 0.5);
        Assert.InRange(Last(after), 0.39f, 0.41f); // el quart segon d'A
        Assert.InRange(player.Position!.Elapsed.TotalSeconds, 3.4, 3.6);
        Assert.InRange(player.Forecast().CurrentEndsIn!.Value.TotalSeconds, 1.4, 1.6);

        Run(player, 2);
        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        var a1 = _ended.Single(e => e.Item.Title == "A");
        Assert.False(a1.Complete); // se n'ha saltat un tros: no ha sonat sencera
        Assert.InRange(a1.Played.TotalSeconds, 2.4, 2.7);
    }

    [Fact]
    public void Anar_enrere_tambe_funciona()
    {
        var player = NewPlayer();
        var (a, _) = Queue(player);
        Run(player, 1.8);

        Assert.Null(player.Seek(a.Id, TimeSpan.FromSeconds(0.5)));
        Assert.InRange(Last(Run(player, 0.3)), 0.09f, 0.11f);
    }

    [Fact]
    public void Si_la_seguent_esta_a_punt_de_començar_no_es_mou()
    {
        var player = NewPlayer();
        var (a, _) = Queue(player);
        Run(player, 3.5); // B comença d'aquí a 1,5 s

        Assert.Equal("La pista següent està a punt de començar.", player.Seek(a.Id, TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void Massa_estona_en_pausa_continua_sola_amb_un_avis()
    {
        var player = NewPlayer(new QueueSettings { MaxPause = TimeSpan.FromSeconds(1) });
        Queue(player);
        Run(player, 0.5);
        player.PauseCurrent();

        var output = Run(player, 1.5);

        Assert.False(player.IsPaused);
        Assert.InRange(Last(output), 0.09f, 0.11f); // continua al primer segon d'A
        Assert.Contains("ha continuat sola", player.PauseNotice);
    }

    [Fact]
    public void Passar_a_la_seguent_durant_la_pausa_acaba_la_pista_en_pausa()
    {
        var player = NewPlayer();
        Queue(player);
        Run(player, 1);
        player.PauseCurrent();
        Run(player, 0.5);

        player.Skip(TimeSpan.FromMilliseconds(100));
        var output = Run(player, 0.5);

        Assert.False(player.IsPaused);
        Assert.InRange(Last(output), 0.89f, 0.91f); // B
        var a = _ended.Single(e => e.Item.Title == "A");
        Assert.False(a.Complete);
        Assert.InRange(a.Played.TotalSeconds, 0.9, 1.3);
    }

    [Fact]
    public void Sense_res_que_soni_no_es_pot_fer_pausa()
    {
        var player = NewPlayer();
        Assert.Equal("No sona cap pista.", player.PauseCurrent());
        Assert.NotNull(player.Seek(Guid.NewGuid(), TimeSpan.Zero));
    }
}
