using OnaBages.Audio;

namespace OnaBages24.Engine.Streaming;

/// <summary>Com està un emissor ara mateix (per a la pantalla i per a <c>streaming.list</c>).</summary>
public enum StreamOutputState
{
    /// <summary>No ha d'emetre (desactivat, o programable i ara no li toca).</summary>
    Off,
    /// <summary>Hauria d'emetre però li falta alguna dada (o la contrasenya desada no es pot llegir).</summary>
    Incomplete,
    Connecting,
    Streaming,
    /// <summary>S'havia connectat i s'ha tallat: es torna a connectar sol.</summary>
    Reconnecting,
    /// <summary>S'ha aturat per un error (l'entrada no hi és, el codificador…): es torna a engegar sol al cap d'una estona.</summary>
    Failed,
}

/// <summary>L'estat d'un emissor (una fotografia: no canvia).</summary>
public sealed record StreamOutputStatus(
    string Id, string Name, StreamOutputState State, string? Error, DateTimeOffset? ConnectedAt, DateTimeOffset? DownSince,
    int Reconnects, string Title, bool InputActive, long BytesDropped);

/// <summary>
/// Els emissors per internet en marxa (fase 5, pas 13a-2; D-202). Cada emissor actiu i continu té el seu <see cref="Streamer"/>
/// (amb els seus fils de captura, codificació i enviament): l'engega, li envia el títol de l'emissió quan canvia i, si
/// s'atura per un error (l'entrada desconnectada…), el torna a engegar sol cada <see cref="RestartDelay"/>. Les
/// reconnexions al servidor ja les fa el mateix <see cref="Streamer"/>.
///
/// <see cref="Tick"/> s'ha de cridar sovint (el motor ho fa cada mig segon, en un fil propi; mai el d'àudio). Un emissor
/// que falla no afecta l'emissió ni els altres emissors: només surt a <see cref="Problems"/> (incidència) i a l'estat.
/// </summary>
public sealed class StreamOutputs : IDisposable
{
    /// <summary>Quant s'espera per tornar a engegar un emissor que s'ha aturat per un error.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Quanta estona pot estar sense emetre abans que sigui una incidència: en engegar, o en un tall curt de la xarxa, es
    /// reconnecta sol en pocs segons i no cal avisar.
    /// </summary>
    public static readonly TimeSpan ProblemAfter = TimeSpan.FromSeconds(30);

    /// <summary>Quant espera un emissor que s'ha refet (n'ha canviat la configuració) abans d'engegar-se.</summary>
    public static readonly TimeSpan ReplaceDelay = TimeSpan.FromSeconds(3);

    /// <summary>La clau de la incidència d'un emissor (tipus <c>EMISSIO</c>).</summary>
    public static string IncidentKey(string id) => $"emissor:{id}";

    private sealed class Running(SavedStreamOutput config, string password, Streamer streamer)
    {
        public SavedStreamOutput Config { get; } = config;
        public string Password { get; } = password;
        public Streamer Streamer { get; } = streamer;
        public DateTimeOffset NextStart { get; set; }
        public DateTimeOffset? DownSince { get; set; }
        public string? LoggedError { get; set; }
        /// <summary>L'últim títol passat a l'emissor (null = cap encara).</summary>
        public string? SentTitle { get; set; }
        public StreamerState LoggedState { get; set; } = StreamerState.Idle;
    }

    private readonly Func<IReadOnlyList<SavedStreamOutput>> _load;
    private readonly Func<string?> _emissionTitle;
    private readonly Func<StreamOutput, Streamer> _create;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger? _log;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Running> _running = [];
    private IReadOnlyList<SavedStreamOutput> _config = [];
    private bool _reload = true;
    private IReadOnlyList<StreamOutputStatus> _status = [];
    private readonly List<Streamer> _toDispose = []; // els aturats: es tanquen fora del bloqueig (pot tardar uns segons)

    /// <param name="load">Llegeix els emissors desats (<see cref="StreamOutputStore.Read"/>).</param>
    /// <param name="emissionTitle">El títol de l'emissió ara (el text de <c>NowPlaying.txt</c>), o null si encara no n'hi ha.</param>
    /// <param name="create">Crea l'emissor d'un emissor desat. Per defecte, el de debò amb l'entrada de Windows; les proves
    /// en fan servir un amb una entrada i un servidor falsos.</param>
    public StreamOutputs(Func<IReadOnlyList<SavedStreamOutput>> load, Func<string?> emissionTitle,
        Func<StreamOutput, Streamer>? create = null, Func<DateTimeOffset>? clock = null, ILogger? log = null)
    {
        _load = load;
        _emissionTitle = emissionTitle;
        _create = create ?? CreateStreamer;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _log = log;
    }

