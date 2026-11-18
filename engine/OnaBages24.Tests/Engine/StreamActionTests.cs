using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>L'acció STREAM del planificador (fase 2, pas 7c; D-010, D-030, D-178).</summary>
public sealed class StreamActionTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly long _stream, _reserve;

    public StreamActionTests()
    {
        _db = Database.Open(_temp.File("stream.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _stream = _library.AddTrack(new Track { Path = "http://127.0.0.1:9/directe", Type = TrackType.Stream, Artist = "LA XARXA", Title = "Directe" });
        string wav = _temp.File("reserva.wav");
        using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            writer.WriteSamples(new float[48000 * 2], 0, 48000 * 2);
        _reserve = _library.AddTrack(new Track { Path = wav, Artist = "Ona Bages", Title = "Butlletí de reserva", DurationMs = 1000, EndMs = 1000, CueSource = CueSource.Analyzer });
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private ScheduledEvent Event(string paramsJson, EventMode mode = EventMode.Cut)
    {
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Butlletí", Mode = mode, StartTime = new TimeOnly(13, 0),
            Actions = [new EventAction { Type = EventActionType.Stream, Params = paramsJson }],
        });
        return _events.Get(id)!;
    }

    private PreparedEvent Prepare(ScheduledEvent ev)
    {
        var executor = new EventExecutor(_library, _queue, null);
        var prepared = executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 18), new TimeOnly(13, 0), Utc));
        executor.Discard(prepared); // allibera la reserva (el TALLA obre el primer 30 s abans)
        return prepared;
    }

    [Fact]
    public void Prepara_el_stream_amb_les_opcions_de_l_accio()
    {
        var ev = Event($$"""{"songId":{{_stream}},"durada_seg":300,"fade_ms":3000,"silenci_seg":8,"directe":false,"retard_seg":4,"reserva_songId":{{_reserve}}}""");
        var prepared = Prepare(ev);

        var item = Assert.Single(prepared.Items);
        Assert.Empty(prepared.Warnings);
        Assert.True(item.IsStream);
        Assert.Equal("LA XARXA - Directe", item.ToString());
        Assert.Equal((0L, 300_000L, 297_000L), (item.StartMs ?? 0, item.EndMs, item.MixMs));
        Assert.Equal((8, false, 4), (item.StreamSilenceSeconds, item.StreamLiveEdge, item.StreamDelaySeconds));
        Assert.Equal(ev.Id, item.EventId);
        Assert.Equal("Butlletí de reserva", item.StreamFallback!.Title);
        Assert.Equal(ev.Id, item.StreamFallback.EventId);
    }

    [Fact]
    public void Per_defecte_comença_pel_directe_amb_un_fos_de_2_s_i_sense_silenci_ni_retard()
    {
        var item = Assert.Single(Prepare(Event($$"""{"songId":{{_stream}},"durada_seg":60}""")).Items);
        Assert.Equal((60_000L, 58_000L), (item.EndMs, item.MixMs));
        Assert.Equal((true, (int?)null, (int?)null, (QueueItem?)null), (item.StreamLiveEdge, item.StreamSilenceSeconds, item.StreamDelaySeconds, item.StreamFallback));
        Assert.Equal((false, false), (item.StreamReconnect, item.StreamReturnAfterSong));
    }

    [Theory]
    [InlineData(""","reconnecta":true""", true, false)]
    [InlineData(""","reconnecta":true,"tornada":"seguida" """, true, false)]
    [InlineData(""","reconnecta":true,"tornada":"canco" """, true, true)]
    public void La_reconnexio_i_la_tornada_passen_a_la_cua(string options, bool reconnect, bool afterSong)
    {
        var item = Assert.Single(Prepare(Event($$"""{"songId":{{_stream}},"durada_seg":3600{{options}}}""")).Items);
        Assert.Equal((reconnect, afterSong), (item.StreamReconnect, item.StreamReturnAfterSong));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(""","titol":true""", true)]
    public void El_titol_per_a_BUTT_passa_a_la_cua(string options, bool title)
    {
        var item = Assert.Single(Prepare(Event($$"""{"songId":{{_stream}},"durada_seg":3600{{options}}}""")).Items);
        Assert.Equal(title, item.StreamTitleForButt);
    }

    [Fact]
    public void Els_esdeveniments_d_abans_amb_l_adreça_escrita_també_sonen()
    {
        var item = Assert.Single(Prepare(Event("""{"url":"http://centauri.shoutca.st:8259","durada_seg":5400}""")).Items);
        Assert.Equal("http://centauri.shoutca.st:8259", item.Path);
        Assert.True(item.IsStream);
        Assert.Equal(5_400_000L, item.EndMs);
    }

    [Theory]
    [InlineData("""{"durada_seg":60}""", "no diu quin stream")]
    [InlineData("""{"songId":99999,"durada_seg":60}""", "no existeix")]
    [InlineData("""{"url":"http://x"}""", "quant ha de durar")]
    public void Si_no_pot_sonar_ho_diu(string paramsJson, string expected)
    {
        var prepared = Prepare(Event(paramsJson, EventMode.Queue));
        Assert.Empty(prepared.Items);
        Assert.Contains(expected, Assert.Single(prepared.Warnings));
    }

    [Fact]
    public void Una_reserva_que_no_existeix_s_avisa_pero_el_stream_sona()
    {
        var prepared = Prepare(Event($$"""{"songId":{{_stream}},"durada_seg":60,"reserva_songId":99999}"""));
        Assert.Null(Assert.Single(prepared.Items).StreamFallback);
        Assert.Contains("La pista de reserva del stream (99999) no existeix", Assert.Single(prepared.Warnings));
    }

    [Fact]
    public void A_la_cua_va_al_davant_com_les_pistes_i_compta_com_a_feta()
    {
        var executor = new EventExecutor(_library, _queue, null);
        var ev = Event($$"""{"songId":{{_stream}},"durada_seg":60}""", EventMode.Queue);
        var outcome = executor.Execute(executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 18), new TimeOnly(13, 0), Utc)));
        Assert.Equal(ExecutionResult.Ok, outcome.Result);
        Assert.Contains("1 pista a la cua", outcome.Detail);
        Assert.True(Assert.Single(_queue.Upcoming).IsStream);
    }

    [Fact]
    public void El_TALLA_amb_retard_de_connexio_ho_anota()
    {
        var executor = new EventExecutor(_library, _queue, null);
        var ev = Event($$"""{"songId":{{_stream}},"durada_seg":60,"retard_seg":3}""");
        var outcome = executor.Execute(executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 18), new TimeOnly(13, 0), Utc)));
        Assert.Contains("retard de connexió del stream: 3 s", outcome.Detail);
    }

    [Fact]
    public void La_descripcio_de_l_accio_diu_les_opcions()
    {
        var describer = new ActionDescriber(_library, null);
        var info = describer.Describe(new EventAction
        {
            Type = EventActionType.Stream,
            Params = $$"""{"songId":{{_stream}},"durada_seg":5400,"silenci_seg":8,"retard_seg":3,"reserva_songId":{{_reserve}}}""",
        });
        Assert.Equal("LA XARXA - Directe", info.Label);
        Assert.Equal("Stream · 1 h 30 min · http://127.0.0.1:9/directe · comença pel directe · s'acaba si arriba en silenci 8 s · " +
                     "retard de connexió 3 s · reserva «Butlletí de reserva»", info.Detail);

        var reconnect = describer.Describe(new EventAction
        {
            Type = EventActionType.Stream,
            Params = $$"""{"songId":{{_stream}},"durada_seg":3600,"reconnecta":true,"tornada":"canco"}""",
        });
        Assert.EndsWith("es reconnecta si cau (torna en acabar la cançó)", reconnect.Detail);
    }

    [Theory]
    [InlineData("\"retard_seg\":31", "retard de connexió")]
    [InlineData("\"silenci_seg\":0", "silenci")]
    [InlineData("\"fade_ms\":20000", "fos final")]
    [InlineData("\"tornada\":\"despres\"", "tornada")]
    [InlineData("\"tornada\":3", "tornada")]
    public void En_desar_es_comproven_els_limits(string option, string expected)
    {
        var router = new CommandRouter(new EngineState(), () => { }, _library, () => [], _queue, events: _events);
        var reply = router.Handle($$$"""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": { "name": "Butlletí", "mode": "TALLA", "startTime": "13:00",
              "actions": [{ "type": "STREAM", "params": { "songId": {{{_stream}}}, "durada_seg": 60, {{{option}}} } }] } }
            """);
        Assert.Contains(expected, reply["error"]!["message"]!.GetValue<string>());
    }
}
