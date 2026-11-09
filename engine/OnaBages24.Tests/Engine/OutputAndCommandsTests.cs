using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Sortida d'emissió amb la d'emergència (D-038; fase 1, pas 7c), sense targeta de so.</summary>
public class OutputManagerTests
{
    private static AudioDevice Out(string id, string name, DeviceConnector connector, DeviceStatus status = DeviceStatus.Active) =>
        new(id, name, DeviceKind.Output, status, new DeviceFormat(48000, 24, 2), connector);

    private readonly List<AudioDevice> _devices =
    [
        Out("hdmi", "Pantalla HDMI", DeviceConnector.Hdmi),
        Out("spdif", "Sortida digital", DeviceConnector.Digital),
        Out("blaster", "Altaveus (Sound BlasterX AE-5 Plus)", DeviceConnector.Analog),
        Out("realtek", "Altaveus (Realtek)", DeviceConnector.Analog),
        new("mic", "Micròfon", DeviceKind.Input, DeviceStatus.Active, null, DeviceConnector.Analog),
    ];

    private readonly Dictionary<string, FakeOutput> _opened = [];

    private OutputManager Manager(Mixer mixer) => new(mixer, () => _devices.ToList(), id =>
    {
        if (!_devices.Any(d => d.Id == id && d.Status == DeviceStatus.Active)) throw new AudioDeviceException("Dispositiu no disponible.");
        var output = new FakeOutput(id == "spdif" ? 44100 : 48000);
        _opened[id] = output;
        return output;
    });

    [Fact]
    public void L_ordre_es_la_configurada_i_despres_analogiques_digitals_i_HDMI()
    {
        var byId = OutputManager.Candidates(_devices, new ConfiguredOutput("spdif", "Sortida digital"));
        Assert.Equal(["spdif", "realtek", "blaster", "hdmi"], byId.Select(c => c.Device.Id));
        Assert.Equal([false, true, true, true], byId.Select(c => c.Emergency));

        // L'identificador ha canviat (controlador reinstal·lat): es troba pel nom
        var byName = OutputManager.Candidates(_devices, new ConfiguredOutput("{vell}", "Altaveus (Realtek)"));
        Assert.Equal("realtek", byName[0].Device.Id);
        Assert.False(byName[0].Emergency);

        // Sense configuració: la primera analògica, per ordre alfabètic
        var none = OutputManager.Candidates(_devices, new ConfiguredOutput(null, null));
        Assert.Equal("realtek", none[0].Device.Id);
        Assert.True(none[0].Emergency);
    }

    [Fact]
    public void Si_la_configurada_no_hi_es_emet_per_la_d_emergencia_i_hi_torna_quan_apareix()
    {
        var mixer = new Mixer(48000);
        var manager = Manager(mixer);
        _devices.RemoveAll(d => d.Id == "realtek");
        manager.Configure(new ConfiguredOutput("realtek", "Altaveus (Realtek)"));

        manager.Ensure();
        Assert.Equal("blaster", manager.Current?.Id);
        Assert.True(manager.IsEmergency);
        Assert.Contains("emergència", manager.Problem);
        Assert.True(_opened["blaster"].IsPlaying);

        // Les comprovacions següents mantenen l'avís mentre no torni la configurada (D-039)
        manager.Ensure();
        manager.Ensure();
        Assert.True(manager.IsEmergency);
        Assert.Contains("emergència", manager.Problem);
        Assert.False(_opened["blaster"].Disposed); // i no es torna a obrir

        // Torna la targeta configurada: hi passa de seguida (D-039)
        _devices.Add(Out("realtek", "Altaveus (Realtek)", DeviceConnector.Analog));
        manager.Ensure();
        Assert.Equal("realtek", manager.Current?.Id);
        Assert.False(manager.IsEmergency);
        Assert.Null(manager.Problem);
        Assert.True(_opened["blaster"].Disposed); // l'anterior, tancada: el mesclador només té una sortida
    }

    [Fact]
    public void Si_la_sortida_falla_mentre_sona_passa_a_la_d_emergencia()
    {
        var mixer = new Mixer(48000);
        var manager = Manager(mixer);
        manager.Configure(new ConfiguredOutput("realtek", "Altaveus (Realtek)"));
        manager.Ensure();
        Assert.Equal("realtek", manager.Current?.Id);

        // Es desendolla
        _devices.RemoveAll(d => d.Id == "realtek");
        _opened["realtek"].Fail("dispositiu desconnectat");
        manager.Ensure();

        Assert.Equal("blaster", manager.Current?.Id);
        Assert.True(manager.IsEmergency);
    }

    [Fact]
    public void Sense_cap_sortida_ho_diu_clarament()
    {
        var mixer = new Mixer(48000);
        var manager = new OutputManager(mixer, () => [], _ => throw new AudioDeviceException("no"));

        manager.Ensure();

        Assert.Null(manager.Current);
        Assert.Contains("cap sortida", manager.Problem);
    }

