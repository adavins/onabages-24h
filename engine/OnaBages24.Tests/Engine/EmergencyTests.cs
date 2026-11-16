using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Pistes d'emergència del disc local (fase 5, pas 4; D-159): una pista que «tarda» a obrir-se (el lector comença aturat,
/// com un NAS que no respon), amb fitxers WAV reals i sense targeta de so.
/// </summary>
public sealed class EmergencyTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly QueuePlayer _player;
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueEnded> _ended = [];
    private readonly List<QueueFailed> _failed = [];
    private readonly List<QueueEmergency> _emergencies = [];
    private readonly HashSet<string> _slow = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<QueueItem> _emergencyTracks = [];

    public EmergencyTests()
    {
        _player = new QueuePlayer(_mixer);
        _player.Started += _started.Add;
        _player.Ended += _ended.Add;
        _player.Failed += _failed.Add;
        _player.EmergencyStarted += _emergencies.Add;
        _player.HoldOpeningForTests = _slow.Contains;
        _player.EmergencyPicker = () => _emergencyTracks.TryDequeue(out var item) ? item : null;
    }

    public void Dispose()
    {
        _player.Dispose();
        _temp.Dispose();
    }

    private string Wav(string name, double seconds, float value = 0.1f)
    {
        string path = _temp.File(name + ".wav");
        var samples = new float[(int)Math.Round(seconds * Rate) * 2];
        Array.Fill(samples, value);
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private QueueItem Item(string name, double seconds, bool slow = false)
    {
        string path = Wav(name, seconds);
        if (slow) _slow.Add(path);
        return new QueueItem { Path = path, Title = name, Origin = QueueOrigin.AutoDj };
    }

    private void AddEmergency(string name, double seconds) =>
        _emergencyTracks.Enqueue(new QueueItem { Path = Wav(name, seconds), Title = name, Origin = QueueOrigin.Emergency });

    private double Seconds(long frames) => frames / (double)Rate;

    /// <summary>Fa sonar com el motor, en blocs de 10 ms (amb pauses curtes perquè els fils de lectura hi arribin).</summary>
    private void Run(double seconds)
    {
        const int block = 480;
        var bytes = new byte[block * 2 * sizeof(float)];
        int blocks = 0;
        for (int done = 0; done < seconds * Rate; done += block)
        {
            _player.Tick();
            var until = DateTime.UtcNow.AddSeconds(2);
            while (_player.IsPreparing && !_slow.Contains(NextPath() ?? "") && DateTime.UtcNow < until)
            {
                Thread.Sleep(1);
                _player.Tick();
            }
            _mixer.Read(bytes);
            if (++blocks % 10 == 0) Thread.Sleep(1);
        }
        _player.Tick();
    }

    private string? NextPath() => _player.NextInPreparation is { } id ? _player.Upcoming.FirstOrDefault(i => i.Id == id)?.Path : null;

    [Fact]
    public void Si_la_seguent_tarda_a_obrir_se_sona_una_d_emergencia_i_quan_es_a_punt_entra_amb_un_fos()
    {
        AddEmergency("E", 30);
        _player.Enqueue(Item("A", 3), Item("B", 5, slow: true));

        Run(4.5);
        Assert.Equal(["A"], _started.Select(s => s.Item.Title));
        Run(1);

        // A s'acaba als 3 s; 2 s després, la d'emergència
        Assert.Equal(["A", "E"], _started.Select(s => s.Item.Title));
        var e = _started[1];
        Assert.Equal(QueueOrigin.Emergency, e.Item.Origin);
        Assert.InRange(Seconds(e.StartFrame) - Seconds(_started[0].StartFrame), 4.9, 5.3);
        Assert.Equal(("B", "E"), (Assert.Single(_emergencies).Waiting.Title, _emergencies[0].Emergency.Title));

        // B ja s'obre: entra de seguida i la d'emergència baixa en 2 s
        long released = _mixer.FramesRendered;
        _player.ReleaseNextForTests();
        Run(3);

        Assert.Equal(["A", "E", "B"], _started.Select(s => s.Item.Title));
        Assert.InRange(Seconds(_started[2].StartFrame - released), 0, 0.3);
        var endedE = Assert.Single(_ended, x => x.Item.Title == "E");
        Assert.InRange(endedE.Played.TotalSeconds, Seconds(released - e.StartFrame) + 1.7, Seconds(released - e.StartFrame) + 2.4);
        Assert.Empty(_failed);
    }

    [Fact]
    public void Un_fitxer_que_no_s_obre_mai_es_dona_per_perdut_als_30_s_i_la_musica_d_emergencia_no_s_atura()
    {
        AddEmergency("E1", 20);
        AddEmergency("E2", 20);
        _player.Enqueue(Item("A", 3), Item("B", 5, slow: true), Item("C", 5));

        Run(33);

        // B s'ha començat a obrir de seguida (A és curta) i als 30 s es dona per perduda; C entra i talla la d'emergència
        var failed = Assert.Single(_failed);
        Assert.Equal("B", failed.Item.Title);
        Assert.Contains("30 s", failed.Message);
        Assert.Equal("C", _started[^1].Item.Title);
        Assert.Contains(_started, s => s.Item.Title == "E1");
        Assert.DoesNotContain(_started, s => s.Item.Title == "B");
    }

    [Fact]
    public void Dues_d_emergencia_seguides_s_encadenen_sense_silenci()
    {
        AddEmergency("E1", 4);
        AddEmergency("E2", 20);
        _player.Enqueue(Item("A", 2), Item("B", 5, slow: true));

        Run(12);

        var e1 = _started.Single(s => s.Item.Title == "E1");
        var e2 = _started.Single(s => s.Item.Title == "E2");
        // E2 comença just quan s'acaba E1 (4 s després), no 2 s més tard
        Assert.InRange(Seconds(e2.StartFrame - e1.StartFrame), 3.95, 4.1);
    }

    [Fact]
    public void Sense_pistes_d_emergencia_espera_la_que_tocava()
    {
        _player.Enqueue(Item("A", 2), Item("B", 3, slow: true));

        Run(5);
        Assert.Equal(["A"], _started.Select(s => s.Item.Title));

        _player.ReleaseNextForTests();
        Run(1);
        Assert.Equal(["A", "B"], _started.Select(s => s.Item.Title));
        Assert.Empty(_emergencies);
    }

    [Fact]
    public void Un_lector_deslligat_ja_no_toca_mai_la_veu()
    {
        var voice = _mixer.AcquireVoice()!;
        var reader = new VoiceReader(voice, Wav("x", 2), Rate, TimeSpan.Zero, null, startPaused: true);
        Thread.Sleep(100); // el fitxer ja és llegit a la memòria i el fil espera per escriure'l
        reader.Cancel();
        reader.ResumeReading();
        Thread.Sleep(100);

        Assert.Equal(0, voice.Buffer.Count);
        reader.Dispose();
    }

    // --- La carpeta ---

    [Fact]
    public void La_carpeta_d_emergencia_avisa_si_no_hi_es_si_es_buida_o_si_no_es_local()
    {
        string folder = _temp.File("emergencia");
        var tracks = new EmergencyTracks(folder, isLocal: _ => true);
        tracks.Refresh();
        Assert.Contains("No es troba", tracks.Problem);

        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "llegeix-me.txt"), "no és àudio");
        tracks.Refresh();
        Assert.Contains("No hi ha cap pista", tracks.Problem);
        Assert.Null(tracks.Pick());

        File.Copy(Wav("Artista - Cançó", 1), Path.Combine(folder, "Artista - Cançó.wav"));
        File.Copy(Wav("Altra", 1), Path.Combine(folder, "Altra.mp3"));
        tracks.Refresh();
        Assert.Null(tracks.Problem);
        Assert.Equal(2, tracks.Count);

        var remote = new EmergencyTracks(folder, isLocal: _ => false);
        remote.Refresh();
        Assert.Contains("disc local", remote.Problem);
        Assert.False(EmergencyTracks.IsLocalDisk(@"\\servidor\musica"));
    }

    [Fact]
    public void Tria_a_l_atzar_sense_repetir_l_ultima_i_amb_l_artista_del_nom()
    {
        string folder = _temp.File("emergencia");
        Directory.CreateDirectory(folder);
        foreach (string name in new[] { "A - Una", "B - Dues", "Tres" }) File.WriteAllText(Path.Combine(folder, name + ".mp3"), "");
        var tracks = new EmergencyTracks(folder, isLocal: _ => true);
        tracks.Refresh();

        string? last = null;
        for (int i = 0; i < 30; i++)
        {
            var item = tracks.Pick()!;
            Assert.NotEqual(last, item.Path);
            Assert.Equal(QueueOrigin.Emergency, item.Origin);
            last = item.Path;
        }
        Assert.Equal(("A", "Una"), EmergencyTracks.NamesFromFile(@"C:\x\A - Una.mp3"));
        Assert.Equal(("", "Tres"), EmergencyTracks.NamesFromFile(@"C:\x\Tres.mp3"));
    }

    [Fact]
    public void La_carpeta_es_canvia_per_ordre_i_ha_de_ser_una_ruta_completa()
    {
        using var db = Database.Open(_temp.File("ob24.db"));
        var library = new Library(db);
        var tracks = new EmergencyTracks(CommandRouter.DefaultEmergencyFolder, isLocal: _ => true);
        var router = new CommandRouter(new EngineState(), () => { }, library, emergency: tracks);
        JsonObject Send(string json) => router.Handle(json);

        string folder = _temp.File("musica-local");
        Directory.CreateDirectory(folder);
        var set = Send($$"""{ "v": 1, "id": 1, "cmd": "emergency.set", "folder": {{JsonValue.Create(folder).ToJsonString()}} }""")["result"]!;
        Assert.Equal(folder, set["folder"]!.GetValue<string>());
        Assert.Equal(0, set["count"]!.GetValue<int>());
        Assert.Contains("No hi ha cap pista", set["problem"]!.GetValue<string>());
        Assert.Equal(folder, CommandRouter.ReadEmergencyFolder(library));

        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 2, "cmd": "emergency.set", "folder": "musica" }""")["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void L_historial_desa_l_origen_emergencia()
    {
        using var db = Database.Open(_temp.File("ob24.db"));
        var library = new Library(db);
        var now = DateTimeOffset.UtcNow;
        library.AddHistory(new Track { Path = @"C:\emergencia\x.mp3", Title = "X" }, now, QueueOrigin.Emergency);

        var entry = Assert.Single(library.SearchHistory(new HistoryFilter { Origins = [QueueOrigin.Emergency] }, 10));
        Assert.Equal(QueueOrigin.Emergency, entry.Origin);
    }
}

/// <summary>El fitxer «en marxa» (fase 5, pas 4; D-159): detectar que el motor no es va tancar bé.</summary>
public sealed class RunMarkerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private DateTimeOffset _now = new(2026, 11, 16, 3, 12, 40, TimeSpan.FromHours(1));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Un_tancament_net_no_es_una_aturada_i_un_que_no_ho_es_si()
    {
        string path = _temp.File(RunMarker.FileName);

        // Primera vegada: no hi ha fitxer
        var first = new RunMarker(path, () => _now);
        Assert.Null(first.Start(_ => false));
        Assert.True(File.Exists(path));
        _now = _now.AddSeconds(10);
        first.Touch();
        first.Stop();
        Assert.False(File.Exists(path));

        // Tancament net: res a dir
        var second = new RunMarker(path, () => _now);
        Assert.Null(second.Start(_ => false));
        _now = _now.AddSeconds(10);
        second.Touch(); // l'última senyal de vida: 03:13:00

        // Peta (no crida Stop): el següent ho detecta, amb l'hora de l'última senyal
        _now = _now.AddMinutes(4);
        var third = new RunMarker(path, () => _now);
        var stop = third.Start(_ => false);
        Assert.NotNull(stop);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 3, 13, 0, TimeSpan.FromHours(1)), stop.LastAlive);
        string text = RunMarker.Text(stop, _now);
        Assert.Contains("aturada inesperada", text);
        Assert.Contains("16/11/2026", text);
        Assert.Contains("(ha estat aturat 4 min)", text);
    }

    [Fact]
    public void Si_el_fitxer_es_d_un_altre_motor_que_funciona_no_es_una_aturada()
    {
        string path = _temp.File(RunMarker.FileName);
        File.WriteAllText(path, """{ "pid": 999999, "lastAlive": "2026-11-16T03:13:00+01:00" }""");

        Assert.Null(new RunMarker(path, () => _now).Start(pid => pid == 999999));
    }

    [Fact]
    public void Un_fitxer_malmes_compta_com_a_aturada_sense_hora()
    {
        string path = _temp.File(RunMarker.FileName);
        File.WriteAllText(path, "{ això no és json");

        var stop = new RunMarker(path, () => _now).Start(_ => false);

        Assert.NotNull(stop);
        Assert.Null(stop.LastAlive);
        Assert.Contains("no se sap", RunMarker.Text(stop, _now));
    }
}
