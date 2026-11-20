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

    /// <summary>Una pista el fitxer de la qual no existeix (com quan la unitat de xarxa desapareix: falla a l'instant).</summary>
    private QueueItem Missing(string name) => new() { Path = _temp.File(name + ".wav"), Title = name, Origin = QueueOrigin.AutoDj };

    [Fact]
    public void Si_les_pistes_fallen_seguides_a_l_instant_sona_una_d_emergencia_de_seguida()
    {
        // La nit del NAS (20-11-2026): totes les pistes fallaven en uns ms i l'espera de 2 s no s'arribava a complir mai
        AddEmergency("E", 30);
        _player.Enqueue(Enumerable.Range(1, 50).Select(i => Missing($"falta-{i}")).ToArray());

        Run(1);

        var e = Assert.Single(_started);
        Assert.Equal(("E", QueueOrigin.Emergency), (e.Item.Title, e.Item.Origin));
        Assert.InRange(Seconds(e.StartFrame), 0, 1);
        // Només n'han fallat unes quantes (fins que la d'emergència sona; després, la següent es prepara prop del seu punt de mescla)
        Assert.InRange(_failed.Count, 3, 10);
        Assert.Equal(Enumerable.Range(1, _failed.Count), _failed.Select(f => f.InRow));
    }

    [Fact]
    public void Mentre_continuen_fallant_les_d_emergencia_s_encadenen_sense_silenci()
    {
        AddEmergency("E1", 3);
        AddEmergency("E2", 30);
        _player.Enqueue(Enumerable.Range(1, 50).Select(i => Missing($"falta-{i}")).ToArray());

        Run(5);

        Assert.Equal(["E1", "E2"], _started.Select(s => s.Item.Title));
        // E2 entra quan E1 s'acaba (al seu punt de mescla), sense forat
        Assert.InRange(Seconds(_started[1].StartFrame - _started[0].StartFrame), 2.8, 3.1);
    }

    [Fact]
    public void Si_la_unitat_no_respon_les_pistes_s_esperen_a_la_cua_i_quan_torna_entren_de_seguida()
    {
        // D-197: la unitat de la música no respon (ja se sap): no s'obre ni se salta cap pista; sonen les d'emergència
        bool down = true;
        var tracks = Enumerable.Range(1, 50).Select(i => Item($"nas-{i}", 3)).ToArray();
        var nas = tracks.Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _player.IsUnavailable = path => down && nas.Contains(path);
        AddEmergency("E", 30);
        _player.Enqueue(tracks);

        Run(2);

        var e = Assert.Single(_started);
        Assert.Equal("E", e.Item.Title);
        Assert.InRange(Seconds(e.StartFrame), 0, 1);
        Assert.Empty(_failed);
        Assert.Equal(50, _player.Upcoming.Count);
        Assert.True(_player.WaitingForDrive);

        // Torna: la primera de la cua entra de seguida (la d'emergència baixa amb un fos), no en acabar la cançó
        down = false;
        long back = _mixer.FramesRendered;
        Run(1.5);

        Assert.Equal(["E", "nas-1"], _started.Select(s => s.Item.Title));
        Assert.InRange(Seconds(_started[1].StartFrame - back), 0, 0.5);
        Assert.False(_player.WaitingForDrive);
    }

    [Fact]
    public void Les_pistes_d_un_esdeveniment_no_s_esperen_la_unitat()
    {
        // Un programa que sonés tard no és el que toca: falla com sempre (decisió del desenvolupador, D-197)
        var program = Missing("programa") with { Origin = QueueOrigin.Event };
        _player.IsUnavailable = path => path == program.Path;
        _player.Enqueue(program);

        Run(0.5);

        Assert.Equal("programa", Assert.Single(_failed).Item.Title);
        Assert.Empty(_player.Upcoming);
    }

    [Fact]
    public void Una_pista_que_torna_a_sonar_posa_el_comptador_de_fallades_a_zero()
    {
        _player.Enqueue(Missing("falta-1"), Missing("falta-2"), Item("A", 3), Missing("falta-3"));

        Run(4);

        Assert.Equal(["A"], _started.Select(s => s.Item.Title));
        Assert.Equal([1, 2, 1], _failed.Select(f => f.InRow));
    }

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

