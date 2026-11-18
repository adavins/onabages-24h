using System.Diagnostics;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Audio;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Entrades de línia a la cua (D-185): sonen com un stream (durada, reserva, reconnexió), amb una entrada falsa.</summary>
[Collection(SoloRealTime.Name)]
public sealed class QueueInputTests : IDisposable
{
    private const int Rate = 48000;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueFailed> _failed = [];
    private readonly List<QueueStreamNotice> _notices = [];

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _temp.Dispose();
    }

    private QueuePlayer NewPlayer(Func<QueueItem, IAudioInput?> open, double prepareSeconds = 20)
    {
        var player = new QueuePlayer(_mixer, new QueueSettings
        {
            StallTimeout = TimeSpan.FromSeconds(1),
            StreamConnectFor = TimeSpan.FromSeconds(1),
            PrepareAhead = TimeSpan.FromSeconds(prepareSeconds),
            StreamRetryEvery = TimeSpan.FromMilliseconds(500),
            StreamStableFor = TimeSpan.FromSeconds(1),
        })
        { InputOpener = open };
        player.Started += e => { lock (_started) _started.Add(e); };
        player.Failed += e => { lock (_failed) _failed.Add(e); };
        player.StreamNotice += e => { lock (_notices) _notices.Add(e); };
        return player;
    }

    /// <summary>Una entrada falsa que envia so constant al ritme del rellotge, mentre està engegada.</summary>
    private FakeInput Feeding(float value)
    {
        var input = new FakeInput(Rate, 2);
        _ = Task.Run(() =>
        {
            int packet = Rate / 100;
            var buffer = Enumerable.Repeat(value, packet * 2).ToArray();
            var watch = Stopwatch.StartNew();
            long sent = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (!input.IsRunning) { Thread.Sleep(5); watch.Restart(); sent = 0; continue; }
                while (sent <= watch.Elapsed.TotalSeconds * Rate)
                {
                    input.Push(buffer);
                    sent += packet;
                }
                Thread.Sleep(2);
            }
        });
        return input;
    }

    private string Wav(double seconds, float value)
    {
        string path = _temp.File($"w{Guid.NewGuid():N}.wav");
        var samples = Enumerable.Repeat(value, (int)(seconds * Rate) * 2).ToArray();
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static QueueItem File(string path, string title) => new() { Path = path, Title = title };

    private static QueueItem Input(long durationMs) =>
        QueueItem.FromInput("{fals}", "Línia 2", QueueOrigin.Event, "Prova").AsStream(durationMs);

    /// <summary>Fa sonar en temps real; retorna el pic de cada tros de 50 ms.</summary>
    private List<float> Run(QueuePlayer player, double seconds, Func<QueueItem>? fill = null)
    {
        const int block = Rate / 100;
        var bytes = new byte[block * 2 * sizeof(float)];
        var floats = new float[block * 2];
        var peaks = new List<float>();
        float peak = 0;
        var watch = Stopwatch.StartNew();
        long frames = (long)(seconds * Rate);
        for (long done = 0; done < frames; done += block)
        {
            if (fill is not null && player.WantsFill) player.AddFill(fill());
            player.Tick();
            _mixer.Read(bytes);
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            foreach (float s in floats) peak = Math.Max(peak, Math.Abs(s));
            if ((done / block) % 5 == 4)
            {
                peaks.Add(peak);
                peak = 0;
            }
            while (watch.Elapsed.TotalSeconds < (double)(done + block) / Rate) Thread.Sleep(1);
        }
        player.Tick();
        return peaks;
    }

    [Fact]
    public void Una_entrada_sona_la_durada_prevista_i_despres_la_seguent()
    {
        var input = Feeding(0.5f);
        var player = NewPlayer(_ => input);
        var item = Input(6000); // amb el fos de 2 s, la següent entra als 4 s
        player.Enqueue(item, File(Wav(3, 0.2f), "Després"));

        var peaks = Run(player, 2);

        Assert.True(item.IsInput);
        Assert.Equal("Línia 2", _started[0].Item.Title);
        Assert.InRange(peaks[^5], 0.45f, 0.55f); // sona l'entrada
        var status = player.StreamStatus;
        Assert.NotNull(status);
        Assert.Equal("live", status.State);
        Assert.True((bool)QueueJson.Queue(player)["current"]!["input"]!);

        peaks.AddRange(Run(player, 4.5));
        Assert.Equal(["Línia 2", "Després"], _started.Select(s => s.Item.Title));
        Assert.InRange(peaks[^5], 0.15f, 0.25f); // ara el fitxer
        Assert.True(input.Disposed); // l'entrada s'ha tancat
        Assert.Empty(_failed);
    }

    [Fact]
    public void Si_l_entrada_no_s_obre_sona_la_reserva()
    {
        var player = NewPlayer(_ => throw new AudioDeviceException("Entrada no disponible: no es troba a Windows."));
        var reserve = File(Wav(2, 0.25f), "Reserva");
        player.Enqueue(Input(5000) with { StreamFallback = reserve }, File(Wav(3, 0.2f), "Després"));

        Run(player, 3);

        Assert.Equal("Reserva", _started[0].Item.Title);
        var failed = Assert.Single(_failed);
        Assert.Contains("no es troba a Windows", failed.Message);
    }

    [Fact]
    public void Amb_reconnexio_si_es_desconnecta_sona_musica_i_l_entrada_torna_quan_funciona()
    {
        var first = Feeding(0.5f);
        var second = Feeding(0.5f);
        var watch = Stopwatch.StartNew();
        int opened = 0;
        // La primera vegada s'obre bé; després, fins als 4 s no hi és; després torna
        var player = NewPlayer(_ =>
        {
            if (Interlocked.Increment(ref opened) == 1) return first;
            if (watch.Elapsed < TimeSpan.FromSeconds(4)) throw new AudioDeviceException("Entrada no disponible: no es troba a Windows.");
            return second;
        }, prepareSeconds: 2);
        string fill = Wav(20, 0.3f);
        player.Enqueue(Input(30_000) with { StreamReconnect = true }, File(Wav(5, 0.2f), "Resta"));

        Run(player, 1.5);
        first.Fail("dispositiu desconnectat");
        var peaks = Run(player, 6, () => File(fill, "Farciment") with { Origin = QueueOrigin.AutoDj });

        Assert.Equal(["Línia 2", "Farciment", "Línia 2"], _started.Select(s => s.Item.Title));
        Assert.Contains(_notices, n => n.Kind == "caigut");
        Assert.Contains(_notices, n => n.Kind == "tornat");
        Assert.InRange(peaks[^5], 0.45f, 0.55f); // torna a sonar l'entrada
        Assert.Contains(player.Upcoming, i => i.Title == "Resta"); // la resta no s'ha avançat
    }
}