    [Fact]
    public void Una_sortida_a_una_altra_frequencia_rep_el_so_remostrejat()
    {
        var mixer = new Mixer(48000);
        var voice = mixer.AcquireVoice()!;
        var samples = new float[48000 * 2];
        Array.Fill(samples, 0.5f);
        voice.Buffer.Write(samples);
        voice.MarkSourceFinished();
        voice.Start(0);

        var adapter = new OutputAdapter(mixer, 44100, 2);
        var bytes = new byte[4410 * 2 * sizeof(float)]; // 100 ms a 44,1 kHz
        for (int i = 0; i < 3; i++) adapter.Read(bytes);
        var output = new float[4410 * 2];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);

        Assert.True(adapter.Resampling);
        Assert.InRange(output[4000 * 2], 0.49f, 0.51f);
        Assert.InRange(mixer.FramesRendered, 13_000, 16_000); // ~300 ms de mesclador (a 48 kHz)
    }

    [Fact]
    public void Una_sortida_de_4_canals_rep_l_estereo_als_dos_primers()
    {
        var mixer = new Mixer(48000);
        var voice = mixer.AcquireVoice()!;
        voice.Buffer.Write(Enumerable.Repeat(0.25f, 960).ToArray());
        voice.MarkSourceFinished();
        voice.Start(0);

        var adapter = new OutputAdapter(mixer, 48000, 4);
        var bytes = new byte[480 * 4 * sizeof(float)];
        adapter.Read(bytes);
        var output = new float[480 * 4];
        Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);

        Assert.False(adapter.Resampling);
        Assert.Equal([0.25f, 0.25f, 0f, 0f], output[..4]);
    }
}