/// <summary>
/// El fitxer «en marxa» (fase 5, passos 4 i 7; D-159, D-162): qualsevol aturada anterior és una incidència, amb el motiu
/// que va deixar escrit o el que diu el registre de Windows.
/// </summary>
public sealed class RunMarkerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private DateTimeOffset _now = new(2026, 11, 16, 3, 12, 40, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset LastAlive = new(2026, 11, 16, 3, 13, 0, TimeSpan.FromHours(1));

    public void Dispose() => _temp.Dispose();

    private string MarkerPath => _temp.File(RunMarker.FileName);

    [Fact]
    public void Una_aturada_amb_motiu_el_deixa_escrit_i_una_que_peta_no()
    {
        // Primera vegada: no hi ha fitxer
        var first = new RunMarker(MarkerPath, () => _now);
        Assert.Null(first.Start(_ => false));
        Assert.True(File.Exists(MarkerPath));
        _now = _now.AddSeconds(10);
        first.Stop(StopReason.Other); // el tancament, abans que arribi el senyal de la consola
        first.Stop(StopReason.CtrlC); // el motiu més concret es queda
        first.Stop(StopReason.Other);
        Assert.Equal(StopReason.CtrlC, first.Reason);
        first.Touch(); // una senyal de vida tardana no l'esborra

        // La vegada següent ho sap
        var second = new RunMarker(MarkerPath, () => _now);
        var stop = second.Start(_ => false);
        Assert.Equal(new PreviousStop(LastAlive.AddSeconds(-10), StopReason.CtrlC), stop);
        _now = _now.AddSeconds(10);
        second.Touch(); // 03:13:00

        // Peta (no crida Stop): el següent ho detecta, sense motiu i amb l'hora de l'última senyal
        _now = _now.AddMinutes(4);
        stop = new RunMarker(MarkerPath, () => _now).Start(_ => false);
        Assert.Equal(new PreviousStop(LastAlive, null), stop);
    }

    [Fact]
    public void Si_el_fitxer_es_d_un_altre_motor_que_funciona_no_es_una_aturada()
    {
        File.WriteAllText(MarkerPath, """{ "pid": 999999, "lastAlive": "2026-11-16T03:13:00+01:00" }""");

        Assert.Null(new RunMarker(MarkerPath, () => _now).Start(pid => pid == 999999));
    }

    [Fact]
    public void Un_fitxer_malmes_compta_com_a_aturada_sense_hora()
    {
        File.WriteAllText(MarkerPath, "{ això no és json");

        var stop = new RunMarker(MarkerPath, () => _now).Start(_ => false);

        Assert.Equal(new PreviousStop(null, null), stop);
        var (severity, text) = RunMarker.Describe(stop!, _now, null, []);
        Assert.Equal(IncidentSeverity.Danger, severity);
        Assert.Contains("no se sap", text);
    }

    private (IncidentSeverity Severity, string Text) Describe(StopReason? reason, DateTimeOffset? boot = null, params WindowsShutdownEvent[] events) =>
        RunMarker.Describe(new PreviousStop(LastAlive, reason), LastAlive.AddMinutes(4), boot, events);

    [Fact]
    public void Les_aturades_demanades_son_un_avis_amb_el_motiu()
    {
        Assert.Equal((IncidentSeverity.Warning, "El motor es va aturar amb l'ordre d'aturada (manteniment o desenvolupament) el 16/11/2026 a les 03:13:00 (ha estat aturat 4 min)"),
            Describe(StopReason.Command));
        Assert.Equal((IncidentSeverity.Warning, "El motor es va aturar amb Ctrl+C a la consola el 16/11/2026 a les 03:13:00 (ha estat aturat 4 min)"),
            Describe(StopReason.CtrlC));
        Assert.Contains("es va tancar la seva consola", Describe(StopReason.ConsoleClosed).Text);
        Assert.Equal(IncidentSeverity.Warning, Describe(StopReason.Other).Severity);
    }

    [Fact]
    public void Si_peta_sense_que_Windows_s_hagi_reiniciat_es_greu()
    {
        var (severity, text) = Describe(null, boot: LastAlive.AddDays(-3));
        Assert.Equal(IncidentSeverity.Danger, severity);
        Assert.Equal("El motor s'ha reiniciat després d'una aturada inesperada (ha petat o l'han tancat a la força): l'última senyal de vida va ser el 16/11/2026 a les 03:13:00 (ha estat aturat 4 min)", text);
    }

    [Fact]
    public void Un_reinici_de_l_ordinador_diu_qui_l_ha_demanat_o_si_no_es_va_tancar_be()
    {
        var boot = LastAlive.AddMinutes(2);
        var at = LastAlive.AddSeconds(30);
        // Demanat per un usuari: Windows n'anota dos (el de l'usuari i el del sistema)
        var byUser = Describe(null, boot,
            new WindowsShutdownEvent(1074, at, "albertdavins", "Explorer.EXE", false),
            new WindowsShutdownEvent(1074, at, "SYSTEM", @"C:\WINDOWS\system32\winlogon.exe (PC24H)", true));
        Assert.Equal((IncidentSeverity.Warning, "L'ordinador es va apagar o reiniciar (ho va demanar albertdavins) el 16/11/2026 a les 03:13:00 (ha estat aturat 4 min)"), byUser);
        // Una actualització de Windows, amb el motor avisat (senyal de Windows) o sense
        foreach (var reason in new StopReason?[] { StopReason.WindowsShutdown, null })
        {
            var update = Describe(reason, boot, new WindowsShutdownEvent(1074, at, "SYSTEM", @"C:\Windows\servicing\TrustedInstaller.exe (PC24H)", true));
            Assert.Equal(IncidentSeverity.Warning, update.Severity);
            Assert.Contains("per una actualització de Windows", update.Text);
        }
        // Tall de llum o penjada
        var power = Describe(null, boot, new WindowsShutdownEvent(41, boot.AddMinutes(1), null, null, true));
        Assert.Equal(IncidentSeverity.Danger, power.Severity);
        Assert.StartsWith("L'ordinador es va apagar sense tancar-se bé (tall de llum o penjada)", power.Text);
        // Windows no en diu res
        Assert.Equal(IncidentSeverity.Danger, Describe(null, boot).Severity);
    }

    [Fact]
    public void L_hora_d_arrencada_de_Windows_es_d_abans_d_ara()
    {
        Assert.True(WindowsRestarts.BootTime() < DateTimeOffset.Now);
        WindowsRestarts.Read(DateTimeOffset.Now.AddDays(-30), DateTimeOffset.Now); // no ha de petar (si no es pot llegir, cap)
    }
}
