using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Streaming;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Els emissors per internet en marxa (fase 5, pas 13a-2; D-202): amb una entrada falsa, un servidor Icecast fals i un
/// rellotge que mouen les proves (el de tornar a engegar i el de les incidències).
/// </summary>
public sealed class StreamOutputsTests : IDisposable
{
    private readonly StreamerTests.FakeIcecast _server = new();
    private readonly List<StreamOutputStatus[]> _changes = [];
    private readonly List<string> _titles = [];
    private List<SavedStreamOutput> _saved = [];
    private FakeInput _input = new();
    private bool _inputMissing;
    private DateTimeOffset _now = new(2026, 11, 20, 18, 0, 0, TimeSpan.FromHours(1));
    private string? _emissionTitle = "Txarango - Som Riu";
    private int _opened; // vegades que s'ha obert l'entrada
    private readonly StreamOutputs _outputs;

    public StreamOutputsTests()
    {
        _outputs = new StreamOutputs(() => _saved, () => _emissionTitle, null, OpenInput, (_, title) => { lock (_titles) _titles.Add(title); }, () => _now);
        _outputs.Changed += s => _changes.Add([.. s]);
    }

    public void Dispose()
    {
        _outputs.Dispose();
        _server.Dispose();
    }

    private IAudioInput OpenInput(StreamOutput o)
    {
        if (_inputMissing) throw new AudioDeviceException("Entrada no disponible: no es troba «Línia» a Windows.");
        _opened++;
        return _input;
    }

    private StreamOutput Output(string id = "e1", string name = "Ona Bages") => new()
    {
        Id = id, Name = name, Host = "127.0.0.1", Port = _server.Port, Mount = "/onabages.mp3", InputDeviceName = "Línia",
    };

    private static SavedStreamOutput Saved(StreamOutput o, string? password = "x") =>
        new(o, password is null ? null : StreamOutputStore.Protect(password));

    private StreamOutputStatus Status(string id = "e1") => _outputs.Status.Single(s => s.Id == id);

    /// <summary>Crida <see cref="StreamOutputs.Tick"/> (com el motor) fins que es compleixi la condició.</summary>
    private void WaitFor(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            _outputs.Tick();
            if (condition()) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"No ha passat: {what} (estat: {string.Join(", ", _outputs.Status)}).");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Un_emissor_continu_s_engega_sol_i_envia_el_titol_de_l_emissio()
    {
        _saved = [Saved(Output())];
        WaitFor(() => Status().State == StreamOutputState.Streaming, "connectat");

        var headers = string.Join("\n", _server.Headers(0));
        Assert.Contains("PUT /onabages.mp3", headers);
        Assert.Contains("ice-bitrate=192", headers, StringComparison.OrdinalIgnoreCase); // el format de l'emissora per defecte
        Assert.Contains(_changes, c => c.Single().State == StreamOutputState.Streaming);
        Assert.Empty(_outputs.Problems);

        WaitFor(() => { lock (_titles) return _titles.Contains("Txarango - Som Riu"); }, "títol en connectar");
        _emissionTitle = "Oques Grasses - Sucre";
        WaitFor(() => { lock (_titles) return _titles.Contains("Oques Grasses - Sucre"); }, "títol nou");
        Assert.Equal("Oques Grasses - Sucre", Status().Title);
    }

    [Fact]
    public void Si_es_desconnecta_l_entrada_es_torna_a_engegar_sol_i_si_dura_es_una_incidencia()
    {
        _saved = [Saved(Output())];
        WaitFor(() => Status().State == StreamOutputState.Streaming, "connectat");

        _input.Fail("USB desconnectat");
        _inputMissing = true;
        WaitFor(() => Status().State == StreamOutputState.Failed, "aturat per l'entrada");
        Assert.Contains("Font no disponible", Status().Error);
        Assert.Equal(_now, Status().DownSince);

        // Abans de 10 s no es torna a provar; als 10 s sí, i l'entrada encara no hi és
        _now += StreamOutputs.RestartDelay - TimeSpan.FromSeconds(1);
        _outputs.Tick();
        Assert.Contains("Font no disponible", Status().Error);
        _now += TimeSpan.FromSeconds(1);
        WaitFor(() => Status().Error?.StartsWith("Entrada no disponible") == true, "tornat a provar");

        // Abans dels 30 s no és cap incidència; després, sí (amb el motiu d'ara)
        Assert.Empty(_outputs.Problems);
        _now += StreamOutputs.ProblemAfter;
        _outputs.Tick();
        var problem = Assert.Single(_outputs.Problems);
        Assert.Equal("emissor:e1", problem.Key);
        Assert.Equal("L'emissor per internet «Ona Bages» no emet des de les 18:00: Entrada no disponible: no es troba «Línia» a Windows.", problem.Value.Text);
        Assert.Equal(IncidentSeverity.Danger, problem.Value.Severity);

        // L'entrada torna: al cap de 10 s del darrer intent es torna a engegar sol
        _inputMissing = false;
        _input = new FakeInput();
        _now += StreamOutputs.RestartDelay;
        WaitFor(() => Status().State == StreamOutputState.Streaming, "torna a emetre");
        Assert.Empty(_outputs.Problems);
        Assert.Null(Status().DownSince);
        Assert.True(_opened >= 2); // s'ha tornat a obrir l'entrada
    }

    [Fact]
    public void Els_desactivats_els_programables_i_els_incomplets_no_emeten()
    {
        _saved =
        [
            Saved(Output("e1", "Desactivat") with { Enabled = false }),
            Saved(Output("e2", "Directe") with { Continuous = false }),
            Saved(Output("e3", "Sense contrasenya"), password: null),
            new(Output("e4", "D'un altre ordinador"), "AAAA"),
        ];
        _outputs.Tick();

        Assert.Equal([StreamOutputState.Off, StreamOutputState.Off, StreamOutputState.Incomplete, StreamOutputState.Incomplete],
            _outputs.Status.Select(s => s.State));
        Assert.Equal(0, _opened);
        Assert.Equal(2, _outputs.Problems.Count);
        Assert.Equal("L'emissor per internet «Sense contrasenya» no pot emetre: falta la contrasenya del servidor.", _outputs.Problems["emissor:e3"].Text);
        Assert.Contains("no es pot llegir la contrasenya desada", _outputs.Problems["emissor:e4"].Text);
    }

    [Fact]
    public void Si_canvia_la_configuracio_es_refa_i_si_s_esborra_s_atura()
    {
        _saved = [Saved(Output())];
        WaitFor(() => Status().State == StreamOutputState.Streaming, "connectat");

        // Una altra qualitat: s'atura el d'abans (tanca la connexió) i el nou espera uns segons per engegar-se
        _saved = [Saved(Output() with { BitrateKbps = 128 })];
        _outputs.Reload();
        _outputs.Tick();
        _server.WaitForConnection(0);
        Assert.Equal(StreamOutputState.Connecting, Status().State);
        _now += StreamOutputs.ReplaceDelay;
        WaitFor(() => Status().State == StreamOutputState.Streaming, "el nou connectat");
        Assert.Contains("ice-bitrate=128", string.Join("\n", _server.Headers(1)), StringComparison.OrdinalIgnoreCase);

        // Sense canvis, no es refà
        int opened = _opened;
        _outputs.Reload();
        _outputs.Tick();
        Assert.Equal(opened, _opened);
        Assert.Equal(2, _server.Connections);

        // Esborrat: s'atura
        _saved = [];
        _outputs.Reload();
        _outputs.Tick();
        _server.WaitForConnection(1);
        Assert.Empty(_outputs.Status);
    }
}
