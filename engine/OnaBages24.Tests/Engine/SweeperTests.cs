using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Trepitjador (fase 3, pas 5): l'AutoDJ enganxa un sweeper de la categoria activa a les pistes dels passos que en
/// demanen, i <see cref="SweeperOverlay"/> el fa sonar a sobre de l'inici de la pista, amb la música atenuada.
/// Les proves de so fan servir fitxers WAV de valor constant: a la sortida es veu què sona, mostra a mostra.
/// </summary>
public sealed class SweeperTests : IDisposable
{
    private const int Rate = 48000;
    private const float First = 0.1f;   // la pista que sona abans
    private const float Song = 0.2f;    // la pista amb trepitjador
    private const float Sweep = 0.3f;   // el sweeper

    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Mixer _mixer = new(Rate);
    private readonly QueuePlayer _queue;
    private readonly AuxPlayers _aux;
    private readonly SweeperOverlay _overlay;
    private readonly long _music;
    private readonly long _sweepersCategory;
    private readonly List<QueueStarted> _started = [];
    private readonly List<AuxStarted> _auxStarted = [];
    private int _next;

    public SweeperTests()
    {
        _db = Database.Open(_temp.File("trepitjador.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _queue = new QueuePlayer(_mixer);
        _aux = new AuxPlayers(_mixer);
        _overlay = new SweeperOverlay(_queue, _aux);
        _music = _library.EnsureCategory("Music");
        _sweepersCategory = _library.EnsureCategory("Sweepers");
        _queue.Started += _started.Add;
        _aux.Started += _auxStarted.Add;
    }

    public void Dispose()
    {
        _aux.Dispose();
        _queue.Dispose();
        _db.Dispose();
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

    private long AddTrack(long subcategory, string title, TrackType type = TrackType.Music) =>
        _library.AddTrack(new Track
        {
            Path = _temp.File($"pista{++_next}.mp3"), SubcategoryId = subcategory, Title = title, Type = type,
            DurationMs = 200_000, StartMs = 0, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
        });

    private AutoDj NewAutoDj() => new(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = 3 }, random: new Random(1));

    // --- L'AutoDJ tria el sweeper ---

    [Fact]
    public void Els_passos_amb_trepitjador_porten_el_sweeper_menys_recent_de_la_categoria_activa()
    {
        long dance = _library.EnsureSubcategory(_music, "Dance90");
        long sweepers = _library.EnsureSubcategory(_sweepersCategory, "Dance90");
        for (int i = 0; i < 3; i++) AddTrack(dance, $"Cançó {i}");
        long old = AddTrack(sweepers, "SW vell", TrackType.Sweeper);
        long recent = AddTrack(sweepers, "SW recent", TrackType.Sweeper);
        _library.AddHistory(_library.GetTrack(recent)!, DateTimeOffset.UtcNow.AddMinutes(-5), QueueOrigin.AutoDj);
        _library.AddHistory(_library.GetTrack(old)!, DateTimeOffset.UtcNow.AddMinutes(-60), QueueOrigin.AutoDj);
        long rotation = _rotations.Save("Dance90", [
            new RotationStep { SubcategoryId = dance, Sweeper = true },
            new RotationStep { SubcategoryId = dance },
            new RotationStep { SubcategoryId = dance, Sweeper = true },
        ]);
        var autoDj = NewAutoDj();
        autoDj.Configure(true, rotation);
        Assert.True(autoDj.SetSweepers(sweepers, "a mà"));

        autoDj.Tick(); autoDj.Tick(); autoDj.Tick();

        var queue = _queue.Upcoming;
        Assert.Equal(old, queue[0].Sweeper?.TrackId);    // el que fa més temps que no sona
        Assert.Null(queue[1].Sweeper);                   // el pas no en demana
        Assert.Equal(recent, queue[2].Sweeper?.TrackId); // no el mateix que una altra pista de la cua
        Assert.Null(autoDj.Problem);
    }

    [Fact]
    public void Sense_categoria_de_sweepers_la_pista_entra_sense_i_es_diu()
    {
        long dance = _library.EnsureSubcategory(_music, "Dance90");
        AddTrack(dance, "Cançó");
        long rotation = _rotations.Save("Dance90", [new RotationStep { SubcategoryId = dance, Sweeper = true }]);
        var autoDj = NewAutoDj();
        autoDj.Configure(true, rotation);

        autoDj.Tick();

        Assert.Null(Assert.Single(_queue.Upcoming).Sweeper);
        Assert.Contains("categoria de sweepers", autoDj.Problem);
    }

    [Fact]
    public void Canviar_la_categoria_de_sweepers_diu_qui_ho_ha_fet_i_avisa_per_desar_la()
    {
        long sweepers = _library.EnsureSubcategory(_sweepersCategory, "Ona Bages");
        var autoDj = NewAutoDj();
        var saved = new List<long?>();
        autoDj.SweepersChanged += saved.Add;

        Assert.False(autoDj.SetSweepers(9999, "a mà")); // no existeix: no canvia res
        Assert.True(autoDj.SetSweepers(sweepers, "esdeveniment «Dance90»"));

        Assert.Equal(sweepers, autoDj.SweepersSubcategoryId);
        Assert.Equal("Sweepers / Ona Bages", autoDj.SweepersName);
        Assert.Equal("esdeveniment «Dance90»", autoDj.SweepersChangedBy);
        Assert.Equal([sweepers], saved);
    }

    [Fact]
    public void L_accio_SWEEPERS_d_un_esdeveniment_canvia_la_categoria()
    {
        long sweepers = _library.EnsureSubcategory(_sweepersCategory, "Elektro90");
        var events = new Events(_db);
        var autoDj = NewAutoDj();
        var now = new DateTimeOffset(2026, 11, 20, 9, 59, 50, TimeSpan.Zero);
        var executor = new EventExecutor(_library, _queue, autoDj);
        var scheduler = new Scheduler(events, executor, NullLogger.Instance, clock: () => now, zone: TimeZoneInfo.Utc);
        events.Save(new ScheduledEvent
        {
            Name = "Elektro", Mode = EventMode.System, StartTime = new TimeOnly(10, 0),
            Actions = [new EventAction { Type = EventActionType.Sweepers, Params = "{\"subcategoriaId\":" + sweepers + "}" }],
        });

        for (int i = 0; i < 30; i++)
        {
            now += TimeSpan.FromSeconds(1);
            scheduler.Tick();
        }

        Assert.Equal(sweepers, autoDj.SweepersSubcategoryId);
        Assert.Equal("esdeveniment «Elektro»", autoDj.SweepersChangedBy);
        var record = Assert.Single(events.Executions(now.AddHours(-1), now.AddHours(1)));
        Assert.Equal(ExecutionResult.Ok, record.Result);
        Assert.Contains("sweepers «Sweepers / Elektro90»", record.Detail);
    }

    // --- El so ---

    /// <summary>Fa sonar <paramref name="seconds"/> segons a blocs de 10 ms, amb els Tick com el motor. Retorna la sortida.</summary>
    private float[] Run(double seconds, Action<int>? everyBlock = null)
    {
        const int block = 480;
        var all = new List<float>();
        var bytes = new byte[block * 2 * sizeof(float)];
        var samples = new float[block * 2];
        int blocks = (int)Math.Round(seconds * 100);
        for (int b = 0; b < blocks; b++)
        {
            everyBlock?.Invoke(b);
            _overlay.Tick();
            _aux.Tick();
            _queue.Tick();
            var wait = DateTime.UtcNow.AddSeconds(5);
            while (_queue.IsPreparing && DateTime.UtcNow < wait)
            {
                Thread.Sleep(1);
                _queue.Tick();
            }
            // Els fitxers s'obren i es llegeixen en altres fils: la prova va més de pressa que el temps real
            if (b % 10 == 0) Thread.Sleep(20);
            _mixer.Read(bytes);
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            all.AddRange(samples);
        }
        _overlay.Tick();
        _aux.Tick();
        _queue.Tick();
        return all.ToArray();
    }

    private QueueItem SongWithSweeper(double songSeconds = 3) => new()
    {
        Path = Wav("canco", songSeconds, Song), Title = "Cançó",
        Sweeper = new QueueItem { Path = Wav("sweeper", 0.5, Sweep), Title = "SW Dance90", Type = TrackType.Sweeper },
    };

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    [Fact]
    public void El_sweeper_comenca_al_mateix_fotograma_que_la_pista_i_la_musica_baixa()
    {
        _queue.Enqueue(new QueueItem { Path = Wav("primera", 1.5, First), Title = "Primera" }, SongWithSweeper());

        var output = Run(3.5);

        var song = Assert.Single(_started, s => s.Item.Title == "Cançó");
        var sweeper = Assert.Single(_auxStarted);
        Assert.Equal(AuxPlayers.SweeperPlayer, sweeper.Player);
        Assert.Equal(song.StartFrame, sweeper.StartFrame); // exactament a l'inici de la pista
        int at = (int)song.StartFrame;
        Assert.Equal(Song * Db(SweeperOverlay.StandardDuckingDb) + Sweep, output[(at + 100) * 2], 4); // pista atenuada i sweeper
        Assert.Equal(Song, output[(at + Rate) * 2], 4);                                            // acabat el sweeper: la pista, sencera
        Assert.Equal(First, output[(at - Rate / 10) * 2], 4);                                      // abans: la primera, sense atenuar
    }

    [Fact]
    public void Amb_atenuacio_0_el_sweeper_sona_sense_baixar_la_musica()
    {
        _overlay.DuckingDb = 0;
        _queue.Enqueue(new QueueItem { Path = Wav("primera", 1, First), Title = "Primera" }, SongWithSweeper());

        var output = Run(2.5);

        int at = (int)Assert.Single(_auxStarted).StartFrame;
        Assert.Equal(Song + Sweep, output[(at + 100) * 2], 4);
    }

    [Fact]
    public void Si_es_salta_a_la_seguent_el_sweeper_tambe_entra_a_l_inici()
    {
        _queue.Enqueue(new QueueItem { Path = Wav("primera", 10, First), Title = "Primera" }, SongWithSweeper());

        // Al cap d'1 s, l'operador salta a la següent (la que té el sweeper)
        Run(3, b => { if (b == 100) _queue.Skip(TimeSpan.FromMilliseconds(100)); });

        var song = Assert.Single(_started, s => s.Item.Title == "Cançó");
        var sweeper = Assert.Single(_auxStarted);
        Assert.InRange(sweeper.StartFrame - song.StartFrame, 0, Rate / 10); // com a molt 100 ms tard (els blocs de la prova)
    }

    [Fact]
    public void Si_la_pista_surt_de_la_cua_el_sweeper_no_sona_i_la_veu_s_allibera()
    {
        var song = SongWithSweeper();
        _queue.Enqueue(new QueueItem { Path = Wav("primera", 2, First), Title = "Primera" }, song);
        Run(0.5);
        Assert.Contains(_mixer.Voices, v => v.State == VoiceState.Reserved); // el sweeper, obert per endavant

        Assert.True(_queue.Remove(song.Id));
        Run(2.5);

        Assert.Empty(_auxStarted);
        Assert.All(_mixer.Voices, v => Assert.Equal(VoiceState.Idle, v.State)); // s'ha acabat la primera i no queda cap veu agafada
    }

    [Fact]
    public void El_sweeper_s_anota_a_l_historial_com_una_pista_mes()
    {
        _ = new PlayHistory(_queue, _library, NullLogger.Instance, _aux);
        long sweeperId = _library.AddTrack(new Track { Path = Wav("sweeper-bib", 0.5, Sweep), Title = "SW Dance90", Type = TrackType.Sweeper });
        var song = SongWithSweeper() with { Sweeper = QueueItem.FromTrack(_library.GetTrack(sweeperId)!, QueueOrigin.AutoDj) };
        _queue.Enqueue(song);

        Run(1.5);

        var history = _library.History(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        var entry = Assert.Single(history, h => h.TrackId == sweeperId);
        Assert.True(entry.Complete);
        Assert.NotNull(_library.GetTrack(sweeperId)!.LastPlayed); // perquè «el menys recent» funcioni
    }

    // --- Ordres ---

    [Fact]
    public void Ordre_sweepers_set_i_l_estat_de_l_AutoDJ_la_porta()
    {
        long sweepers = _library.EnsureSubcategory(_sweepersCategory, "Ona Bages");
        var autoDj = NewAutoDj();
        var router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: autoDj, rotations: _rotations);
        JsonObject Send(string json) => router.Handle(json);

        Assert.Null(Send("""{ "v": 1, "id": 1, "cmd": "autodj.get" }""")["result"]!["autodj"]!["sweepers"]);
        var set = Send($$"""{ "v": 1, "id": 2, "cmd": "sweepers.set", "subcategoryId": {{sweepers}} }""")["result"]!["autodj"]!["sweepers"]!;
        Assert.Equal(sweepers, set["subcategoryId"]!.GetValue<long>());
        Assert.Equal("Sweepers / Ona Bages", set["name"]!.GetValue<string>());
        Assert.Equal("a mà", set["changedBy"]!.GetValue<string>());

        Assert.Null(Send("""{ "v": 1, "id": 3, "cmd": "sweepers.set", "subcategoryId": null }""")["result"]!["autodj"]!["sweepers"]);
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 4, "cmd": "sweepers.set" }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 5, "cmd": "sweepers.set", "subcategoryId": 9999 }""")["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send("""{ "v": 1, "id": 6, "cmd": "sweepers.set", "subcategoryId": "Dance" }""")["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void La_categoria_de_sweepers_desada_es_recupera_en_arrencar()
    {
        long sweepers = _library.EnsureSubcategory(_sweepersCategory, "Dance90");
        _library.SetSetting(EngineServer.SweepersSettingKey, "{\"subcategoryId\":" + sweepers + "}");
        var autoDj = NewAutoDj();

        EngineServer.ConfigureSweepers(autoDj, _library, NullLogger.Instance);

        Assert.Equal(sweepers, autoDj.SweepersSubcategoryId);
        Assert.Equal("en arrencar el motor", autoDj.SweepersChangedBy);
    }

    [Fact]
    public void A_la_cua_cada_pista_diu_quin_sweeper_portara()
    {
        var item = QueueJson.Item(SongWithSweeper());
        Assert.Equal("SW Dance90", item["sweeper"]!["title"]!.GetValue<string>());
        Assert.Equal("sweeper", item["sweeper"]!["type"]!.GetValue<string>());
        Assert.Null(QueueJson.Item(new QueueItem { Path = "x.mp3" })["sweeper"]);
    }
}
