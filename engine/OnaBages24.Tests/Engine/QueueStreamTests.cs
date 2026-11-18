using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Streams a la cua (fase 2, pas 7b; D-030, D-175): amb un servidor de ràdio fals local que envia un MP3 al ritme del
/// directe. Les proves van en temps real (el stream arriba com arribaria de debò), sense targeta de so.
/// </summary>
public sealed class QueueStreamTests : IDisposable
{
    private const int Rate = 48000;
    private const int Bps = FakeRadio.BytesPerSecond;
    private readonly TempFolder _temp = new();
    private readonly Mixer _mixer = new(Rate);
    private readonly List<QueueStarted> _started = [];
    private readonly List<QueueFailed> _failed = [];
    private readonly List<QueueStuck> _stuck = [];
    private readonly List<QueueStreamNotice> _notices = [];

    /// <summary>Si no és null, fa d'AutoDJ: quan la cua demana música de farciment (D-031), n'hi posa una d'aquestes.</summary>
    private Func<QueueItem>? _fill;

    public void Dispose() => _temp.Dispose();

    private QueuePlayer NewPlayer(TimeSpan? stall = null)
    {
        var player = new QueuePlayer(_mixer, new QueueSettings
        {
            StallTimeout = stall ?? TimeSpan.FromSeconds(1),
            StreamConnectFor = TimeSpan.FromSeconds(1),
        });
        player.Started += e => { lock (_started) _started.Add(e); };
        player.Failed += e => { lock (_failed) _failed.Add(e); };
        player.Stuck += e => { lock (_stuck) _stuck.Add(e); };
        player.StreamNotice += e => { lock (_notices) _notices.Add(e); };
        return player;
    }