    /// <summary>Es dispara (des de <see cref="Tick"/>) quan canvia l'estat d'algun emissor.</summary>
    public event Action<IReadOnlyList<StreamOutputStatus>>? Changed;

    /// <summary>L'estat de tots els emissors desats, en el seu ordre (el de l'últim <see cref="Tick"/>).</summary>
    public IReadOnlyList<StreamOutputStatus> Status { get { lock (_sync) return _status; } }

    /// <summary>
    /// Les incidències que hi ha d'haver ara (clau → text; tipus <c>EMISSIO</c>, greus): els emissors que haurien d'emetre i
    /// fa més de <see cref="ProblemAfter"/> que no ho fan, i els que no poden perquè els falta alguna dada.
    /// </summary>
    public IReadOnlyDictionary<string, string> Problems
    {
        get
        {
            var now = _clock();
            var problems = new Dictionary<string, string>();
            foreach (var s in Status)
            {
                if (s.State == StreamOutputState.Incomplete)
                    problems[IncidentKey(s.Id)] = $"L'emissor per internet «{s.Name}» no pot emetre: {s.Error}";
                else if (s.DownSince is { } since && now - since >= ProblemAfter)
                    problems[IncidentKey(s.Id)] = $"L'emissor per internet «{s.Name}» no emet des de les {since:HH:mm}: "
                        + (s.Error ?? (s.State == StreamOutputState.Connecting ? "no es pot connectar amb el servidor." : "s'està tornant a connectar."));
            }
            return problems;
        }
    }

    /// <summary>Els emissors desats han canviat: es tornen a llegir al pròxim <see cref="Tick"/>.</summary>
    public void Reload()
    {
        lock (_sync) _reload = true;
    }

    /// <summary>
    /// Revisió periòdica: aplica la configuració (engega, atura o refà els emissors que han canviat), torna a engegar els que
    /// s'han aturat per un error, envia el títol si ha canviat i actualitza l'estat.
    /// </summary>
    public void Tick()
    {
        IReadOnlyList<StreamOutputStatus> status;
        List<Streamer> stopped;
        bool changed;
        lock (_sync)
        {
            if (_reload)
            {
                _reload = false;
                _config = _load();
            }
            var now = _clock();
            string emissionTitle = _emissionTitle() ?? "";
            var list = new List<StreamOutputStatus>();
            var wanted = new HashSet<string>();
            foreach (var saved in _config)
            {
                var o = saved.Output;
                if (!o.Enabled || !o.Continuous)
                {
                    list.Add(new StreamOutputStatus(o.Id, o.Name, StreamOutputState.Off, null, null, null, 0, "", false, 0));
                    continue;
                }
                string? password = saved.Password;
                string? missing = saved.PasswordUnreadable
                    ? "no es pot llegir la contrasenya desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure a ⚙ Configuració."
                    : o.Missing(password is not null) is { } m ? Lower(m) : null;
                if (missing is not null)
                {
                    list.Add(new StreamOutputStatus(o.Id, o.Name, StreamOutputState.Incomplete, missing, null, null, 0, "", false, 0));
                    continue;
                }
                wanted.Add(o.Id);
                var running = Ensure(saved, password!, now);
                list.Add(Step(running, TitleFor(o, emissionTitle), now));
            }
            foreach (string id in _running.Keys.Where(id => !wanted.Contains(id)).ToList()) StopAndRemove(id);

            changed = !list.SequenceEqual(_status, StatusComparer.Instance);
            _status = list;
            status = list;
            stopped = [.. _toDispose];
            _toDispose.Clear();
        }
        foreach (var streamer in stopped) streamer.Dispose(); // envia el que quedava i tanca la connexió
        if (changed) Changed?.Invoke(status);
    }

    /// <summary>L'emissor en marxa per a aquesta configuració: el que ja hi era o, si ha canviat (o no n'hi havia), un de nou.</summary>
    private Running Ensure(SavedStreamOutput saved, string password, DateTimeOffset now)
    {
        var o = saved.Output;
        if (_running.TryGetValue(o.Id, out var running) && running.Config.Output == o && running.Password == password) return running;
        // Si ha canviat, el d'abans s'atura; el nou espera uns segons que el servidor alliberi el punt de muntatge
        bool replacing = running is not null;
        if (replacing) StopAndRemove(o.Id);

        var streamer = _create(o);
        streamer.SetDevice(DeviceKey(o));
        streamer.SetServer(o.ToServer(password), o.ToInfo());
        streamer.SetQuality(o.BitrateKbps, o.SampleRate, o.Mono);
        running = new Running(saved, password, streamer) { NextStart = replacing ? now + ReplaceDelay : now };
        _running[o.Id] = running;
        _log?.LogInformation("Emissor per internet «{Name}»: s'engega ({Server})", o.Name, Describe(o));
        return running;
    }

