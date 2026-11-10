using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// TALLA a l'hora exacta (fase 2, pas 4; D-021): el tall a la mostra, el rellotge de mostres i la cadena sencera
/// planificador → cua → mesclador, amb fitxers WAV reals i sense targeta de so. Cada pista té un valor constant diferent:
/// a la sortida es veu quina sona i on comença, mostra a mostra.
/// </summary>
public sealed class CutTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueEnded> _ended = [];

    public void Dispose() => _temp.Dispose();

    private QueuePlayer NewPlayer()
    {
        var player = new QueuePlayer(_mixer);
        player.Started += _started.Add;
        player.Ended += _ended.Add;
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

    private static QueueItem Item(string path, string title, long? eventId = null) =>
        new() { Path = path, Title = title, Origin = eventId is null ? QueueOrigin.AutoDj : QueueOrigin.Event, EventId = eventId,
                EventAt = eventId is null ? null : new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero) };

    /// <summary>Fa sonar <paramref name="frames"/> fotogrames a blocs de 10 ms, amb un Tick abans de cada bloc (com el motor).</summary>
    private float[] Run(QueuePlayer player, int frames, Action? beforeBlock = null)
    {
        const int block = 480;
        var all = new float[frames * 2];
        var bytes = new byte[block * 2 * sizeof(float)];
        for (int done = 0; done < frames; done += block)
        {
            beforeBlock?.Invoke();
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

    /// <summary>Espera que la reserva tingui dades (el fitxer s'obre en un altre fil), com passa amb els 30 s de marge.</summary>
    private static void WaitReady() => Thread.Sleep(300);

    private static int FirstAtLeast(float[] stereo, float value, int fromFrame = 0)
    {
        for (int f = fromFrame; f < stereo.Length / 2; f++)
            if (stereo[f * 2] >= value - 1e-4f) return f;
        return -1;
    }

    // --- Tall a la cua ---

    [Fact]
    public void El_tall_comenca_a_la_mostra_exacta_i_la_que_sonava_fa_el_fos_alhora()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 10, 0.1f), "A"), Item(Wav("x", 5, 0.3f), "Ja hi era"));
        var output = new List<float>(Run(player, Rate)); // A sona des de fa 1 s
        var cut = Item(Wav("b", 3, 0.5f), "Programa", eventId: 7);
        var reservation = player.Reserve(cut);
        Assert.NotNull(reservation);
        WaitReady();

        long frame = 3 * Rate + 123; // una mostra qualsevol, no a l'inici d'un bloc
        var removed = player.CutAt(frame, TimeSpan.FromMilliseconds(500), [cut, Item(Wav("c", 1, 0.7f), "Tancament")], clearQueue: false, reservation);
        Assert.Empty(removed);
        Assert.Equal(["Programa", "Tancament", "Ja hi era"], player.Upcoming.Select(i => i.Title));
        output.AddRange(Run(player, 3 * Rate));
        var all = output.ToArray();

        var started = _started.Single(s => s.Item.Title == "Programa");
        Assert.Equal(frame, started.StartFrame);
        Assert.Equal(frame, started.CutFrame);
        Assert.Equal(frame, FirstAtLeast(all, 0.5f));                 // a la sortida, just a la mostra
        Assert.Equal(0.1f, all[(frame - 1) * 2], 4);                   // fins aquí, A sencera (sense silenci)
        Assert.InRange(all[(frame + Rate / 4) * 2], 0.5f, 0.6f);       // durant el fos, A encara se sent una mica
        Assert.Equal(0.5f, all[(frame + Rate * 6 / 10) * 2], 4);       // i al cap de 500 ms ja no
        var a = _ended.Single(e => e.Item.Title == "A");
        Assert.False(a.Complete);
    }

    [Fact]
    public void Esborra_la_cua_i_retorna_el_que_ha_tret()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 10, 0.1f), "A"), Item(Wav("x", 5, 0.3f), "Promo", eventId: 3), Item(Wav("y", 5, 0.3f), "Música"));
        Run(player, Rate);

        var removed = player.CutAt(2 * Rate, TimeSpan.Zero, [Item(Wav("b", 3, 0.5f), "Programa", eventId: 7)], clearQueue: true);

        Assert.Equal(["Promo", "Música"], removed.Select(i => i.Title));
        Assert.Equal(["Programa"], player.Upcoming.Select(i => i.Title));
        Run(player, 2 * Rate);
        Assert.Equal(2 * Rate, _started.Single(s => s.Item.Title == "Programa").StartFrame); // sense reserva, també a l'hora
    }

    [Fact]
    public void Si_la_seguent_ja_havia_de_comencar_abans_del_tall_comenca_i_es_ella_la_que_es_talla()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 2, 0.1f), "A"), Item(Wav("b", 10, 0.3f), "B"));
        Run(player, Rate + Rate / 2); // A acaba a 2 s i B ja està programada

        long frame = 2 * Rate + Rate / 2;
        player.CutAt(frame, TimeSpan.FromMilliseconds(100), [Item(Wav("p", 2, 0.5f), "Programa", eventId: 7)], clearQueue: false);
        Run(player, 2 * Rate);

        Assert.Equal(["A", "B", "Programa"], _started.Select(s => s.Item.Title));
        Assert.Equal(_started[0].StartFrame + 2 * Rate, _started[1].StartFrame); // B, al final d'A, sense silenci
        Assert.Equal(frame, _started[2].StartFrame);                   // el programa, a l'hora
        Assert.False(_ended.Single(e => e.Item.Title == "B").Complete); // B s'ha tallat
    }

    [Fact]
    public void Si_s_hi_arriba_tard_talla_de_seguida()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 10, 0.1f), "A"));
        Run(player, 2 * Rate);

        player.CutAt(Rate, TimeSpan.Zero, [Item(Wav("p", 2, 0.5f), "Programa", eventId: 7)], clearQueue: false); // fa 1 s
        Run(player, Rate / 2);

        var started = _started.Single(s => s.Item.Title == "Programa");
        Assert.InRange(started.StartFrame, 2 * Rate, 2 * Rate + Rate / 10); // de seguida
        Assert.Equal(Rate, started.CutFrame);                               // i se sap quant tard: 1 s
    }

    [Fact]
    public void Si_la_primera_pista_no_es_pot_obrir_comenca_la_seguent_a_l_hora()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 10, 0.1f), "A"));
        Run(player, Rate);

        long frame = 2 * Rate;
        player.CutAt(frame, TimeSpan.Zero, [Item(_temp.File("no-hi-es.mp3"), "Falta", 7), Item(Wav("p", 2, 0.5f), "Programa", 7)], clearQueue: false);
        var output = Run(player, 2 * Rate);

        Assert.Equal(frame, _started.Single(s => s.Item.Title == "Programa").StartFrame);
        Assert.Equal(frame - Rate, FirstAtLeast(output, 0.5f)); // mai silenci: d'A al programa directament
    }

    [Fact]
    public void Un_ENCUA_poc_abans_del_tall_va_darrere_de_la_pista_del_tall()
    {
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 10, 0.1f), "A"));
        Run(player, Rate);
        player.CutAt(3 * Rate, TimeSpan.Zero, [Item(Wav("p", 2, 0.5f), "Programa", 7)], clearQueue: false);

        player.Insert(0, Item(Wav("x", 1, 0.3f), "Promo", 8));

        Assert.Equal(["Programa", "Promo"], player.Upcoming.Select(i => i.Title));
        Run(player, 3 * Rate);
        Assert.Equal(3 * Rate, _started.Single(s => s.Item.Title == "Programa").StartFrame);
    }

    [Fact]
    public void Una_reserva_que_no_es_fa_servir_allibera_la_veu()
    {
        var player = NewPlayer();
        var reservation = player.Reserve(Item(Wav("p", 2, 0.5f), "Programa", 7));
        Assert.Contains(_mixer.Voices, v => v.State != VoiceState.Idle);

        player.Release(reservation!.Value);

        Assert.All(_mixer.Voices, v => Assert.Equal(VoiceState.Idle, v.State));
    }

    // --- Rellotge de mostres ---

    [Fact]
    public void El_rellotge_de_mostres_converteix_l_hora_en_fotograma_i_al_reves()
    {
        long t0 = 1_000_000_000;
        long now = t0 + Stopwatch.Frequency / 50; // l'últim bloc es va mesclar fa 20 ms
        var wall = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new SampleClock(Rate, () => (480_000, t0), () => now, () => wall, () => TimeSpan.FromMilliseconds(100));

        // El fotograma 480.000 sonarà a les 12:00:00,080 (−20 ms + 100 ms de latència)
        Assert.Equal(480_000, clock.FrameAt(wall.AddMilliseconds(80)));
        Assert.Equal(480_000 + Rate * 920 / 1000, clock.FrameAt(wall.AddSeconds(1)));
        Assert.Equal(wall.AddSeconds(1), clock.TimeOf(480_000 + Rate * 920 / 1000));
    }

    [Fact]
    public void Sense_sortida_el_rellotge_de_mostres_no_respon()
    {
        var wall = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var never = new SampleClock(Rate, () => (0, 0), () => 5, () => wall, () => TimeSpan.Zero);
        var stopped = new SampleClock(Rate, () => (1000, 1), () => 1 + Stopwatch.Frequency * 2, () => wall, () => TimeSpan.Zero); // fa 2 s

        Assert.Null(never.FrameAt(wall));
        Assert.Null(stopped.FrameAt(wall));
        Assert.Null(stopped.TimeOf(1000));
    }

    // --- Cadena sencera ---

    [Fact]
    public void Un_TALLA_programat_sona_a_la_mostra_de_l_hora_i_el_registre_ho_diu()
    {
        using var db = Database.Open(_temp.File("talla.db"));
        var library = new Library(db);
        var events = new Events(db);
        long sub = library.EnsureSubcategory(library.EnsureCategory("Programes"), "Diaris");
        long track = library.AddTrack(new Track { Path = Wav("programa", 3, 0.5f), SubcategoryId = sub, Title = "Programa", Type = TrackType.Program, DurationMs = 3000 });
        var player = NewPlayer();
        player.Enqueue(Item(Wav("a", 60, 0.1f), "Música"));

        // Rellotge fals, però el del mesclador és el de veritat: el mesclador «sona» quan el fem córrer
        var now = new DateTimeOffset(2026, 11, 20, 9, 59, 50, TimeSpan.Zero);
        var sampleClock = new SampleClock(_mixer, () => TimeSpan.Zero, () => now);
        var executor = new EventExecutor(library, player, null, clock: sampleClock);
        var scheduler = new Scheduler(events, executor, NullLogger.Instance, clock: () => now, zone: TimeZoneInfo.Utc);
        var fired = new List<ExecutionRecord>();
        scheduler.Fired += fired.Add;
        events.Save(new ScheduledEvent { Name = "Programa", Mode = EventMode.Cut, StartTime = new TimeOnly(10, 0), FadeMs = 200,
            Actions = [new EventAction { Type = EventActionType.Track, Params = $$"""{"songId":{{track}}}""" }] });

        // De 9:59:50 a 10:00:01: a cada bloc de 10 ms de so, el rellotge fals avança 10 ms
        long? cutFrame = null;
        Run(player, 11 * Rate, () =>
        {
            now += TimeSpan.FromMilliseconds(10);
            if (cutFrame is null && now >= new DateTimeOffset(2026, 11, 20, 9, 59, 58, TimeSpan.Zero))
                cutFrame = _mixer.FramesRendered + 2 * Rate; // on hauria de caure, segons el mesclador
            scheduler.Tick();
        });

        var started = _started.Single(s => s.Item.Title == "Programa");
        Assert.Equal(started.CutFrame, started.StartFrame);
        Assert.InRange(started.StartFrame, cutFrame!.Value - Rate / 100, cutFrame.Value + Rate / 100); // ±10 ms (el temps real de la prova)
        var record = Assert.Single(events.Executions(now.AddHours(-1), now.AddHours(1)));
        Assert.Equal(ExecutionResult.Ok, record.Result);
        Assert.NotNull(record.ExecutedAt);
        Assert.Contains("ha començat a la mostra prevista", record.Detail);
        Assert.Contains("fos de 200 ms", record.Detail);
        Assert.Equal(2, fired.Count); // en programar-lo i quan ha sonat
    }
}