    /// <summary>Un WAV de valor constant (per reconèixer-lo a la sortida).</summary>
    private string Wav(double seconds, float value)
    {
        string path = _temp.File($"w{Guid.NewGuid():N}.wav");
        var samples = Enumerable.Repeat(value, (int)(seconds * Rate) * 2).ToArray();
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2));
        writer.WriteSamples(samples, 0, samples.Length);
        return path;
    }

    private static QueueItem Stream(FakeRadio radio, long durationMs, int? silenceSeconds = null, bool liveEdge = true) =>
        new QueueItem
        {
            Path = $"http://127.0.0.1:{radio.Port}/directe", Title = "Directe", Type = TrackType.Stream,
            StreamSilenceSeconds = silenceSeconds, StreamLiveEdge = liveEdge,
        }.AsStream(durationMs);

    private static QueueItem File(string path) => new() { Path = path, Title = Path.GetFileName(path) };

    /// <summary>
    /// Fa sonar <paramref name="seconds"/> en temps real (el mesclador avança al ritme del rellotge, com amb la targeta).
    /// Retorna el pic de cada tros de 50 ms.
    /// </summary>
    private List<float> Run(QueuePlayer player, double seconds)
    {
        const int block = Rate / 100; // 10 ms
        var bytes = new byte[block * 2 * sizeof(float)];
        var floats = new float[block * 2];
        var peaks = new List<float>();
        float peak = 0;
        var watch = Stopwatch.StartNew();
        long frames = (long)(seconds * Rate);
        for (long done = 0; done < frames; done += block)
        {
            if (_fill is not null && player.WantsFill) player.AddFill(_fill());
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

    /// <summary>Envia la capçalera i l'MP3: els primers <paramref name="burstSeconds"/> de cop i la resta al ritme del directe.</summary>
    private static async Task SendLive(TcpClient client, byte[] mp3, double burstSeconds, double? closeAfterSeconds = null)
    {
        await FakeRadio.Send(client, "ICY 200 OK\r\ncontent-type:audio/mpeg\r\n\r\n");
        var stream = client.GetStream();
        int burst = Math.Min(mp3.Length, (int)(burstSeconds * Bps));
        await stream.WriteAsync(mp3.AsMemory(0, burst));
        int stopAt = closeAfterSeconds is { } close ? Math.Min(mp3.Length, (int)(close * Bps)) : mp3.Length;
        var watch = Stopwatch.StartNew();
        for (int at = burst; at < stopAt; at += Bps / 20)
        {
            // Al ritme del directe: 50 ms d'àudio cada 50 ms
            double due = (at - burst) / (double)Bps;
            while (watch.Elapsed.TotalSeconds < due) await Task.Delay(5);
            await stream.WriteAsync(mp3.AsMemory(at, Math.Min(Bps / 20, stopAt - at)));
        }
        if (closeAfterSeconds is null) await Task.Delay(30_000);
    }

    /// <summary>Quants trossos de 50 ms seguits, a partir de <paramref name="from"/>, tenen el pic a prop de <paramref name="level"/>.</summary>
    private static int Run(List<float> peaks, int from, float level)
    {
        int n = 0;
        while (from + n < peaks.Count && Math.Abs(peaks[from + n] - level) < 0.05f) n++;
        return n;
    }

    private static int FirstAbove(List<float> peaks, float level) => peaks.FindIndex(p => p > level);

    [Fact]
    public void Un_stream_sona_la_durada_prevista_i_despres_la_seguent()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 1));
        var player = NewPlayer();
        player.Enqueue(Stream(radio, 3000), File(Wav(3, 0.2f)));

        var peaks = Run(player, 6);

        Assert.Equal(["Directe", _started.Count > 1 ? _started[1].Item.Title : "?"], _started.Select(s => s.Item.Title).Take(2));
        int start = FirstAbove(peaks, 0.3f);
        Assert.InRange(start, 0, 30); // ha començat en menys d'1,5 s (connexió i marge)
        Assert.InRange(peaks[start + 10], 0.4f, 0.6f); // el to del stream
        // Al cap de 3 s (amb el fos de 2 s al final), la següent: el WAV de 0,2
        Assert.InRange(peaks[start + 3000 / 50 + 10], 0.15f, 0.25f);
        Assert.Empty(_failed);
        Assert.Empty(_stuck);
    }

    [Fact]
    public void Comença_pel_directe_sense_la_rafega_del_servidor()
    {
        // La ràfega: 2 s «vells» (0,1) i 1 s més recent (0,3); després, el directe (0,5)
        var mp3 = Concat(FakeRadio.Mp3(2, 0.1f), FakeRadio.Mp3(1, 0.3f), FakeRadio.Mp3(15, 0.5f));
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 3));

        var live = NewPlayer();
        live.Enqueue(Stream(radio, 10_000));
        var withEdge = Run(live, 2.5);
        live.Skip(TimeSpan.Zero);
        Run(live, 0.3);

        var normal = NewPlayer();
        normal.Enqueue(Stream(radio, 10_000, liveEdge: false));
        var withoutEdge = Run(normal, 4);
        normal.Skip(TimeSpan.Zero);
        Run(normal, 0.3);

        // Amb «Comença pel directe», del tros vell (0,1) en sona com a molt mig segon; sense, gairebé tot
        int liveOld = Run(withEdge, FirstAbove(withEdge, 0.05f), 0.1f);
        int normalOld = Run(withoutEdge, FirstAbove(withoutEdge, 0.05f), 0.1f);
        Assert.InRange(liveOld, 0, 12);
        Assert.InRange(normalOld, 25, 42); // com un reproductor normal: el que envia de cop (menys el que ha trigat a arribar)
    }

    [Fact]
    public void Si_no_connecta_se_salta_i_sona_la_seguent()
    {
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }
        var player = NewPlayer();
        var stream = new QueueItem { Path = $"http://127.0.0.1:{port}/", Title = "Directe", Type = TrackType.Stream }.AsStream(10_000);
        player.Enqueue(stream, File(Wav(3, 0.2f)));

        var peaks = Run(player, 3);

        var failed = Assert.Single(_failed);
        Assert.Equal("Directe", failed.Item.Title);
        Assert.Contains("127.0.0.1", failed.Message); // «ha rebutjat la connexió» o «no respon» (Windows triga uns 2 s a dir que el port és tancat)
        Assert.InRange(peaks[^5], 0.15f, 0.25f); // sona el WAV
    }

    [Fact]
    public void Si_no_connecta_i_te_pista_de_reserva_sona_la_reserva()
    {
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }
        var player = NewPlayer();
        var reserve = File(Wav(8, 0.3f)) with { Title = "Reserva" };
        var stream = new QueueItem { Path = $"http://127.0.0.1:{port}/", Title = "Directe", Type = TrackType.Stream }.AsStream(10_000)
            with { StreamFallback = reserve };
        player.Enqueue(stream, File(Wav(5, 0.2f)));

        var peaks = Run(player, 6); // Windows triga uns 2 s a dir que el port és tancat (més, amb l'ordinador carregat)

        Assert.Equal("Reserva", Assert.Single(_failed).Fallback?.Title);
        Assert.Equal("Reserva", _started[0].Item.Title);
        Assert.InRange(peaks[^5], 0.25f, 0.35f); // sona la reserva (0,3), no la següent (0,2)
    }

    /// <summary>Un port on no escolta ningú (el stream no connecta mai).</summary>
    private static int ClosedPort()
    {
        using var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        return ((IPEndPoint)free.LocalEndpoint).Port;
    }

    [Fact]
    public void A_l_hora_del_tall_si_el_stream_no_connecta_la_reserva_sona_a_l_hora()
    {
        // D-179: abans, la reserva arribava uns segons tard (el motor esperava que fallés el segon intent, a l'hora)
        var player = NewPlayer();
        player.Enqueue(File(Wav(20, 0.2f)));
        Run(player, 0.5);
        var reserve = File(Wav(8, 0.3f)) with { Title = "Reserva" };
        var stream = new QueueItem { Path = $"http://127.0.0.1:{ClosedPort()}/", Title = "Directe", Type = TrackType.Stream }.AsStream(60_000)
            with { StreamFallback = reserve };
        var reservation = player.Reserve(stream); // com el TALLA, uns segons abans
        Run(player, 1);
        long cutFrame = player.CurrentFrame + Rate * 3;
        player.CutAt(cutFrame, TimeSpan.Zero, [stream], false, reservation);

        Run(player, 4);

        var started = Assert.Single(_started, s => s.Item.Title == "Reserva");
        Assert.InRange(started.StartFrame - cutFrame, 0, Rate / 5); // a l'hora (menys de 200 ms després: obrir el fitxer)
        Assert.Equal("Reserva", Assert.Single(_failed).Fallback?.Title);
    }

    [Fact]
    public void Si_el_stream_torna_uns_segons_abans_del_tall_sona_a_l_hora()
    {
        // La connexió anticipada falla (encara no emeten), però a l'hora ja hi és: es va tornant a provar
        var mp3 = FakeRadio.Mp3(30, 0.5f);
        var watch = Stopwatch.StartNew();
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (watch.Elapsed < TimeSpan.FromSeconds(3)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        var player = NewPlayer();
        player.Enqueue(File(Wav(20, 0.2f)));
        Run(player, 0.5);
        var stream = Stream(radio, 60_000);
        var reservation = player.Reserve(stream);
        Run(player, 1);
        long cutFrame = player.CurrentFrame + Rate * 5; // el tall, quan ja fa uns segons que emeten
        player.CutAt(cutFrame, TimeSpan.Zero, [stream], false, reservation);

        Run(player, 6);

        Assert.True(_started.Any(s => s.Item.Title == "Directe"),
            string.Join(" | ", _failed.Select(x => x.Message)) + " / " + string.Join(",", _started.Select(x => x.Item.Title)));
        var started = Assert.Single(_started, s => s.Item.Title == "Directe");
        Assert.InRange(started.StartFrame - cutFrame, 0, Rate / 20); // a la mostra (marge de 50 ms)
        Assert.Empty(_failed);
    }

    [Fact]
    public void Si_es_talla_i_no_torna_passa_a_la_seguent()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 3);
            else await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
        });
        var player = NewPlayer(stall: TimeSpan.FromSeconds(1));
        player.Enqueue(Stream(radio, 20_000), File(Wav(5, 0.2f)));

        var peaks = Run(player, 6);

        var stuck = Assert.Single(_stuck);
        Assert.Equal("Directe", stuck.Item.Title);
        Assert.Contains(_notices, n => n.Kind == "tall");
        Assert.InRange(peaks[^5], 0.15f, 0.25f); // ha passat al WAV
    }

    [Fact]
    public void Si_es_talla_i_torna_a_temps_continua()
    {
        var mp3 = FakeRadio.Mp3(30, 0.5f);
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2.5);
            else await SendLive(client, mp3, 1);
        });
        var player = NewPlayer(stall: TimeSpan.FromSeconds(4));
        player.Enqueue(Stream(radio, 20_000), File(Wav(5, 0.2f)));

        var peaks = Run(player, 7);

        Assert.Empty(_stuck);
        Assert.Single(_started); // no ha passat a la següent
        Assert.Contains(_notices, n => n.Kind == "tall");
        Assert.Contains(_notices, n => n.Kind == "recuperat");
        Assert.InRange(peaks[^5], 0.4f, 0.6f); // torna a sonar el stream
    }

    [Fact]
    public void Si_la_connexio_es_queda_penjada_es_torna_a_connectar_abans_de_saltar()
    {
        // Com quan es talla la xarxa (provat pel desenvolupador, 17-11-2026): la connexió no es tanca, només deixen
        // d'arribar dades. Ha de tornar-s'hi a connectar abans que la cua la doni per encallada.
        var mp3 = FakeRadio.Mp3(30, 0.5f);
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1)
            {
                await SendLive(client, mp3, 1, closeAfterSeconds: 2.5);
                await Task.Delay(20_000); // penjada: oberta però sense enviar res
            }
            else
            {
                await Task.Delay(1500); // tornar-s'hi a connectar costa una estona (a la xarxa de debò, 1,6 s)
                await SendLive(client, mp3, 1);
            }
        });
        // Amb 3 s de pista encallada (i no 5) es veu prou clar: abans, el tall es veia als 5 s sense dades i ja era tard
        var player = new QueuePlayer(_mixer, new QueueSettings { StallTimeout = TimeSpan.FromSeconds(3) });
        player.Stuck += e => { lock (_stuck) _stuck.Add(e); };
        player.Started += e => { lock (_started) _started.Add(e); };
        player.StreamNotice += e => { lock (_notices) _notices.Add(e); };
        player.Enqueue(Stream(radio, 30_000), File(Wav(5, 0.2f)));

        var peaks = Run(player, 11);

        Assert.Empty(_stuck);
        Assert.Single(_started); // no ha passat a la següent
        Assert.Contains(_notices, n => n.Kind == "recuperat");
        Assert.InRange(peaks[^5], 0.4f, 0.6f); // torna a sonar el stream
    }

    // --- Reconnexió durant tota la durada (D-031, D-180) ---

    /// <summary>Com <see cref="NewPlayer"/>, amb els temps de la reconnexió escurçats (preparar 2 s abans, provar cada 0,5 s, 1 s per donar-lo per bo).</summary>
    private QueuePlayer NewHoldPlayer(double prepareSeconds = 2)
    {
        var player = new QueuePlayer(_mixer, new QueueSettings
        {
            StallTimeout = TimeSpan.FromSeconds(1),
            StreamConnectFor = TimeSpan.FromSeconds(1),
            PrepareAhead = TimeSpan.FromSeconds(prepareSeconds),
            StreamRetryEvery = TimeSpan.FromMilliseconds(500),
            StreamStableFor = TimeSpan.FromSeconds(1),
        });
        player.Started += e => { lock (_started) _started.Add(e); };
        player.Failed += e => { lock (_failed) _failed.Add(e); };
        player.Stuck += e => { lock (_stuck) _stuck.Add(e); };
        player.StreamNotice += e => { lock (_notices) _notices.Add(e); };
        return player;
    }

    /// <summary>La música de farciment: un WAV de 0,3 de <paramref name="seconds"/> (el mateix fitxer cada vegada).</summary>
    private void FillWith(double seconds)
    {
        string path = Wav(seconds, 0.3f);
        _fill = () => File(path) with { Title = "Farciment", Origin = QueueOrigin.AutoDj };
    }

    private static QueueItem Reconnecting(FakeRadio radio, long durationMs, bool afterSong = false) =>
        Stream(radio, durationMs) with { StreamReconnect = true, StreamReturnAfterSong = afterSong };

    [Fact]
    public void Amb_reconnexio_si_cau_sona_musica_i_el_stream_torna_quan_funciona()
    {
        // Emet 3 s, després no emet (503) fins als 6 s, i després torna
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 3);
            else if (watch.Elapsed < TimeSpan.FromSeconds(6)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(20);
        var player = NewHoldPlayer();
        player.Enqueue(Reconnecting(radio, 30_000), File(Wav(5, 0.2f)) with { Title = "Resta" });

        var peaks = Run(player, 11);

        Assert.Equal(["Directe", "Farciment", "Directe"], _started.Select(s => s.Item.Title));
        Assert.Contains(_notices, n => n.Kind == "caigut" && n.Message.Contains("Sona música de la rotació"));
        Assert.Contains(_notices, n => n.Kind == "tornat");
        Assert.Contains(peaks, p => Math.Abs(p - 0.3f) < 0.03f); // ha sonat la música
        Assert.InRange(peaks[^5], 0.4f, 0.6f); // i ara torna a sonar el stream
        Assert.Empty(_stuck);
        Assert.Empty(_failed);
        Assert.DoesNotContain(player.Upcoming, i => i.Title == "Farciment"); // la música que no ha sonat ja no hi és
        Assert.Equal("Resta", Assert.Single(player.Upcoming).Title); // la resta no s'ha avançat
    }

    [Fact]
    public void Amb_reconnexio_a_l_hora_prevista_de_final_la_musica_es_talla_i_continua_la_resta()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n"); // ja no torna
        });
        FillWith(30); // més llarga que el que queda: s'ha de tallar
        var player = NewHoldPlayer();
        player.Enqueue(Reconnecting(radio, 20_000), File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 22);

        var stream = _started.First(s => s.Item.Title == "Directe");
        var rest = Assert.Single(_started, s => s.Item.Title == "Resta");
        long expected = stream.StartFrame + (20_000 - QueueItem.StreamFadeMs) * Rate / 1000; // on hauria entrat amb el stream sencer
        Assert.InRange(rest.StartFrame - expected, 0, Rate / 20); // a l'hora (50 ms)
        var summary = Assert.Single(_notices, n => n.Kind == "resum");
        Assert.StartsWith("S'ha acabat el temps previst: 1 tall, ", summary.Message);
        Assert.DoesNotContain(_started, s => s.Item.Title == "Directe" && s != stream);
    }

    [Fact]
    public void Amb_reconnexio_si_no_connecta_a_l_hora_sona_la_reserva_i_despres_musica()
    {
        FillWith(20);
        var player = NewHoldPlayer();
        var reserve = File(Wav(2, 0.25f)) with { Title = "Reserva" };
        var stream = new QueueItem { Path = $"http://127.0.0.1:{ClosedPort()}/", Title = "Directe", Type = TrackType.Stream }.AsStream(30_000)
            with { StreamFallback = reserve, StreamReconnect = true };
        player.Enqueue(stream, File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 8);

        Assert.Equal(["Reserva", "Farciment"], _started.Select(s => s.Item.Title));
        Assert.Contains("Sona la pista de reserva i després música de la rotació", Assert.Single(_notices, n => n.Kind == "caigut").Message);
        Assert.Empty(_failed); // no se salta: s'espera que torni
        Assert.Contains(player.Upcoming, i => i.Title == "Resta");
    }

    [Fact]
    public void Amb_reconnexio_i_tornada_en_acabar_la_canço_el_stream_espera_que_s_acabi()
    {
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 3);
            else if (watch.Elapsed < TimeSpan.FromSeconds(5)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(6);
        var player = NewHoldPlayer();
        player.Enqueue(Reconnecting(radio, 30_000, afterSong: true), File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 13);

        var fill = Assert.Single(_started, s => s.Item.Title == "Farciment");
        var back = _started.Last();
        Assert.Equal("Directe", back.Item.Title);
        Assert.InRange(back.StartFrame - fill.StartFrame, 6 * Rate - Rate / 50, 6 * Rate + Rate / 20); // en acabar la cançó
    }

    [Fact]
    public void El_vigilant_de_pistes_de_durada_variable_no_toca_els_streams_de_la_cua()
    {
        // La durada d'un stream és la de l'esdeveniment; la pista de la biblioteca no en té (D-180)
        var player = NewPlayer();
        var stream = new QueueItem { Path = "http://127.0.0.1:1/", Title = "Directe", Type = TrackType.Stream, TrackId = 7 }.AsStream(600_000);
        player.Enqueue(stream);

        int updated = player.RefreshTrack(new Track { Id = 7, Path = "http://127.0.0.1:1/", Type = TrackType.Stream });

        Assert.Equal(0, updated);
        Assert.Equal(600_000, player.Upcoming[0].EndMs);
    }

    [Fact]
    public void Amb_tornada_en_acabar_la_canço_el_stream_que_ja_funciona_de_fa_estona_torna_a_la_mescla()
    {
        // Prova del desenvolupador (18-11-2026, 14:39): només el stream a la cua, sona la reserva i, al darrere, música de
        // l'AutoDJ; el stream ja arribava bé molt abans de la mescla de la reserva, però no hi va tornar: el vigilant de
        // pistes de durada variable (cada 5 s) el veia diferent de la biblioteca i el tornava a preparar
        var mp3 = FakeRadio.Mp3(60, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        var lastConnection = TimeSpan.Zero;
        using var radio = new FakeRadio(async (client, _) =>
        {
            lastConnection = watch.Elapsed;
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else if (watch.Elapsed < TimeSpan.FromSeconds(4)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(30);
        var player = NewHoldPlayer(prepareSeconds: 6);
        var reserve = File(Wav(14, 0.25f)) with { Title = "Reserva" };
        player.Enqueue(Reconnecting(radio, 60_000, afterSong: true) with { StreamFallback = reserve, TrackId = 7 });
        var libraryStream = new Track { Id = 7, Path = $"http://127.0.0.1:{radio.Port}/directe", Type = TrackType.Stream };
        using var checker = new Timer(_ => player.RefreshTrack(libraryStream), null, 1000, 300); // com TrackChangeChecker (quan ja sona, com després d'un TALLA)

        Run(player, 20);

        Assert.Equal(new[] { "Directe", "Reserva", "Directe" }, _started.Select(s => s.Item.Title));
        Assert.Contains(_notices, n => n.Kind == "tornat");
        // Quan ja arribava bé (cap als 5 s), ja no s'hi ha tornat a connectar: la tornada no s'ha desfet
        Assert.InRange(lastConnection.TotalSeconds, 0, 8);
    }

    [Fact]
    public void Amb_tornada_en_acabar_la_canço_si_l_operador_mou_la_canço_el_stream_torna_a_la_mescla_nova()
    {
        // Prova del desenvolupador (18-11-2026): va moure la cançó de farciment a prop del final i el stream hi va entrar,
        // però no es va anotar «Ha tornat»
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else if (watch.Elapsed < TimeSpan.FromSeconds(4)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(30);
        var player = NewHoldPlayer();
        var reserve = File(Wav(30, 0.25f)) with { Title = "Reserva" }; // com a la prova: la que sonava era la reserva
        player.Enqueue(Reconnecting(radio, 40_000, afterSong: true) with { StreamFallback = reserve }, File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 8);
        var fill = player.Current!;
        Assert.Equal("Reserva", fill.Title);
        Assert.Null(player.Seek(fill.Id, TimeSpan.FromSeconds(26))); // queden 4 s de cançó
        Run(player, 6);

        Assert.Equal("Directe", _started.Last().Item.Title);
        Assert.Contains(_notices, n => n.Kind == "tornat");
    }

    [Fact]
    public void Si_es_mou_la_canço_quan_el_stream_ja_espera_la_mescla_continua_tornant()
    {
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else if (watch.Elapsed < TimeSpan.FromSeconds(4)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        // Prova del desenvolupador (18-11-2026): el stream ja esperava la mescla (es prepara 20 s abans) i es va moure la
        // cançó; el stream va anar a la cua com una pista qualsevol i la reconnexió es va quedar penjada
        FillWith(30);
        var player = NewHoldPlayer(prepareSeconds: 6);
        player.Enqueue(Reconnecting(radio, 60_000, afterSong: true), File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 8);
        var fill = player.Current!;
        Assert.Equal("Farciment", fill.Title);
        Assert.Null(player.Seek(fill.Id, TimeSpan.FromSeconds(22))); // queden 8 s: d'aquí a 2 s, el stream ja és la següent
        Run(player, 3.5);
        Assert.Equal("Directe", player.Upcoming[0].Title); // espera la mescla
        Assert.Null(player.Seek(fill.Id, TimeSpan.FromSeconds(18))); // es mou la cançó: la mescla és més tard
        Run(player, 14);

        Assert.Equal("Directe", _started.Last().Item.Title);
        Assert.Contains(_notices, n => n.Kind == "tornat");
        Assert.DoesNotContain(player.Upcoming, i => i.Title == "Directe");
    }

    [Fact]
    public void Si_l_operador_fa_sonar_una_altra_pista_mentre_sona_el_stream_ja_no_s_espera()
    {
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else if (watch.Elapsed < TimeSpan.FromSeconds(4)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(30);
        var player = NewHoldPlayer();
        var rest = File(Wav(5, 0.2f)) with { Title = "Resta" };
        player.Enqueue(Reconnecting(radio, 40_000), rest);

        Run(player, 8); // ha caigut i ha tornat
        Assert.Contains(_notices, n => n.Kind == "tornat");
        Assert.NotNull(player.PlayNow(rest.Id, TimeSpan.FromMilliseconds(500)));
        Run(player, 1);

        Assert.Equal("Resta", _started.Last().Item.Title);
        Assert.StartsWith("Ja no s'espera que torni", Assert.Single(_notices, n => n.Kind == "resum").Message);
        Assert.False(player.WantsFill);
    }

    [Fact]
    public void Sense_reconnexio_si_cau_passa_a_la_seguent_com_sempre()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 2);
            else await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
        });
        FillWith(20);
        var player = NewHoldPlayer();
        player.Enqueue(Stream(radio, 30_000), File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 5);

        Assert.Equal(["Directe", "Resta"], _started.Select(s => s.Item.Title));
        Assert.Single(_stuck);
        Assert.DoesNotContain(_notices, n => n.Kind == "caigut");
    }

    [Fact]
    public void Si_arriba_en_silenci_s_acaba_i_passa_a_la_seguent()
    {
        var mp3 = Concat(FakeRadio.Mp3(2, 0.5f), FakeRadio.Mp3(20, 0f));
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 1));
        var player = NewPlayer();
        player.Enqueue(Stream(radio, 20_000, silenceSeconds: 1), File(Wav(5, 0.2f)));

        var peaks = Run(player, 6);

        var notice = Assert.Single(_notices, n => n.Kind == "silenci");
        Assert.Contains("Arribava en silenci des de fa 1 s", notice.Message);
        Assert.InRange(peaks[^5], 0.15f, 0.25f); // ha passat al WAV abans de la durada
    }

    [Fact]
    public void Un_stream_no_es_pot_posar_en_pausa_ni_moure()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 1));
        var player = NewPlayer();
        var stream = Stream(radio, 20_000);
        player.Enqueue(stream);
        Run(player, 2);

        Assert.Equal("Un stream en directe no es pot posar en pausa.", player.PauseCurrent());
        Assert.Equal("Un stream en directe no es pot moure.", player.Seek(stream.Id, TimeSpan.FromSeconds(10)));
        player.Skip(TimeSpan.Zero);
        Run(player, 0.3);
    }

    [Fact]
    public void Una_pista_de_tipus_stream_de_la_biblioteca_dura_una_hora_si_no_en_te_durada()
    {
        var track = new Track { Id = 5, Path = "http://servidor:8000/directe", Type = TrackType.Stream, Title = "Directe" };
        var item = QueueItem.FromTrack(track, QueueOrigin.Manual);
        Assert.True(item.IsStream);
        Assert.Equal((0L, QueueItem.DefaultStreamMs, QueueItem.DefaultStreamMs - QueueItem.StreamFadeMs), (item.StartMs, item.EndMs, item.MixMs));
        Assert.Equal(90_000L, QueueItem.FromTrack(track with { DurationMs = 90_000 }, QueueOrigin.Manual).EndMs);
        Assert.False(QueueItem.FromTrack(track with { Path = @"C:\musica\a.mp3", Type = TrackType.Music }, QueueOrigin.Manual).IsStream);
    }

    // --- Estat dels streams per a la interfície (fase 2, pas 7e) ---

    [Fact]
    public void Mentre_sona_un_stream_la_cua_diu_que_es_en_directe_i_quant_en_falta()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 1));
        var player = NewPlayer();
        player.Enqueue(Stream(radio, 20_000), File(Wav(3, 0.2f)));

        Run(player, 3);

        var status = player.StreamStatus;
        Assert.NotNull(status);
        Assert.Equal("Directe", status.Item.Title);
        Assert.Equal("live", status.State);
        Assert.False(status.Reconnects);
        Assert.InRange(status.Elapsed.TotalSeconds, 1.5, 3.1);
        // Fins a l'hora prevista de final (on comença la següent): la durada menys el fos
        Assert.InRange((status.Elapsed + status.Remaining).TotalMilliseconds, 20_000 - QueueItem.StreamFadeMs - 50, 20_000 - QueueItem.StreamFadeMs + 50);
        var json = QueueJson.Queue(player)["stream"]!;
        Assert.Equal("live", (string?)json["state"]);
        Assert.Equal(status.Item.Id.ToString(), (string?)json["itemId"]);
    }

    [Fact]
    public void Amb_reconnexio_la_cua_diu_quan_ha_caigut_com_va_la_prova_i_quan_torna()
    {
        // Emet 3 s, després no emet (503) fins als 6 s, i després torna
        var mp3 = FakeRadio.Mp3(40, 0.5f);
        var watch = Stopwatch.StartNew();
        int connections = 0;
        using var radio = new FakeRadio(async (client, _) =>
        {
            if (Interlocked.Increment(ref connections) == 1) await SendLive(client, mp3, 1, closeAfterSeconds: 3);
            else if (watch.Elapsed < TimeSpan.FromSeconds(6)) await FakeRadio.Send(client, "HTTP/1.0 503 Service Unavailable\r\n\r\n");
            else await SendLive(client, mp3, 1);
        });
        FillWith(20);
        var player = NewHoldPlayer();
        var states = new List<string>();
        player.Changed += () =>
        {
            if (player.StreamStatus is { } s) lock (states) if (states.Count == 0 || states[^1] != s.State) states.Add(s.State);
        };
        player.Enqueue(Reconnecting(radio, 30_000), File(Wav(5, 0.2f)) with { Title = "Resta" });

        Run(player, 5.5);

        var down = player.StreamStatus;
        Assert.NotNull(down);
        Assert.Equal("down", down.State);
        Assert.Equal("trying", down.Probe); // encara respon 503
        Assert.Equal(1, down.Cuts);
        Assert.True(down.Reconnects);
        Assert.NotNull(down.DownFor);
        Assert.InRange(down.DownFor.Value.TotalSeconds, 0.5, 4);
        Assert.InRange(down.Elapsed.TotalSeconds, 4.5, 5.6); // des de l'inici del stream, no de la música
        long planned = 30_000 - QueueItem.StreamFadeMs;
        Assert.InRange((down.Elapsed + down.Remaining).TotalMilliseconds, planned - 50, planned + 50);
        var json = QueueJson.Queue(player)["stream"]!;
        Assert.Equal("down", (string?)json["state"]);
        Assert.NotNull((string?)json["downSince"]);

        Run(player, 5.5);

        var back = player.StreamStatus;
        Assert.NotNull(back);
        Assert.Equal("live", back.State);
        Assert.Null(back.DownFor);
        Assert.Null(back.Probe);
        // El temps continua comptant des de l'inici del stream, no des que ha tornat
        Assert.InRange(back.Elapsed.TotalSeconds, 10, 11.2);
        Assert.InRange((back.Elapsed + back.Remaining).TotalMilliseconds, planned - 50, planned + 50);
        // La interfície ho ha sabut a cada canvi
        lock (states) Assert.Equal(["live", "down", "returning", "live"], states.Where(s => s != "cut"));
    }

    [Fact]
    public void Un_stream_que_espera_a_la_cua_diu_quan_ja_es_a_punt()
    {
        var mp3 = FakeRadio.Mp3(20, 0.5f);
        using var radio = new FakeRadio((client, _) => SendLive(client, mp3, 1));
        var player = NewPlayer();
        var stream = Stream(radio, 10_000);
        player.Enqueue(File(Wav(6, 0.2f)), stream);

        Run(player, 3);

        Assert.Equal("ready", player.UpcomingStreamStates[stream.Id]);
        var upcoming = QueueJson.Queue(player)["upcoming"]!.AsArray();
        Assert.Equal("ready", (string?)upcoming[0]!["streamState"]);
        Assert.Null(player.StreamStatus); // encara no sona cap stream
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