/// <summary>Recàlcul dels punts en segon pla (D-042).</summary>
public class CueRecalculatorTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;

    public CueRecalculatorTests()
    {
        _db = Database.Open(_temp.File("recalcul.db"));
        _library = new Library(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private string Wav()
    {
        string path = _temp.File($"{Guid.NewGuid():N}.wav");
        var samples = new List<float>();
        for (int i = 0; i < 48000 / 2; i++) { samples.Add(0); samples.Add(0); }   // 0,5 s de silenci
        for (int i = 0; i < 48000 * 2; i++) { samples.Add(0.5f); samples.Add(0.5f); } // 2 s de so
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
        writer.WriteSamples(samples.ToArray(), 0, samples.Count);
        return path;
    }

    [Fact]
    public void Substitueix_els_punts_de_RadioDJ_i_no_toca_els_manuals()
    {
        long radioDj = _library.AddTrack(new Track { Path = Wav(), CueSource = CueSource.RadioDj, StartMs = 530, MixMs = 2400, EndMs = 2540 });
        long manual = _library.AddTrack(new Track { Path = Wav(), CueSource = CueSource.Manual, StartMs = 100, MixMs = 200, EndMs = 300 });
        long missing = _library.AddTrack(new Track { Path = _temp.File("no-hi-es.mp3"), CueSource = CueSource.RadioDj, StartMs = 1, MixMs = 2, EndMs = 3 });

        var pending = _library.TracksNeedingCueAnalysis(0, 10);
        Assert.Equal([radioDj, missing], pending.Select(t => t.Id)); // la manual no hi és

        var recalculator = new CueRecalculator(_library);
        foreach (var track in pending) recalculator.Recalculate(track);

        var updated = _library.GetTrack(radioDj)!;
        Assert.Equal(CueSource.Analyzer, updated.CueSource);
        Assert.InRange(updated.StartMs!.Value, 499, 501);
        Assert.InRange(updated.EndMs!.Value, 2499, 2501);
        Assert.Equal((100L, 200L, 300L), (_library.GetTrack(manual)!.StartMs!.Value, _library.GetTrack(manual)!.MixMs!.Value, _library.GetTrack(manual)!.EndMs!.Value));
        Assert.Equal(CueSource.RadioDj, _library.GetTrack(missing)!.CueSource); // no es pot llegir: es queda com estava
        Assert.Equal((1, 1), (recalculator.Done, recalculator.Failed));
        Assert.Equal([missing], _library.TracksNeedingCueAnalysis(0, 10).Select(t => t.Id));
    }
}

/// <summary>Ordres de la cua i de la sortida (docs/protocol.md, 4.2 a 4.4).</summary>
public class QueueCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Mixer _mixer = new(48000);
    private readonly QueuePlayer _queue;
    private readonly OutputManager _outputs;
    private readonly CommandRouter _router;

    private static readonly AudioDevice[] Devices =
    [
        new("{a}", "Altaveus A", DeviceKind.Output, DeviceStatus.Active, null, DeviceConnector.Analog),
    ];

    public QueueCommandsTests()
    {
        _db = Database.Open(_temp.File("ordres.db"));
        _library = new Library(_db);
        _queue = new QueuePlayer(_mixer);
        _outputs = new OutputManager(_mixer, () => Devices, _ => new FakeOutput());
        _router = new CommandRouter(new EngineState(), () => { }, _library, () => Devices, _queue, _outputs);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    [Fact]
    public void Afegeix_pistes_per_id_i_per_ruta_al_final_o_al_principi()
    {
        long id = _library.AddTrack(new Track { Path = @"D:\a.mp3", Artist = "Art", Title = "Biblioteca" });

        Send($$"""{ "v": 1, "id": 1, "cmd": "queue.insert", "items": [ { "trackId": {{id}} } ] }""");
        var result = Send("""{ "v": 1, "id": 2, "cmd": "queue.insert", "items": [ { "path": "D:\\fora.mp3" } ], "position": "next" }""")["result"]!;

        var upcoming = result["queue"]!["upcoming"]!.AsArray();
        Assert.Equal(["fora", "Biblioteca"], upcoming.Select(i => i!["title"]!.GetValue<string>()));
        Assert.Equal("manual", upcoming[1]!["origin"]!.GetValue<string>());
        Assert.Equal(id, upcoming[1]!["trackId"]!.GetValue<long>());
        Assert.Equal("music", upcoming[1]!["type"]!.GetValue<string>());

        // Treure'n una
        string itemId = upcoming[0]!["id"]!.GetValue<string>();
        var after = Send($$"""{ "v": 1, "id": 3, "cmd": "queue.remove", "itemId": "{{itemId}}" }""")["result"]!;
        Assert.Single(after["queue"]!["upcoming"]!.AsArray());
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "queue.insert" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "queue.insert", "items": [ { "trackId": 999 } ] }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "queue.insert", "items": [ { "res": 1 } ] }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "queue.insert", "items": [ { "path": "D:\\a.mp3" } ], "position": "enmig" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "queue.remove", "itemId": "no-és-un-id" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "player.next", "fadeMs": 99999 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "output.set", "deviceId": "{no-existeix}" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "library.list" }""")]
    public void Els_parametres_incorrectes_donen_bad_params(string json)
    {
        Assert.Equal("bad_params", Send(json)["error"]?["code"]?.GetValue<string>());
    }

    [Fact]
    public void La_sortida_triada_es_desa_a_la_configuracio()
    {
        var result = Send("""{ "v": 1, "id": 1, "cmd": "output.set", "deviceId": "{a}" }""")["result"]!;
        Assert.Equal("{a}", result["output"]!["configured"]!["id"]!.GetValue<string>());

        var saved = JsonNode.Parse(_library.GetSetting(CommandRouter.OutputSettingKey)!)!;
        Assert.Equal("Altaveus A", saved["name"]!.GetValue<string>());

        _outputs.Ensure();
        var get = Send("""{ "v": 1, "id": 2, "cmd": "output.get" }""")["result"]!["output"]!;
        Assert.Equal("{a}", get["current"]!["id"]!.GetValue<string>());
        Assert.False(get["emergency"]!.GetValue<bool>());
    }

    [Fact]
    public void Llista_les_pistes_d_una_carpeta()
    {
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 9\a.mp3", Title = "A" });
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 9\b.mp3", Title = "B" });
        _library.AddTrack(new Track { Path = @"O:\musica\NADAL\c.mp3", Title = "C" });

        var tracks = Send("""{ "v": 1, "id": 1, "cmd": "library.list", "folder": "O:\\musica\\BOSSA 9", "limit": 10 }""")["result"]!["tracks"]!.AsArray();

        Assert.Equal(["A", "B"], tracks.Select(t => t!["title"]!.GetValue<string>()));
    }
}

/// <summary>Afegir pistes d'una carpeta, per a les proves d'escolta.</summary>
public class QueueFolderCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly QueuePlayer _queue;
    private readonly CommandRouter _router;
    private readonly Library _library;

    public QueueFolderCommandTests()
    {
        _db = Database.Open(_temp.File("carpeta.db"));
        _library = new Library(_db);
        _queue = new QueuePlayer(new Mixer(48000));
        _router = new CommandRouter(new EngineState(), () => { }, _library, () => [], _queue);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Afegeix_pistes_barrejades_d_una_carpeta_sense_les_que_falten()
    {
        for (int i = 0; i < 20; i++) _library.AddTrack(new Track { Path = $@"O:\musica\BOSSA 9\{i}.mp3", Title = $"T{i}" });
        _library.AddTrack(new Track { Path = @"O:\musica\BOSSA 9\falta.mp3", Title = "Falta", FileState = FileState.Missing });

        var response = _router.Handle("""{ "v": 1, "id": 1, "cmd": "queue.insert", "folder": "O:\\musica\\BOSSA 9", "limit": 5 }""");
        var result = response["result"] ?? throw new Xunit.Sdk.XunitException(response.ToJsonString());

        var titles = result["queue"]!["upcoming"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).ToList();
        Assert.Equal(5, titles.Count);
        Assert.DoesNotContain("Falta", titles);
        Assert.Equal(5, titles.Distinct().Count());
    }
}
