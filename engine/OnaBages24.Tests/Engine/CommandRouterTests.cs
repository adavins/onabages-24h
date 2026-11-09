using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Protocol: format dels missatges i primeres ordres (fase 1, pas 6; docs/protocol.md).</summary>
public class CommandRouterTests
{
    private static readonly AudioDevice[] Devices =
    [
        new("{sortida-1}", "Altaveus (Sound BlasterX AE-5 Plus)", DeviceKind.Output, DeviceStatus.Active, new DeviceFormat(48000, 24, 2)),
        new("{sortida-2}", "HDMI", DeviceKind.Output, DeviceStatus.Unplugged, null),
        new("{entrada-1}", "Entrada de línia", DeviceKind.Input, DeviceStatus.Active, new DeviceFormat(48000, 16, 2)),
    ];

    private bool _shutdownRequested;

    private CommandRouter Router(Library? library = null) =>
        new(new EngineState(), () => _shutdownRequested = true, library, () => Devices);

    private static JsonObject Send(CommandRouter router, string json) => router.Handle(json);

    private static string? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<string>();

    [Fact]
    public void Hello_diu_el_nom_la_versio_i_el_protocol()
    {
        var response = Send(Router(), """{ "v": 1, "id": 7, "cmd": "engine.hello" }""");

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal(7, response["id"]!.GetValue<int>());
        Assert.Equal(1, response["v"]!.GetValue<int>());
        Assert.Equal("Ona Bages 24h — motor", response["result"]!["name"]!.GetValue<string>());
        Assert.Equal(1, response["result"]!["protocol"]!.GetValue<int>());
    }

    [Fact]
    public void Ping_respon()
    {
        var response = Send(Router(), """{ "v": 1, "id": "a", "cmd": "engine.ping" }""");
        Assert.True(response["result"]!["pong"]!.GetValue<bool>());
        Assert.Equal("a", response["id"]!.GetValue<string>()); // l'id es retorna tal com ha arribat
    }

