using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Streaming;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Emissors per internet: la configuració i la prova de connexió (fase 5, pas 13a; D-202).</summary>
public sealed class StreamingSettingsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly CommandRouter _router;
    private readonly List<(StreamServer Server, StreamFormat Format, StreamInfo Info)> _tested = [];
    private int _changes;

    public StreamingSettingsTests()
    {
        _db = Database.Open(_temp.File("ob24.db"));
        _library = new Library(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library);
        _router.TestStreamServer = (server, format, info) =>
        {
            _tested.Add((server, format, info));
            return server.Password == "bona" ? null : "El servidor no accepta la contrasenya.";
        };
        _router.StreamOutputsChanged += () => _changes++;
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);
    private JsonNode Result(string json) => Send(json)["result"] ?? throw new Xunit.Sdk.XunitException(Send(json).ToJsonString());
    private string Error(string json) => Send(json)["error"]!["message"]!.GetValue<string>();

    private const string Complete = """
        "name": "Ona Bages", "serverType": "icecast", "host": "http://radio.exemple.cat/", "port": 8000,
        "mount": "onabages.mp3", "password": "bona", "inputDeviceId": "{in-1}", "inputDeviceName": "Línia (Sound Blaster Z)"
        """;

    [Fact]
    public void Un_emissor_es_desa_amb_la_contrasenya_xifrada_i_no_surt_mai_del_motor()
    {
        var output = Result($$"""{ "v": 1, "id": 1, "cmd": "streaming.save", {{Complete}} }""")["output"]!;

        Assert.Equal("e1", output["id"]!.GetValue<string>());
        Assert.Equal("radio.exemple.cat", output["host"]!.GetValue<string>()); // sense «http://» ni la barra
        Assert.Equal("/onabages.mp3", output["mount"]!.GetValue<string>());       // amb la barra
        Assert.Equal(192, output["bitrateKbps"]!.GetValue<int>());                // el format de l'emissora per defecte
        Assert.Equal(48000, output["sampleRate"]!.GetValue<int>());
        Assert.True(output["hasPassword"]!.GetValue<bool>());
        Assert.Null(output["password"]);
        Assert.Equal(1, _changes);
        Assert.DoesNotContain("bona", _library.GetSetting(StreamOutputStore.SettingKey)!); // xifrada
        Assert.Equal("bona", StreamOutputStore.Read(_library).Single().Password);
        Assert.DoesNotContain("bona", Result("""{ "v": 1, "id": 2, "cmd": "streaming.list" }""").ToJsonString());
    }

    [Fact]
    public void L_adreca_amb_port_es_rebutja_i_diu_com_s_ha_d_escriure()
    {
        // «radio.exemple.cat:8000» queda amb el port enganxat: el port va a part
        string error = Error($$"""{ "v": 1, "id": 1, "cmd": "streaming.save", {{Complete.Replace("exemple.cat/", "exemple.cat:8000/")}} }""");
        Assert.Contains("ha de ser només el nom", error);
    }

    [Fact]
    public void Es_canvien_nomes_els_camps_que_s_envien_i_la_contrasenya_es_conserva()
    {
        string complete = Complete;
        Result($$"""{ "v": 1, "id": 1, "cmd": "streaming.save", {{complete}} }""");
        string other = complete.Replace("Ona Bages\"", "Ona Bages Directe\"");
        var second = Result($$"""{ "v": 1, "id": 2, "cmd": "streaming.save", {{other}}, "continuous": false }""")["output"]!;
        Assert.Equal("e2", second["id"]!.GetValue<string>());

        var changed = Result("""{ "v": 1, "id": 3, "cmd": "streaming.save", "outputId": "e1", "bitrateKbps": 128, "titleMode": "fixed", "fixedTitle": "Ona  Bages\n en directe" }""")["output"]!;
        Assert.Equal(128, changed["bitrateKbps"]!.GetValue<int>());
        Assert.Equal("Ona Bages en directe", changed["fixedTitle"]!.GetValue<string>()); // en una línia
        Assert.Equal("radio.exemple.cat", changed["host"]!.GetValue<string>());
        Assert.True(changed["hasPassword"]!.GetValue<bool>());

        var list = Result("""{ "v": 1, "id": 4, "cmd": "streaming.list" }""")["outputs"]!.AsArray();
        Assert.Equal(["Ona Bages", "Ona Bages Directe"], list.Select(o => o!["name"]!.GetValue<string>()));
        Assert.False(list[1]!["continuous"]!.GetValue<bool>());
        Assert.Null(list[0]!["problem"]);

        // El nom no es pot repetir; un emissor que no existeix
        Assert.Contains("Ja hi ha un emissor", Error("""{ "v": 1, "id": 5, "cmd": "streaming.save", "outputId": "e2", "name": "ona bages" }"""));
        Assert.Contains("No existeix", Error("""{ "v": 1, "id": 6, "cmd": "streaming.save", "outputId": "e9", "port": 80 }"""));

        // S'esborra
        Assert.Equal("e1", Result("""{ "v": 1, "id": 7, "cmd": "streaming.delete", "outputId": "e1" }""")["deleted"]!.GetValue<string>());
        Assert.Equal("e2", StreamOutputStore.Read(_library).Single().Output.Id);
        Assert.Equal(4, _changes);
    }

    [Fact]
    public void Un_emissor_actiu_ha_d_estar_complet_i_un_d_inactiu_es_pot_desar_a_mitges()
    {
        Assert.Equal("Falta la contrasenya del servidor.",
            Error("""{ "v": 1, "id": 1, "cmd": "streaming.save", "name": "A", "host": "radio.exemple.cat", "inputDeviceName": "Línia" }"""));
        Assert.Equal("Falta triar l'entrada que s'emet.",
            Error("""{ "v": 1, "id": 2, "cmd": "streaming.save", "name": "A", "host": "radio.exemple.cat", "password": "x" }"""));
        Assert.Contains("punt de muntatge", Error("""{ "v": 1, "id": 3, "cmd": "streaming.save", "name": "A", "host": "radio.exemple.cat", "mount": "/", "password": "x", "inputDeviceName": "Línia" }"""));
        Assert.Contains("128", Error("""{ "v": 1, "id": 4, "cmd": "streaming.save", "name": "A", "host": "radio.exemple.cat", "bitrateKbps": 150, "password": "x", "inputDeviceName": "Línia" }"""));
        Assert.Equal("Falta el títol fix.",
            Error("""{ "v": 1, "id": 5, "cmd": "streaming.save", "name": "A", "host": "radio.exemple.cat", "password": "x", "inputDeviceName": "Línia", "titleMode": "fixed" }"""));

        // Inactiu: es pot desar sense contrasenya ni entrada, i la llista diu què li falta quan s'activi
        var saved = Result("""{ "v": 1, "id": 6, "cmd": "streaming.save", "name": "A", "enabled": false }""")["output"]!;
        Assert.False(saved["enabled"]!.GetValue<bool>());
        Assert.Null(saved["problem"]);
        Assert.Equal("Falta l'adreça del servidor.", Error("""{ "v": 1, "id": 7, "cmd": "streaming.save", "outputId": "e1", "enabled": true }"""));
    }

    [Fact]
    public void La_prova_de_connexio_fa_servir_el_formulari_i_si_no_la_contrasenya_desada()
    {
        string complete = Complete;
        Result($$"""{ "v": 1, "id": 1, "cmd": "streaming.save", {{complete.Replace("icecast", "shoutcast")}}, "publicName": "Ona Bages 24h", "streamId": 2 }""");

        var ok = Result("""{ "v": 1, "id": 2, "cmd": "streaming.test", "outputId": "e1", "port": 8010 }""");
        Assert.True(ok["connected"]!.GetValue<bool>());
        var (server, format, info) = Assert.Single(_tested);
        Assert.Equal((StreamServerType.Shoutcast, "radio.exemple.cat", 8010, 2, "bona"), (server.Type, server.Host, server.Port, server.StreamId, server.Password));
        Assert.Equal(new StreamFormat(192, 48000, 2), format);
        Assert.Equal("Ona Bages 24h", info.Name);
        Assert.Equal(8000, StreamOutputStore.Read(_library).Single().Output.Port); // la prova no desa res

        var bad = Result("""{ "v": 1, "id": 3, "cmd": "streaming.test", "outputId": "e1", "password": "dolenta" }""");
        Assert.False(bad["connected"]!.GetValue<bool>());
        Assert.Equal("El servidor no accepta la contrasenya.", bad["error"]!.GetValue<string>());

        // Sense emissor desat: només el formulari
        Assert.Equal("Falta la contrasenya del servidor.", Error("""{ "v": 1, "id": 4, "cmd": "streaming.test", "host": "radio.exemple.cat" }"""));
        Assert.True(CommandRouter.IsSlow("""{ "v": 1, "id": 5, "cmd": "streaming.test" }"""));
    }

    [Fact]
    public void Una_contrasenya_d_un_altre_ordinador_s_ha_de_tornar_a_escriure()
    {
        string complete = Complete;
        Result($$"""{ "v": 1, "id": 1, "cmd": "streaming.save", {{complete}} }""");
        var copy = JsonNode.Parse(_library.GetSetting(StreamOutputStore.SettingKey)!)!.AsArray();
        copy[0]!["contrasenya"] = "AAAA";
        _library.SetSetting(StreamOutputStore.SettingKey, copy.ToJsonString());

        var listed = Result("""{ "v": 1, "id": 2, "cmd": "streaming.list" }""")["outputs"]![0]!;
        Assert.StartsWith("No es pot llegir la contrasenya desada", listed["problem"]!.GetValue<string>());
        Assert.StartsWith("No es pot llegir la contrasenya desada", Error("""{ "v": 1, "id": 3, "cmd": "streaming.save", "outputId": "e1", "port": 8001 }"""));
        Assert.True(Result("""{ "v": 1, "id": 4, "cmd": "streaming.save", "outputId": "e1", "password": "bona" }""")["output"]!["hasPassword"]!.GetValue<bool>());
    }
}
