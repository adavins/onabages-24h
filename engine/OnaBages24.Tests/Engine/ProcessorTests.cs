using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Processador d'àudio de la sortida (fase 3, pas 6; D-086): preajusts, volum, sostre de seguretat i ordres.</summary>
public sealed class ProcessorTests : IDisposable
{
    private const int Rate = 48000;
    private static readonly float Ceiling = (float)Math.Pow(10, OutputProcessor.CeilingDb / 20);

    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Mixer _mixer = new(Rate);
    private readonly OutputProcessor _processor;

    public ProcessorTests()
    {
        _db = Database.Open(_temp.File("processador.db"));
        _library = new Library(_db);
        _processor = new OutputProcessor(_mixer.Processor);
        // En activar-se hi ha una transició de 10 ms des del so sense processar: que passi abans de les proves (en silenci)
        Render(0.05);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Una veu que sona tota l'estona a un nivell constant (els dos canals).</summary>
    private void Voice(float value, double seconds = 4)
    {
        var voice = _mixer.AcquireVoice()!;
        var samples = new float[(int)(seconds * Rate) * 2];
        Array.Fill(samples, value);
        voice.Buffer.Write(samples);
        voice.MarkSourceFinished();
        voice.Start(_mixer.FramesRendered); // ara mateix
    }

    /// <summary>Mescla <paramref name="seconds"/> segons i en torna les mostres (estèreo intercalat).</summary>
    private float[] Render(double seconds)
    {
        var bytes = new byte[(int)(seconds * Rate) * 2 * sizeof(float)];
        _mixer.Read(bytes);
        var output = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);
        return output;
    }

    [Fact]
    public void Per_defecte_es_Suau_a_0_dB_amb_el_sostre_de_seguretat()
    {
        Assert.Equal("suau", _processor.Preset.Id);
        Assert.Equal(0, _processor.VolumeDb);
        var s = _mixer.Processor.Settings;
        Assert.True(s.Enabled);
        Assert.True(s.StereoLink);
        Assert.Equal(OutputProcessor.CeilingDb, s.CeilingDb);
    }

    [Fact]
    public void Sense_compressio_el_so_passa_igual()
    {
        _processor.Configure("cap", 0);
        Voice(0.25f);
        var output = Render(0.5);
        Assert.InRange(output[^2], 0.249f, 0.251f);
        Assert.Equal(0, _processor.TakeReductionDb(), 1);
    }

    [Fact]
    public void Dues_pistes_fortes_solapades_no_passen_mai_del_sostre()
    {
        _processor.Configure("cap", 0);
        Voice(0.8f);
        Voice(0.8f); // la suma, 1,6: sense sostre distorsionaria
        var output = Render(0.5);
        Assert.All(output, v => Assert.True(Math.Abs(v) <= Ceiling + 1e-6f, $"{v} passa del sostre"));
        Assert.True(output[^2] > Ceiling - 0.01f); // i no s'abaixa més del que cal
        Assert.True(_processor.TakeReductionDb() > 4); // 1,6 → −0,3 dB: uns 4,4 dB d'abaixada
    }

    [Fact]
    public void El_volum_abaixa_o_puja_la_sortida()
    {
        _processor.Configure("cap", -6);
        Voice(0.25f);
        var output = Render(0.5);
        Assert.InRange(output[^2], 0.124f, 0.127f); // −6 dB ≈ la meitat
    }

    [Fact]
    public void Suau_no_toca_el_so_fluix_i_comprimeix_el_fort()
    {
        // Fluix (−20 dBFS, per sota del llindar de −10): només el guany de compensació (+2 dB)
        Voice(0.1f);
        var quiet = Render(1);
        Assert.InRange(quiet[^2], 0.124f, 0.128f);
        _processor.TakeReductionDb();

        // Fort (−1 dBFS): comprimeix (2:1 per sobre de −10 → uns 4,5 dB menys)
        Voice(0.79f); // ara sonen totes dues: 0,89
        var loud = Render(2);
        float reduction = _processor.TakeReductionDb();
        Assert.InRange(reduction, 3, 7);
        Assert.True(loud[^2] < 0.89f);
    }

    [Fact]
    public void Ordres_processor_get_i_set()
    {
        int changed = 0;
        _processor.Changed += () => changed++;
        var router = new CommandRouter(new EngineState(), () => { }, _library, processor: _processor);
        JsonObject Send(string json) => router.Handle(json);

        var got = Send("""{ "v": 1, "id": 1, "cmd": "processor.get" }""")["result"]!["processor"]!;
        Assert.Equal("suau", got["preset"]!.GetValue<string>());
        Assert.Equal(["cap", "suau", "mitja", "fort"], got["presets"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()));

        var set = Send("""{ "v": 1, "id": 2, "cmd": "processor.set", "preset": "fort", "volumeDb": -3.04 }""")["result"]!["processor"]!;
        Assert.Equal("fort", set["preset"]!.GetValue<string>());
        Assert.Equal("Fort", set["presetName"]!.GetValue<string>());
        Assert.Equal(-3.0, set["volumeDb"]!.GetValue<double>());
        Assert.Equal(1, changed);

        // Només el volum: el preajust es manté
        Send("""{ "v": 1, "id": 3, "cmd": "processor.set", "volumeDb": 2 }""");
        Assert.Equal("fort", _processor.Preset.Id);
        Assert.Equal(2, _processor.VolumeDb);

        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 4, "cmd": "processor.set", "preset": "extrem" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 5, "cmd": "processor.set", "volumeDb": 30 }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 6, "cmd": "processor.set", "volumeDb": "fort" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal(2, changed); // els errors no canvien res
    }

    [Fact]
    public void El_processador_desat_es_recupera_en_arrencar_i_un_de_dolent_no_l_espatlla()
    {
        _library.SetSetting(EngineServer.ProcessorSettingKey, """{ "preset": "mitja", "volumeDb": -2.5 }""");
        EngineServer.ConfigureProcessor(_processor, _library, NullLogger.Instance);
        Assert.Equal("mitja", _processor.Preset.Id);
        Assert.Equal(-2.5, _processor.VolumeDb);

        var other = new OutputProcessor(new Mixer(Rate).Processor);
        _library.SetSetting(EngineServer.ProcessorSettingKey, """{ "preset": "no-existeix", "volumeDb": 0 }""");
        EngineServer.ConfigureProcessor(other, _library, NullLogger.Instance);
        Assert.Equal("suau", other.Preset.Id);
    }

    [Fact]
    public void Els_mesuradors_porten_la_reduccio()
    {
        var ev = QueueJson.Meters((0.5f, 0.5f), null, 3.14f);
        Assert.Equal(3.1, ev["reductionDb"]!.GetValue<double>());
    }
}