    /// <summary>Una volta d'un emissor: el títol, l'estat del <see cref="Streamer"/> i, si s'ha aturat, tornar-lo a engegar.</summary>
    private StreamOutputStatus Step(Running r, string title, DateTimeOffset now)
    {
        var s = r.Streamer;
        var o = r.Config.Output;
        if (title != r.SentTitle)
        {
            s.SetTitle(title);
            r.SentTitle = title;
        }
        s.Tick();
        // S'acaba d'aturar per un error: es torna a engegar al cap de RestartDelay (comptat des d'ara, no des de l'engegada)
        if (s.State == StreamerState.Error && r.LoggedState != StreamerState.Error) r.NextStart = now + RestartDelay;
        if (!s.IsActive && now >= r.NextStart)
        {
            try { s.Start(); }
            catch (StreamerCommandException ex) { _log?.LogWarning("Emissor per internet «{Name}»: no s'ha pogut engegar: {Error}", o.Name, ex.Message); }
            r.NextStart = now + RestartDelay;
        }

        if (s.State == StreamerState.Streaming) r.DownSince = null;
        else r.DownSince ??= now;

        // Al registre, només els canvis (no cada intent)
        string? error = s.State == StreamerState.Error ? s.ErrorMessage : s.ConnectionError;
        if (s.State != r.LoggedState || error != r.LoggedError)
        {
            if (s.State == StreamerState.Streaming)
                _log?.LogInformation("Emissor per internet «{Name}»: connectat, s'emet", o.Name);
            else if (error is not null)
                _log?.LogWarning("Emissor per internet «{Name}»: {Error}", o.Name, error + (s.ErrorDetail is { } d && s.State == StreamerState.Error ? $" ({d})" : ""));
            r.LoggedState = s.State;
            r.LoggedError = error;
        }

        var state = s.State switch
        {
            StreamerState.Streaming => StreamOutputState.Streaming,
            StreamerState.Reconnecting => StreamOutputState.Reconnecting,
            StreamerState.Error => StreamOutputState.Failed,
            _ => StreamOutputState.Connecting,
        };
        return new StreamOutputStatus(o.Id, o.Name, state, error, s.ConnectedAt, r.DownSince, s.Reconnects, s.Title, s.InputActive, s.BytesDropped);
    }

    private void StopAndRemove(string id)
    {
        if (!_running.Remove(id, out var running)) return;
        _log?.LogInformation("Emissor per internet «{Name}»: s'atura", running.Config.Output.Name);
        _toDispose.Add(running.Streamer);
    }

    private static string TitleFor(StreamOutput o, string emissionTitle) => o.TitleMode switch
    {
        StreamTitleMode.Fixed => o.FixedTitle,
        StreamTitleMode.None => "",
        _ => emissionTitle,
    };

    /// <summary>
    /// Com es diu l'entrada a l'emissor (<see cref="Streamer.SetDevice"/>): l'identificador de Windows o, si no n'hi ha,
    /// el nom. El <see cref="Streamer"/> de debò l'obre amb tots dos (<see cref="CreateStreamer"/>).
    /// </summary>
    private static string DeviceKey(StreamOutput o) => o.InputDeviceId ?? o.InputDeviceName ?? "";

    /// <summary>L'emissor de debò: obre l'entrada per identificador i, si no hi és, pel nom (com les entrades de línia).</summary>
    private static Streamer CreateStreamer(StreamOutput o) =>
        new(o.Id, _ => LineInputReader.OpenDevice(o.InputDeviceId, o.InputDeviceName));

    private static string Describe(StreamOutput o) => o.ServerType == StreamServerType.Icecast
        ? $"Icecast {o.Host}:{o.Port}{o.Mount}, {o.BitrateKbps} kbps"
        : $"Shoutcast {o.Host}:{o.Port} sid {o.StreamId}, {o.BitrateKbps} kbps";

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    public void Dispose()
    {
        List<Streamer> stopped;
        lock (_sync)
        {
            foreach (string id in _running.Keys.ToList()) StopAndRemove(id);
            stopped = [.. _toDispose];
            _toDispose.Clear();
        }
        foreach (var streamer in stopped) streamer.Dispose();
    }

    /// <summary>Compara els estats sense els bytes perduts (canvien sovint i no cal avisar la interfície per això).</summary>
    private sealed class StatusComparer : IEqualityComparer<StreamOutputStatus>
    {
        public static readonly StatusComparer Instance = new();
        public bool Equals(StreamOutputStatus? a, StreamOutputStatus? b) =>
            a is not null && b is not null && a with { BytesDropped = 0 } == b with { BytesDropped = 0 };
        public int GetHashCode(StreamOutputStatus s) => s.Id.GetHashCode();
    }
}
