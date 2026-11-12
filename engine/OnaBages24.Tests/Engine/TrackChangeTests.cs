using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Playback;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Pistes de durada variable (fase 4, pas 1; D-012): si el fitxer d'una pista de la cua canvia, durada i punts nous.</summary>
public sealed class TrackChangeTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public TrackChangeTests()
    {
        _db = Database.Open(_temp.File("variable.db"));
        _library = new Library(_db);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>WAV amb 0,5 s de silenci, <paramref name="seconds"/> de to i 0,5 s de silenci, modificat fa un minut.</summary>
    private static void Wav(string path, double seconds)
    {
        const int rate = 44100;
        var samples = new List<float>();
        void Part(double s, float amplitude)
        {
            for (int i = 0; i < s * rate; i++)
            {
                float v = amplitude * (float)Math.Sin(2 * Math.PI * 440 * i / rate);
                samples.Add(v);
                samples.Add(v);
            }
        }
        Part(0.5, 0);
        Part(seconds, 0.5f);
        Part(0.5, 0);
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
            writer.WriteSamples(samples.ToArray(), 0, samples.Count);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
    }

    /// <summary>Una pista de 3 s de to (4 s el fitxer), a la biblioteca amb la mida i la data d'ara, i a la cua.</summary>
    private (long Id, string Path, QueueItem Item) Queued(CueSource source = CueSource.Analyzer)
    {
        string path = _temp.File("Programa.wav");
        Wav(path, 3);
        var info = new FileInfo(path);
        long id = _library.AddTrack(new Track
        {
            Path = path, Artist = "Ona Bages", Title = "Programa", DurationMs = 4000, StartMs = 500, MixMs = 3400, EndMs = 3500,
            CueSource = source, VariableDuration = true, FileSize = info.Length,
            FileModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        });
        var item = QueueItem.FromTrack(_library.GetTrack(id)!, QueueOrigin.Event);
        _queue.Enqueue(item);
        return (id, path, item);
    }

    private TrackChangeChecker Checker() => new(_library, _queue, clock: () => _now);

    [Fact]
    public void Un_fitxer_substituit_mes_llarg_dona_la_durada_i_els_punts_nous_a_la_biblioteca_i_a_la_cua()
    {
        var (id, path, _) = Queued();
        Wav(path, 8); // el programa nou: 9 s el fitxer

        using var checker = Checker();
        var updated = new List<long>();
        checker.TrackUpdated += updated.Add;
        Assert.Equal(1, checker.CheckNow());

        var track = _library.GetTrack(id)!;
        Assert.InRange(track.DurationMs!.Value, 8900, 9100);
        Assert.InRange(track.EndMs!.Value, 8300, 8700); // el final del to nou, no el de l'antic (3,5 s)
        Assert.Equal(CueSource.Analyzer, track.CueSource);
        Assert.Equal([id], updated);

        var item = Assert.Single(_queue.Upcoming);
        Assert.Equal(track.EndMs, item.EndMs);
        Assert.Equal(track.DurationMs, item.FileDurationMs);

        Assert.Equal(0, checker.CheckNow()); // ja no hi ha res de nou
    }

    [Fact]
    public void Un_fitxer_que_s_esta_escrivint_no_es_toca_fins_que_acaba()
    {
        var (id, path, item) = Queued();
        Wav(path, 8);
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddSeconds(-1)); // modificat fa 1 s

        using var checker = Checker();
        Assert.Equal(0, checker.CheckNow());
        Assert.Equal(3500, _library.GetTrack(id)!.EndMs);
        Assert.Equal(item.EndMs, Assert.Single(_queue.Upcoming).EndMs); // l'última versió bona

        _now += TimeSpan.FromSeconds(10); // ja fa estona que no canvia
        Assert.Equal(1, checker.CheckNow());
        Assert.NotEqual(3500, _library.GetTrack(id)!.EndMs);
    }

    [Fact]
    public void Un_fitxer_que_no_es_pot_llegir_deixa_l_ultima_versio_bona()
    {
        var (id, path, item) = Queued();
        File.WriteAllText(path, "no és àudio");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var logs = new List<string>();

        using var checker = new TrackChangeChecker(_library, _queue, clock: () => _now, log: logs.Add);
        Assert.Equal(0, checker.CheckNow());
        Assert.Equal(0, checker.CheckNow());

        Assert.Equal(3500, _library.GetTrack(id)!.EndMs);
        Assert.Equal(item.EndMs, Assert.Single(_queue.Upcoming).EndMs);
        Assert.Contains("l'última versió bona", Assert.Single(logs)); // un sol avís, no un a cada volta
    }

    [Fact]
    public void Els_punts_manuals_no_es_toquen_pero_la_durada_si()
    {
        var (id, path, _) = Queued(CueSource.Manual);
        Wav(path, 8);

        using var checker = Checker();
        checker.CheckNow();

        var track = _library.GetTrack(id)!;
        Assert.Equal((CueSource.Manual, 500L, 3500L), (track.CueSource, track.StartMs!.Value, track.EndMs!.Value));
        Assert.InRange(track.DurationMs!.Value, 8900, 9100);
    }

    [Fact]
    public void Els_punts_canviats_a_la_biblioteca_per_una_altra_via_arriben_a_la_cua()
    {
        var (id, _, _) = Queued();
        _library.UpdateTrack(_library.GetTrack(id)! with { MixMs = 3000, CueSource = CueSource.Manual }); // p. ex. l'editor de punts

        using var checker = Checker();
        Assert.Equal(0, checker.CheckNow()); // el fitxer no ha canviat
        Assert.Equal(3000, Assert.Single(_queue.Upcoming).MixMs);
    }
}
