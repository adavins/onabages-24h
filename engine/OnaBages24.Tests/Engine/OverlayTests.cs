using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Mode SUPERPOSA i acció AUXILIAR al planificador (fase 2, pas 5b; D-064): la cadena sencera planificador → reproductors
/// auxiliars → mesclador, amb fitxers WAV reals i sense targeta de so. Cada fitxer té un valor constant: a la sortida es
/// veu què sona i on comença, mostra a mostra.
/// </summary>
public sealed class OverlayTests : IDisposable
{
    private const int Rate = 48000;
    private const float Music = 0.1f;
    private const float Signal = 0.3f;
    private static readonly DateTimeOffset Hour = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly QueuePlayer _queue;
    private readonly AuxPlayers _aux;
    private readonly Scheduler _scheduler;
    private readonly List<AuxStarted> _auxStarted = [];
    private readonly List<ExecutionRecord> _fired = [];
    private DateTimeOffset _now = Hour - TimeSpan.FromSeconds(10);

    public OverlayTests()
    {
        _db = Database.Open(_temp.File("superposa.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _queue = new QueuePlayer(_mixer);
        _aux = new AuxPlayers(_mixer);
        _aux.Started += _auxStarted.Add;
        // Rellotge fals, però el del mesclador és el de veritat: el mesclador «sona» quan el fem córrer
        var sampleClock = new SampleClock(_mixer, () => TimeSpan.Zero, () => _now);
        var executor = new EventExecutor(_library, _queue, null, clock: sampleClock, aux: _aux);
        _scheduler = new Scheduler(_events, executor, NullLogger.Instance, clock: () => _now, zone: TimeZoneInfo.Utc);
        _scheduler.Fired += _fired.Add;
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

    private long Track(string title, double seconds, float value) =>
        _library.AddTrack(new Track { Path = Wav(title, seconds, value), Title = title, DurationMs = (long)(seconds * 1000) });

    private static EventAction Aux(long songId, int? player = null, double? duckingDb = null) => new()
    {
        Type = EventActionType.Auxiliary,
        Params = "{\"songId\":" + songId + (player is null ? "" : ",\"player\":" + player) +
                 (duckingDb is null ? "" : ",\"duckingDb\":" + duckingDb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "}",
    };

    private static EventAction Pista(long songId) => new() { Type = EventActionType.Track, Params = "{\"songId\":" + songId + "}" };

    /// <summary>
    /// Fa sonar fins a <paramref name="until"/> a blocs de 10 ms: a cada bloc, el rellotge fals avança 10 ms i es fan
    /// els Tick (com el motor). Retorna la sortida i el fotograma que ha sonat a l'hora en punt.
    /// </summary>
    private (float[] Output, long First, long HourFrame) Run(DateTimeOffset until)
    {
        const int block = 480;
        var all = new List<float>();
        var bytes = new byte[block * 2 * sizeof(float)];
        var samples = new float[block * 2];
        long first = _mixer.FramesRendered;
        long hourFrame = -1;
        int blocks = 0;
        while (_now < until)
        {
            if (hourFrame < 0 && _now >= Hour) hourFrame = _mixer.FramesRendered;
            _now += TimeSpan.FromMilliseconds(10);
            _scheduler.Tick();
            _aux.Tick();
            _queue.Tick();
            var wait = DateTime.UtcNow.AddSeconds(5);
            while (_queue.IsPreparing && DateTime.UtcNow < wait)
            {
                Thread.Sleep(1);
                _queue.Tick();
            }
            // La prova va molt més de pressa que el temps real: cada mig segon d'àudio, es deixa que els fils de lectura
            // tornin a omplir la memòria de les veus (si no, es buiden i surt silenci, cosa que al motor no pot passar)
            if (++blocks % 50 == 0) WaitForReaders();
            _mixer.Read(bytes);
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            all.AddRange(samples);
            if (_now == Hour - TimeSpan.FromSeconds(5)) Thread.Sleep(300); // els fitxers preparats s'obren en un altre fil
        }
        _aux.Tick();
        return (all.ToArray(), first, hourFrame);
    }

    /// <summary>Espera (com a molt 100 ms) que les veus que sonen tinguin com a mínim 1 s a la memòria.</summary>
    private void WaitForReaders()
    {
        var until = DateTime.UtcNow.AddMilliseconds(100);
        while (DateTime.UtcNow < until
               && _mixer.Voices.Any(v => v.State == VoiceState.Playing && v.Buffer.Count < Rate * 2))
            Thread.Sleep(1);
    }

    private void StartMusic()
    {
        _queue.Enqueue(new QueueItem { Path = Wav("musica", 60, Music), Title = "Música" });
    }

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    [Fact]
    public void Un_SUPERPOSA_sona_a_la_mostra_de_l_hora_per_sobre_de_la_musica_i_el_registre_ho_diu()
    {
        StartMusic();
        long signals = Track("Senyals horaris", 1, Signal);
        _events.Save(new ScheduledEvent { Name = "Senyals", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0), DuckingDb = -6,
            Actions = [Aux(signals)] });

        var (output, first, hourFrame) = Run(Hour + TimeSpan.FromSeconds(2));

        var started = Assert.Single(_auxStarted);
        Assert.Equal(started.TargetFrame, started.StartFrame);                         // a la mostra prevista
        Assert.InRange(started.StartFrame, hourFrame - Rate / 100, hourFrame + Rate / 100); // ±10 ms (els blocs de la prova)
        int at = (int)(started.StartFrame - first);
        Assert.Equal(Music * Db(-6) + Signal, output[at * 2], 4);                      // música atenuada (de l'esdeveniment) i senyals
        Assert.Equal(Music, output[(at + Rate + Rate / 2) * 2], 4);                    // acabat: la música, sencera

        Assert.Equal("Música", _queue.Current?.Title); // la cua no se n'ha assabentat
        Assert.Empty(_queue.Upcoming);
        var record = Assert.Single(_events.Executions(Hour.AddHours(-1), Hour.AddHours(1)));
        Assert.Equal(ExecutionResult.Ok, record.Result);
        Assert.NotNull(record.ExecutedAt);
        Assert.Contains("pel reproductor auxiliar 1, amb la música a -6 dB", record.Detail);
        Assert.Contains("ha començat a la mostra prevista", record.Detail);
        Assert.Equal(2, _fired.Count); // en programar-lo i quan ha sonat
    }

    [Fact]
    public void L_atenuacio_de_l_accio_mana_sobre_la_de_l_esdeveniment()
    {
        StartMusic();
        long signals = Track("Senyals horaris", 1, Signal);
        _events.Save(new ScheduledEvent { Name = "Senyals", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0), DuckingDb = -6,
            Actions = [Aux(signals, duckingDb: -12)] });

        var (output, first, _) = Run(Hour + TimeSpan.FromSeconds(1));

        int at = (int)(Assert.Single(_auxStarted).StartFrame - first);
        Assert.Equal(Music * Db(-12) + Signal, output[at * 2], 4);
    }

    [Fact]
    public void Un_ENCUA_amb_senyals_posa_la_pista_a_la_cua_i_els_senyals_sonen_a_l_hora()
    {
        StartMusic();
        long signals = Track("Senyals horaris", 1, Signal);
        long news = Track("Informatiu", 2, 0.5f);
        _events.Save(new ScheduledEvent { Name = "Senyals i informatiu", Mode = EventMode.Queue, StartTime = new TimeOnly(10, 0),
            Actions = [Aux(signals), Pista(news)] });

        Run(Hour + TimeSpan.FromSeconds(1));

        var started = Assert.Single(_auxStarted);
        Assert.Equal(started.TargetFrame, started.StartFrame);
        Assert.Equal("Informatiu", Assert.Single(_queue.Upcoming).Title); // la pista, la següent a sonar
        var record = Assert.Single(_events.Executions(Hour.AddHours(-1), Hour.AddHours(1)));
        Assert.Contains("1 pista a la cua", record.Detail);
        Assert.Contains("ha començat a la mostra prevista", record.Detail);
    }

    [Fact]
    public void En_un_SUPERPOSA_les_pistes_normals_no_sonen_i_una_segona_accio_al_mateix_reproductor_tampoc()
    {
        StartMusic();
        long signals = Track("Senyals horaris", 1, Signal);
        long other = Track("Una altra", 1, 0.2f);
        long news = Track("Informatiu", 2, 0.5f);
        _events.Save(new ScheduledEvent { Name = "Senyals", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0),
            Actions = [Aux(signals), Aux(other, player: 1), Pista(news), Aux(other, player: 2)] });

        Run(Hour + TimeSpan.FromSeconds(1));

        Assert.Equal([1, 2], _auxStarted.Select(s => s.Player).Order());
        Assert.Equal("Senyals horaris", _auxStarted.Single(s => s.Player == 1).Item.Title);
        Assert.Empty(_queue.Upcoming);
        var record = Assert.Single(_events.Executions(Hour.AddHours(-1), Hour.AddHours(1)));
        Assert.Equal(ExecutionResult.Ok, record.Result);
        Assert.Contains("només sona la primera", record.Detail);
        Assert.Contains("Les pistes PISTA no sonen en un esdeveniment SUPERPOSA", record.Detail);
    }

    [Fact]
    public void Un_SUPERPOSA_sense_cap_auxiliar_que_es_pugui_fer_sonar_es_un_error_i_la_musica_continua()
    {
        StartMusic();
        _events.Save(new ScheduledEvent { Name = "Senyals", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0),
            Actions = [Aux(9999)] });

        var (output, _, hourFrame) = Run(Hour + TimeSpan.FromSeconds(1));

        Assert.Empty(_auxStarted);
        var record = Assert.Single(_events.Executions(Hour.AddHours(-1), Hour.AddHours(1)));
        Assert.Equal(ExecutionResult.Error, record.Result);
        Assert.Contains("La pista 9999 (AUXILIAR) no existeix", record.Detail);
        Assert.Equal(Music, output[^2], 4); // ni silenci ni atenuació
        Assert.True(hourFrame > 0);
    }

    [Fact]
    public void Si_l_esdeveniment_s_esborra_abans_de_l_hora_la_veu_preparada_s_allibera()
    {
        StartMusic();
        long signals = Track("Senyals horaris", 1, Signal);
        long id = _events.Save(new ScheduledEvent { Name = "Senyals", Mode = EventMode.Overlay, StartTime = new TimeOnly(10, 0),
            Actions = [Aux(signals)] });

        Run(Hour - TimeSpan.FromSeconds(4)); // ja preparat (30 s abans)
        Assert.Equal(1, _mixer.Voices.Count(v => v.Overlay));

        _events.Delete(id);
        _scheduler.Reload();
        Run(Hour + TimeSpan.FromSeconds(1));

        Assert.Empty(_auxStarted);
        Assert.Equal(0, _mixer.Voices.Count(v => v.Overlay && v.State != VoiceState.Idle));
    }
}
