using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Reproductors auxiliars i atenuació (fase 2, pas 5): l'auxiliar sona per sobre de la música a la mostra prevista, la
/// música baixa i torna a pujar, i la cua no se n'assabenta. Cada fitxer té un valor constant: a la sortida es veu
/// què sona i amb quin guany, mostra a mostra.
/// </summary>
public sealed class AuxTests : IDisposable
{
    private const int Rate = 48000;
    private const float Music = 0.1f;
    private const float Signal = 0.3f;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly QueuePlayer _queue;
    private readonly AuxPlayers _aux;
    private readonly List<AuxStarted> _started = [];
    private readonly List<AuxEnded> _ended = [];
    private readonly List<AuxFailed> _failed = [];

    public AuxTests()
    {
        _queue = new QueuePlayer(_mixer);
        _aux = new AuxPlayers(_mixer);
        _aux.Started += _started.Add;
        _aux.Ended += _ended.Add;
        _aux.Failed += _failed.Add;
    }

    public void Dispose()
    {
        _aux.Dispose();
        _queue.Dispose();
        _temp.Dispose();
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

    private static QueueItem Item(string path, string title = "") => new() { Path = path, Title = title };

    /// <summary>Fa sonar <paramref name="frames"/> fotogrames a blocs de 10 ms, amb els Tick abans de cada bloc (com el motor).</summary>
    private float[] Run(int frames)
    {
        const int block = 480;
        var all = new float[frames * 2];
        var bytes = new byte[block * 2 * sizeof(float)];
        for (int done = 0; done < frames; done += block)
        {
            _aux.Tick();
            _queue.Tick();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (_queue.IsPreparing && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
                _queue.Tick();
            }
            int n = Math.Min(block, frames - done);
            _mixer.Read(bytes.AsSpan(0, n * 2 * sizeof(float)));
            Buffer.BlockCopy(bytes, 0, all, done * 2 * sizeof(float), n * 2 * sizeof(float));
        }
        _aux.Tick();
        _queue.Tick();
        return all;
    }

    /// <summary>Música de fons a la cua: sona des del primer bloc.</summary>
    private void StartMusic(double seconds = 30)
    {
        _queue.Enqueue(Item(Wav("musica", seconds, Music), "Música"));
        Run(Rate / 10); // ja sona
    }

    /// <summary>Espera que el fitxer de l'auxiliar estigui obert (com passa amb els 30 s de marge).</summary>
    private static void WaitReady() => Thread.Sleep(300);

    private static float Left(float[] stereo, int frame) => stereo[frame * 2];

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    [Fact]
    public void L_auxiliar_comenca_a_la_mostra_prevista_per_sobre_de_la_musica()
    {
        StartMusic();
        var id = _aux.Prepare(1, Item(Wav("senyals", 1, Signal), "Senyals"), duckingDb: null)!.Value;
        WaitReady();
        long target = _mixer.FramesRendered + Rate / 2; // d'aquí a mig segon
        _aux.StartAt(id, target);

        long first = _mixer.FramesRendered;
        var output = Run(Rate * 2);
        int at = (int)(target - first);

        Assert.Equal(Music, Left(output, at - 1), 4);           // abans: només la música
        Assert.Equal(Music + Signal, Left(output, at), 4);      // a la mostra exacta: tots dos
        Assert.Equal(Music + Signal, Left(output, at + Rate - 1), 4);
        Assert.Equal(Music, Left(output, at + Rate), 4);        // acabat el fitxer (1 s): la música, que no s'ha aturat

        var started = Assert.Single(_started);
        Assert.Equal(target, started.StartFrame);
        Assert.Equal(target, started.TargetFrame);
        var ended = Assert.Single(_ended);
        Assert.True(ended.Complete);
        Assert.Equal("Música", _queue.Current?.Title); // la cua no se n'ha assabentat
    }

    [Fact]
    public void Amb_atenuacio_la_musica_baixa_abans_i_torna_a_pujar_despres()
    {
        StartMusic();
        var id = _aux.Prepare(1, Item(Wav("senyals", 1, Signal)), duckingDb: -12)!.Value;
        WaitReady();
        long target = _mixer.FramesRendered + Rate; // d'aquí a 1 s
        _aux.StartAt(id, target);

        long first = _mixer.FramesRendered;
        var output = Run(Rate * 3);
        int at = (int)(target - first);
        int down = (int)(AuxPlayers.DuckDown.TotalSeconds * Rate);
        int up = (int)(AuxPlayers.DuckUp.TotalSeconds * Rate);

        Assert.Equal(Music, Left(output, at - down - 1), 4);                // abans de la baixada, sencera
        Assert.Equal(Music * Db(-6), Left(output, at - down / 2), 3);       // a mitja baixada, −6 dB
        Assert.Equal(Music * Db(-12) + Signal, Left(output, at), 4);        // a l'hora: atenuada, i el senyal sencer
        Assert.Equal(Music * Db(-12) + Signal, Left(output, at + Rate - 1), 4);
        Assert.Equal(Music * Db(-6), Left(output, at + Rate + up / 2), 3);  // a mitja pujada
        Assert.Equal(Music, Left(output, at + Rate + up), 4);               // ja és com era
    }

    [Fact]
    public void Sense_hora_fixa_la_musica_baixa_del_tot_abans_que_comenci()
    {
        StartMusic();
        Assert.True(_aux.Play(1, Item(Wav("veu", 1, Signal)), duckingDb: -12));
        WaitReady();

        long first = _mixer.FramesRendered;
        var output = Run(Rate);
        int at = (int)(Assert.Single(_started).StartFrame - first);

        Assert.Equal(Music * Db(-12) + Signal, Left(output, at), 4); // el primer so ja troba la música atenuada
        Assert.True(Left(output, at - 1) < Music);                     // i abans ha anat baixant
    }

    [Fact]
    public void Dos_reproductors_sonen_alhora()
    {
        StartMusic();
        var a = _aux.Prepare(1, Item(Wav("u", 1, 0.2f)), null)!.Value;
        var b = _aux.Prepare(2, Item(Wav("dos", 1, 0.3f)), null)!.Value;
        WaitReady();
        long target = _mixer.FramesRendered + Rate / 2;
        _aux.StartAt(a, target);
        _aux.StartAt(b, target);

        long first = _mixer.FramesRendered;
        var output = Run(Rate);
        Assert.Equal(Music + 0.2f + 0.3f, Left(output, (int)(target - first)), 4);
        Assert.Equal(2, _started.Count);
        Assert.Equal(2, _aux.Status.Count);
    }

    [Fact]
    public void Al_mateix_reproductor_el_nou_substitueix_el_que_sonava()
    {
        StartMusic();
        Assert.True(_aux.Play(1, Item(Wav("llarg", 10, 0.2f), "Llarg"), null));
        WaitReady();
        Run(Rate / 2);

        var id = _aux.Prepare(1, Item(Wav("nou", 1, 0.3f), "Nou"), null)!.Value;
        WaitReady();
        long target = _mixer.FramesRendered + Rate / 2;
        _aux.StartAt(id, target);

        long first = _mixer.FramesRendered;
        var output = Run(Rate);
        int at = (int)(target - first);
        int fade = (int)(AuxPlayers.ReplaceFade.TotalSeconds * Rate);

        Assert.Equal(Music + 0.2f, Left(output, at - 1), 4);       // abans, el llarg
        Assert.Equal(Music + 0.3f, Left(output, at + fade), 4);    // passat el fos curt, només el nou
        var ended = Assert.Single(_ended);
        Assert.Equal("Llarg", ended.Item.Title);
        Assert.False(ended.Complete);
        Assert.Equal("Nou", Assert.Single(_aux.Status).Item.Title);
    }

    [Fact]
    public void Aturar_un_reproductor_fa_un_fos_i_la_musica_torna_a_pujar()
    {
        StartMusic();
        Assert.True(_aux.Play(1, Item(Wav("llarg", 10, Signal)), duckingDb: -12));
        WaitReady();
        Run(Rate);

        Assert.True(_aux.Stop(1, TimeSpan.FromMilliseconds(100)));
        var output = Run(Rate);

        Assert.Equal(Music, Left(output, Rate - 1), 4); // al cap d'1 s: només la música, sencera
        Assert.False(Assert.Single(_ended).Complete);
        Assert.Empty(_aux.Status);
        Assert.False(_aux.Stop(1, TimeSpan.Zero));      // ja no sona res
    }

    [Fact]
    public void Si_el_fitxer_falta_la_musica_continua_i_queda_un_avis()
    {
        StartMusic();
        Assert.True(_aux.Play(1, Item(_temp.File("no-hi-es.mp3"), "Falta"), duckingDb: -12));
        WaitReady();
        var output = Run(Rate);

        Assert.Equal("Falta", Assert.Single(_failed).Item.Title);
        Assert.Empty(_started);
        Assert.Equal(Music, Left(output, Rate - 1), 4); // ni silenci ni atenuació
        Assert.Empty(_aux.Status);
    }

    [Fact]
    public void Una_preparacio_que_no_sona_allibera_la_veu()
    {
        int free = _mixer.Voices.Count(v => v.State == VoiceState.Idle);
        var id = _aux.Prepare(1, Item(Wav("senyals", 1, Signal)), null)!.Value;
        WaitReady();
        Assert.Equal(free - 1, _mixer.Voices.Count(v => v.State == VoiceState.Idle));

        _aux.Release(id);
        Assert.Equal(free, _mixer.Voices.Count(v => v.State == VoiceState.Idle));
    }

    // --- Ordres aux.* ---

    private JsonObject Send(string json) =>
        new CommandRouter(new EngineState(), () => { }, listDevices: () => [], queue: _queue, aux: _aux).Handle(json);

    [Fact]
    public void Les_ordres_aux_fan_sonar_aturen_i_diuen_que_sona()
    {
        string path = Wav("senyals", 5, Signal).Replace(@"\", @"\\");
        var play = Send($$"""{ "v": 1, "id": 1, "cmd": "aux.play", "path": "{{path}}", "player": 2, "duckingDb": -9 }""");
        Assert.True(play["ok"]!.GetValue<bool>());
        Assert.Equal(2, play["result"]!["player"]!.GetValue<int>());
        WaitReady();
        Run(Rate / 2);

        var get = Send("""{ "v": 1, "id": 2, "cmd": "aux.get" }""")["result"]!["aux"]!.AsArray();
        var status = Assert.Single(get)!;
        Assert.Equal(2, status["player"]!.GetValue<int>());
        Assert.True(status["started"]!.GetValue<bool>());
        Assert.Equal(-9, status["duckingDb"]!.GetValue<double>());
        Assert.Equal("senyals", status["item"]!["title"]!.GetValue<string>());

        var stop = Send("""{ "v": 1, "id": 3, "cmd": "aux.stop", "player": 2, "fadeMs": 0 }""")["result"]!;
        Assert.True(stop["stopped"]!.GetValue<bool>());
    }

    [Fact]
    public void Una_pista_carregada_sona_amb_play_i_s_hi_queda_per_tornar_a_sonar()
    {
        var changed = new List<int>();
        _aux.LoadedChanged += changed.Add;
        string path = Wav("jingle", 1, Signal).Replace(@"\", @"\\");

        var load = Send($$"""{ "v": 1, "id": 1, "cmd": "aux.load", "path": "{{path}}", "player": 3 }""");
        Assert.True(load["ok"]!.GetValue<bool>());
        var loaded = load["result"]!["loaded"]!.AsArray();
        Assert.Equal(3, loaded.Count); // un per reproductor
        Assert.Null(loaded[0]!["item"]);
        Assert.Equal("jingle", loaded[2]!["item"]!["title"]!.GetValue<string>());
        Assert.Equal([3], changed);
        Run(Rate / 10);
        Assert.Empty(_started); // carregar no la fa sonar

        // ▶ sense pista: sona la carregada
        var play = Send("""{ "v": 1, "id": 2, "cmd": "aux.play", "player": 3 }""");
        Assert.True(play["ok"]!.GetValue<bool>());
        WaitReady();
        var output = Run(Rate / 2);
        Assert.Equal(Signal, Left(output, Rate / 4), 3);
        var first = Assert.Single(_started);
        Assert.Equal(3, first.Player);
        Assert.Equal(first.Item.Id.ToString(), play["result"]!["item"]!["id"]!.GetValue<string>()); // la resposta diu la que sona

        // S'acaba i continua carregada: es pot tornar a fer sonar (amb un altre id)
        Run(Rate);
        Assert.Single(_ended);
        Assert.Equal("jingle", Send("""{ "v": 1, "id": 3, "cmd": "aux.get" }""")["result"]!["loaded"]![2]!["item"]!["title"]!.GetValue<string>());
        Assert.True(Send("""{ "v": 1, "id": 4, "cmd": "aux.play", "player": 3 }""")["ok"]!.GetValue<bool>());
        WaitReady();
        Run(Rate / 2);
        Assert.Equal(2, _started.Count);
        Assert.NotEqual(_started[0].Item.Id, _started[1].Item.Id);

        // Treure-la no atura el que sona; després, ▶ respon que no hi ha res carregat
        Assert.True(Send("""{ "v": 1, "id": 5, "cmd": "aux.unload", "player": 3 }""")["ok"]!.GetValue<bool>());
        Assert.Single(_aux.Status);
        var empty = Send("""{ "v": 1, "id": 6, "cmd": "aux.play", "player": 3 }""");
        Assert.False(empty["ok"]!.GetValue<bool>());
        Assert.Contains("cap pista carregada", empty["error"]!["message"]!.GetValue<string>());
        Assert.Equal([3, 3], changed);
    }

    [Fact]
    public void No_es_pot_carregar_un_fitxer_que_no_hi_es()
    {
        var response = Send("""{ "v": 1, "id": 1, "cmd": "aux.load", "path": "D:\\no-hi-es\\x.mp3", "player": 2 }""");
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Contains("no hi és", response["error"]!["message"]!.GetValue<string>());
        Assert.Null(_aux.Loaded(2));
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.play" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.load", "player": 2 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.unload", "player": 9 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.play", "path": "D:\\x.mp3", "player": 4 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.play", "path": "D:\\x.mp3", "duckingDb": 3 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "aux.stop", "player": 0 }""")]
    public void Les_ordres_aux_amb_parametres_dolents_responen_bad_params(string json)
    {
        var response = Send(json);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("bad_params", response["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void L_envolupant_de_l_atenuacio_baixa_i_puja_en_dB()
    {
        var duck = new DuckEnvelope(Start: 1000, End: 2000, Db: -20, DownFrames: 100, UpFrames: 200);
        Assert.Equal(1f, duck.GainAt(899));
        Assert.Equal(Db(-10), duck.GainAt(950), 4);
        Assert.Equal(Db(-20), duck.GainAt(1000), 4);
        Assert.Equal(Db(-20), duck.GainAt(1999), 4);
        Assert.Equal(Db(-10), duck.GainAt(2100), 4);
        Assert.Equal(1f, duck.GainAt(2200));
        Assert.Equal(Db(-20), duck.WithEnd(3000).GainAt(2500), 4);
    }
}
