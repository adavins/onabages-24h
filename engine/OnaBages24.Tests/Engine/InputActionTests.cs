using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>L'acció ENTRADA del planificador (D-185): l'entrada de línia a la cua, com un stream.</summary>
public sealed class InputActionTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly CommandRouter _router;

    public InputActionTests()
    {
        _db = Database.Open(_temp.File("entrada.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        var scheduler = new Scheduler(_events, new EventExecutor(_library, _queue, null), NullLogger.Instance, zone: Utc);
        _router = new CommandRouter(new EngineState(), () => { }, _library, () => [], _queue, events: _events, scheduler: scheduler);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private PreparedEvent Prepare(string paramsJson, EventMode mode = EventMode.Queue)
    {
        long id = _events.Save(new ScheduledEvent
        {
            Name = "Programa de fora", Mode = mode, StartTime = new TimeOnly(13, 0),
            Actions = [new EventAction { Type = EventActionType.Input, Params = paramsJson }],
        });
        var executor = new EventExecutor(_library, _queue, null);
        var prepared = executor.Prepare(Recurrences.At(_events.Get(id)!, new DateOnly(2026, 11, 18), new TimeOnly(13, 0), Utc));
        executor.Discard(prepared);
        return prepared;
    }

    [Fact]
    public void L_entrada_va_a_la_cua_com_un_stream_amb_les_opcions()
    {
        var prepared = Prepare("""{"dispositiuId":"{0.0.1.00000000}.{abc}","dispositiu":"Línia 2","durada_seg":3600,"fade_ms":3000,"silenci_seg":20,"reconnecta":true,"tornada":"canco"}""");

        var item = Assert.Single(prepared.Items);
        Assert.True(item.IsInput);
        Assert.True(item.IsStream); // sona com un stream
        Assert.Equal("entrada://Línia 2", item.Path);
        Assert.Equal("Línia 2", item.Title);
        Assert.Equal("{0.0.1.00000000}.{abc}", item.InputDeviceId);
        Assert.Equal((3_600_000L, 3_597_000L), (item.EndMs, item.MixMs));
        Assert.Equal((20, true, true), (item.StreamSilenceSeconds, item.StreamReconnect, item.StreamReturnAfterSong));
        Assert.Equal("L'entrada «Línia 2»", item.SourceName());
        Assert.Empty(prepared.Warnings);
    }

    [Fact]
    public void El_guany_i_l_AGC_passen_a_la_cua()
    {
        var plain = Assert.Single(Prepare("""{"dispositiu":"Línia 2","durada_seg":600}""").Items);
        Assert.Equal(0, plain.InputGainDb);
        Assert.Null(plain.InputAgc);

        Assert.Null(plain.ButtTitle);
        var titled = Assert.Single(Prepare("""{"dispositiu":"Línia 2","durada_seg":600,"titol_butt":" Esport i Punt "}""").Items);
        Assert.Equal("Esport i Punt", titled.ButtTitle);

        var gain = Assert.Single(Prepare("""{"dispositiu":"Línia 2","durada_seg":600,"guany_db":6}""").Items);
        Assert.Equal(6, gain.InputGainDb);

        var agc = Assert.Single(Prepare("""{"dispositiu":"Línia 2","durada_seg":600,"agc":true}""").Items);
        Assert.Equal((QueueItem.DefaultAgcTargetLufs, QueueItem.DefaultAgcMaxBoostDb), agc.InputAgc);
        var custom = Assert.Single(Prepare("""{"dispositiu":"Línia 2","durada_seg":600,"agc":true,"agc_objectiu":-18,"agc_max_db":6}""").Items);
        Assert.Equal((-18.0, 6.0), custom.InputAgc);
    }

    [Fact]
    public void Sense_entrada_o_sense_durada_no_sona_i_ho_diu()
    {
        Assert.Contains("quina entrada", Assert.Single(Prepare("""{"durada_seg":60}""").Warnings));
        Assert.Contains("quant ha de durar", Assert.Single(Prepare("""{"dispositiu":"Línia 2"}""").Warnings));
    }

    [Theory]
    [InlineData("""{ "durada_seg": 60 }""", "quina entrada")]
    [InlineData("""{ "dispositiu": "Línia 2" }""", "durada de l'entrada")]
    [InlineData("""{ "dispositiu": "Línia 2", "durada_seg": 60, "silenci_seg": 0 }""", "silenci")]
    [InlineData("""{ "dispositiu": "Línia 2", "durada_seg": 60, "tornada": "mai" }""", "tornada")]
    [InlineData("""{ "dispositiu": "Línia 2", "durada_seg": 60, "guany_db": 25 }""", "guany")]
    [InlineData("""{ "dispositiu": "Línia 2", "durada_seg": 60, "agc": true, "agc_objectiu": -3 }""", "objectiu de l'AGC")]
    [InlineData("""{ "dispositiu": "Línia 2", "durada_seg": 60, "agc": true, "agc_max_db": 30 }""", "apujar l'AGC")]
    public void En_desar_es_comprova_l_entrada_i_les_opcions(string paramsJson, string expected)
    {
        var response = _router.Handle($$"""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": {
                "name": "Programa de fora", "mode": "TALLA", "startTime": "13:00:00",
                "actions": [ { "type": "ENTRADA", "params": {{paramsJson}} } ] } }
            """);
        Assert.Equal("bad_params", response["error"]?["code"]?.GetValue<string>());
        Assert.Contains(expected, response["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void Es_desa_i_es_descriu()
    {
        var response = _router.Handle("""
            { "v": 1, "id": 1, "cmd": "scheduler.save", "event": {
                "name": "Programa de fora", "mode": "TALLA", "startTime": "13:00:00",
                "actions": [ { "type": "ENTRADA", "params": { "dispositiuId": "{x}", "dispositiu": "Línia 2", "durada_seg": 3600, "silenci_seg": 8, "titol_butt": "Esport i Punt", "guany_db": -4.5, "agc": true, "reconnecta": true } } ] } }
            """);

        Assert.True(response["ok"]!.GetValue<bool>(), response.ToJsonString());
        var action = response["result"]!["event"]!["actions"]![0]!;
        Assert.Equal("ENTRADA", action["type"]!.GetValue<string>());
        Assert.Equal("Entrada «Línia 2»", action["label"]!.GetValue<string>());
        Assert.Equal("Entrada · 1 h 00 min · s'acaba si arriba en silenci 8 s · títol per a BUTT «Esport i Punt» · guany −4,5 dB · AGC a −13 LUFS (com a molt +12 dB) · es reconnecta si cau (torna de seguida)",
            action["detail"]!.GetValue<string>());
    }
}