    [Theory]
    [InlineData("això no és JSON", "bad_json")]
    [InlineData("[1, 2]", "bad_json")]
    [InlineData("""{ "v": 1, "id": 1, "id": 2, "cmd": "engine.ping" }""", "bad_json")]
    [InlineData("""{ "id": 1, "cmd": "engine.ping" }""", "protocol_version")]
    [InlineData("""{ "v": 2, "id": 1, "cmd": "engine.ping" }""", "protocol_version")]
    [InlineData("""{ "v": 1, "id": 1 }""", "missing_cmd")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "no.existeix" }""", "unknown_command")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "devices.list", "kind": "altaveus" }""", "bad_params")]
    public void Els_missatges_incorrectes_donen_el_seu_codi_d_error(string json, string code)
    {
        var response = Send(Router(), json);

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal(code, ErrorCode(response));
        Assert.False(string.IsNullOrWhiteSpace(response["error"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public void La_llista_de_dispositius_es_pot_filtrar()
    {
        var router = Router();

        var actives = Send(router, """{ "v": 1, "id": 1, "cmd": "devices.list" }""")["result"]!["devices"]!.AsArray();
        Assert.Equal(["{sortida-1}", "{entrada-1}"], actives.Select(d => d!["id"]!.GetValue<string>()));

        var outputs = Send(router, """{ "v": 1, "id": 2, "cmd": "devices.list", "kind": "output", "includeInactive": true }""")["result"]!["devices"]!.AsArray();
        Assert.Equal(["{sortida-1}", "{sortida-2}"], outputs.Select(d => d!["id"]!.GetValue<string>()));
        Assert.Equal("unplugged", outputs[1]!["status"]!.GetValue<string>());
        Assert.Equal(48000, outputs[0]!["format"]!["sampleRate"]!.GetValue<int>());
    }

    [Fact]
    public void L_estat_inclou_el_motor_i_la_biblioteca()
    {
        using var temp = new TempFolder();
        using var db = Database.Open(temp.File("estat.db"));
        var library = new Library(db);
        library.AddTrack(new Track { Path = @"D:\a.mp3" });
        library.AddTrack(new Track { Path = @"D:\b.mp3" });

        var result = Send(Router(library), """{ "v": 1, "id": 1, "cmd": "state.get" }""")["result"]!;

        Assert.Equal(2, result["library"]!["tracks"]!.GetValue<long>());
        Assert.Equal(0, result["engine"]!["clients"]!.GetValue<int>());
        Assert.True(result["engine"]!["uptime"]!.GetValue<double>() >= 0);
    }

    [Fact]
    public void Aturar_el_motor_respon_i_demana_aturar()
    {
        var response = Send(Router(), """{ "v": 1, "id": 1, "cmd": "engine.shutdown" }""");

        Assert.True(response["result"]!["stopping"]!.GetValue<bool>());
        Assert.True(_shutdownRequested);
    }
}

/// <summary>Opcions d'arrencada i testimoni.</summary>
public class EngineOptionsTests
{
    [Fact]
    public void Per_defecte_port_47824_i_testimoni_aleatori()
    {
        var a = EngineOptions.Parse([]);
        var b = EngineOptions.Parse([]);

        Assert.Equal(47824, a.Port);
        Assert.Equal(48, a.Token.Length);
        Assert.NotEqual(a.Token, b.Token);
        Assert.Null(a.DataDir);
    }

    [Fact]
    public void Es_poden_canviar_el_port_el_testimoni_i_la_carpeta()
    {
        var options = EngineOptions.Parse(["--port", "50000", "--token", "secret", "--data-dir", @"C:\Proves\OnaBages24"]);

        Assert.Equal(50000, options.Port);
        Assert.Equal("secret", options.Token);
        Assert.Equal(@"C:\Proves\OnaBages24", options.DataDir);
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "llarg")]
    [InlineData("--data-dir", @"relativa\carpeta")]
    public void Els_valors_no_valids_es_rebutgen(string option, string value)
    {
        Assert.Throws<ArgumentException>(() => EngineOptions.Parse([option, value]));
    }

    [Fact]
    public void El_testimoni_s_ha_de_correspondre_exactament()
    {
        Assert.True(EngineOptions.TokenMatches("abc", "abc"));
        Assert.False(EngineOptions.TokenMatches("abd", "abc"));
        Assert.False(EngineOptions.TokenMatches("", "abc"));
        Assert.False(EngineOptions.TokenMatches(null, "abc"));
    }
}

/// <summary>Carpeta de dades de la versió portable (D-044).</summary>
public class PortableDataFolderTests
{
    [Fact]
    public void Troba_la_carpeta_dades_marcada_pujant_des_de_la_del_motor()
    {
        using var temp = new TempFolder();
        string engine = Path.Combine(temp.Path, "OnaBages24", "resources", "engine");
        Directory.CreateDirectory(engine);
        string data = Path.Combine(temp.Path, "OnaBages24", "dades");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, RuntimeFile.PortableMarkerFile), "");

        Assert.Equal(data, RuntimeFile.FindPortableDataFolder(engine));
    }

    [Fact]
    public void Una_carpeta_dades_sense_la_marca_no_compta()
    {
        using var temp = new TempFolder();
        string engine = Path.Combine(temp.Path, "app", "resources", "engine");
        Directory.CreateDirectory(engine);
        Directory.CreateDirectory(Path.Combine(temp.Path, "app", "dades")); // sense LLEGEIX-ME.txt

        Assert.Null(RuntimeFile.FindPortableDataFolder(engine));
    }

    [Fact]
    public void En_desenvolupament_es_la_carpeta_dades_del_repositori()
    {
        // Les proves corren des de engine\OnaBages24.Tests\bin\…, igual que el motor des de engine\OnaBages24.Engine\bin\…
        string? found = RuntimeFile.FindPortableDataFolder(AppContext.BaseDirectory);

        Assert.NotNull(found);
        Assert.True(File.Exists(Path.Combine(found, "..", "engine", "OnaBages24.slnx")));
    }
}
