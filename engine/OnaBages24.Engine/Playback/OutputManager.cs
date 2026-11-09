using OnaBages.Audio;

namespace OnaBages24.Engine.Playback;

/// <summary>Sortida configurada (es desa a la configuració): identificador de Windows i nom, per si l'identificador canvia.</summary>
public sealed record ConfiguredOutput(string? Id, string? Name);

/// <summary>
/// Gestiona l'única sortida d'emissió (D-005, D-038; docs/disseny-motor.md, secció 6). Tria quina sortida s'obre, en aquest ordre:
/// 1. la configurada, per identificador;
/// 2. la configurada, pel nom (l'identificador canvia si es reinstal·la el controlador);
/// 3. una <b>sortida d'emergència</b>: la primera activa, primer les analògiques, després les digitals i, al final, HDMI.
/// Mai el «dispositiu predeterminat» de Windows. Si la sortida falla (p. ex. es desendolla), passa a la següent; quan
/// la configurada torna, hi torna de seguida. El motor crida <see cref="Ensure"/> sovint (fil de control).
/// </summary>
public sealed class OutputManager : IDisposable
{
    private readonly Mixer _mixer;
    private readonly Func<IReadOnlyList<AudioDevice>> _listDevices;
    private readonly Func<string, IAudioOutput> _openOutput;
    private readonly object _sync = new();
    private IAudioOutput? _output;
    private volatile bool _failed;
    private volatile bool _poked = true;

    /// <param name="listDevices">Dispositius de Windows (les proves en passen una llista inventada).</param>
    /// <param name="openOutput">Obre una sortida per identificador (les proves en fan servir una de falsa).</param>
    public OutputManager(Mixer mixer, Func<IReadOnlyList<AudioDevice>>? listDevices = null, Func<string, IAudioOutput>? openOutput = null)
    {
        _mixer = mixer;
        _listDevices = listDevices ?? DeviceList.GetAll;
        _openOutput = openOutput ?? (id => WasapiAudioOutput.Open(id));
    }

    /// <summary>La sortida configurada (pot ser buida: llavors s'emet per la d'emergència).</summary>
    public ConfiguredOutput Configured { get; private set; } = new(null, null);

    /// <summary>La sortida per on s'emet ara (null si no n'hi ha cap).</summary>
    public AudioDevice? Current { get; private set; }

    /// <summary>S'emet per una sortida d'emergència (no és la configurada): cal avisar.</summary>
    public bool IsEmergency { get; private set; }

    /// <summary>L'últim problema (en català), o null si tot va bé.</summary>
    public string? Problem { get; private set; }

    /// <summary>Ha canviat la sortida, l'estat d'emergència o el problema.</summary>
    public event Action? Changed;

    /// <summary>Canvia la sortida configurada i hi passa de seguida (al pròxim <see cref="Ensure"/>).</summary>
    public void Configure(ConfiguredOutput output)
    {
        lock (_sync) Configured = output;
        Poke();
    }

    /// <summary>Hi ha hagut un canvi de dispositius a Windows: es tornarà a mirar al pròxim <see cref="Ensure"/>.</summary>
    public void Poke() => _poked = true;

    /// <summary>
    /// Fil de control: comprova que s'emet per la millor sortida possible i, si cal, n'obre una altra.
    /// Si tot va bé i no hi ha hagut canvis, no fa res (és barata).
    /// </summary>
    public void Ensure()
    {
        bool changed = false;
        lock (_sync)
        {
            // Si s'emet per la d'emergència, es mira cada vegada si ha tornat la configurada.
            if (_output is not null && !_failed && !_poked && !IsEmergency) return;
            _poked = false;

            IReadOnlyList<AudioDevice> devices;
            try { devices = _listDevices(); }
            catch (Exception ex)
            {
                changed = SetProblem($"No s'han pogut llegir els dispositius: {ex.Message}");
                goto done;
            }

            var candidates = Candidates(devices, Configured);
            if (_output is not null && !_failed && candidates.Count > 0 && candidates[0].Device.Id == Current?.Id)
            {
                // Ja s'emet per la millor possible; si és la d'emergència, l'avís es manté (D-039)
                IsEmergency = candidates[0].Emergency;
                changed = SetProblem(IsEmergency ? EmergencyProblem(candidates[0].Device) : null);
                goto done;
            }
            if (_output is not null && !_failed && IsEmergency && (candidates.Count == 0 || candidates[0].Emergency))
                goto done; // continua per la d'emergència: no ha tornat la configurada

            // Cal obrir una sortida: primer es tanca l'anterior (el mesclador només el pot llegir una sortida alhora)
            CloseOutput();
            changed = true;
            var errors = new List<string>();
            foreach (var candidate in candidates)
            {
                try
                {
                    var output = _openOutput(candidate.Device.Id);
                    output.Stopped += ex => { if (ex is not null) { _failed = true; _poked = true; } };
                    output.Init(new OutputAdapter(_mixer, output.SampleRate, output.Channels));
                    output.Play();
                    _output = output;
                    _failed = false;
                    Current = candidate.Device;
                    IsEmergency = candidate.Emergency;
                    Problem = candidate.Emergency ? EmergencyProblem(candidate.Device) : null;
                    goto done;
                }
                catch (Exception ex) when (ex is AudioDeviceException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    errors.Add($"{candidate.Device.Name}: {ex.Message}");
                }
            }
            Current = null;
            IsEmergency = false;
            Problem = errors.Count == 0
                ? "No hi ha cap sortida d'àudio activa: no s'emet res."
                : "No s'ha pogut obrir cap sortida: " + string.Join("; ", errors);
        }
    done:
        if (changed) Changed?.Invoke();
    }

    private string EmergencyProblem(AudioDevice device) =>
        $"S'emet per una sortida d'emergència: {device.Name}" +
        (Configured.Name is null ? " (no hi ha cap sortida configurada)" : $" (no es troba «{Configured.Name}»)");

    private bool SetProblem(string? problem)
    {
        if (Problem == problem) return false;
        Problem = problem;
        return true;
    }

    /// <summary>
    /// Ordre de preferència de les sortides actives (D-038): la configurada per identificador, la configurada pel nom
    /// i, després, les d'emergència (analògiques, desconegudes, digitals, HDMI, remotes). Pública per provar-la.
    /// </summary>
    public static IReadOnlyList<(AudioDevice Device, bool Emergency)> Candidates(IEnumerable<AudioDevice> devices, ConfiguredOutput configured)
    {
        var active = devices.Where(d => d.Kind == DeviceKind.Output && d.Status == DeviceStatus.Active).ToList();
        var list = new List<(AudioDevice, bool)>();

        var byId = active.FirstOrDefault(d => configured.Id is not null && d.Id == configured.Id);
        var byName = byId ?? active.FirstOrDefault(d => configured.Name is not null && string.Equals(d.Name, configured.Name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) list.Add((byName, false));

        static int Rank(DeviceConnector c) => c switch
        {
            DeviceConnector.Analog => 0,
            DeviceConnector.Unknown => 1,
            DeviceConnector.Digital => 2,
            DeviceConnector.Hdmi => 3,
            _ => 4,
        };
        foreach (var device in active.Where(d => d != byName).OrderBy(d => Rank(d.Connector)).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            list.Add((device, true));
        return list;
    }

    private void CloseOutput()
    {
        if (_output is null) return;
        try { _output.Stop(); } catch (Exception) { /* ja no hi és */ }
        try { _output.Dispose(); } catch (Exception) { }
        _output = null;
    }

    public void Dispose()
    {
        lock (_sync) CloseOutput();
    }
}
