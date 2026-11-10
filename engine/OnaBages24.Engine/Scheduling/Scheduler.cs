using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Ajustos del planificador.</summary>
public sealed record SchedulerSettings
{
    /// <summary>Quant abans de l'hora es prepara cada esdeveniment (D-008).</summary>
    public TimeSpan PrepareAhead { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fins a quan es calculen les emissions per endavant (ha de ser més que <see cref="PrepareAhead"/>).</summary>
    public TimeSpan Lookahead { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Cada quant es tornen a calcular les emissions de la finestra.</summary>
    public TimeSpan RefreshEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// En arrencar, fins a quan enrere es miren les emissions que no s'han fet (motor aturat o reiniciat): les que encara
    /// són dins de la tolerància es disparen tard; les altres s'anoten com a omeses (D-057).
    /// </summary>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Planificador (docs/planificador.md; D-008, D-057). Treballa al fil de control del motor (<see cref="Tick"/>), mai amb
/// temporitzadors de la interfície:
/// - té a memòria les emissions dels pròxims minuts (es tornen a calcular cada minut i quan es desa un esdeveniment);
/// - uns 30 s abans de l'hora, prepara cada esdeveniment (<see cref="EventExecutor.Prepare"/>);
/// - a l'hora, el dispara i n'anota el resultat al registre d'execució.
/// Regles: un esdeveniment que arriba més tard que la seva tolerància s'omet (<c>OMES_TARD</c>); si dos TALLA o ENCUA
/// coincideixen al mateix instant, només es dispara el de prioritat més alta (<c>OMES_CONFLICTE</c>); una hora que no
/// existeix pel canvi d'hora no sona (<c>OMES_CANVI_HORA</c>). Els desactivats i les plantilles no es disparen mai.
/// Cada emissió es fa una sola vegada, també si el motor es reinicia (es mira el registre).
/// </summary>
public sealed class Scheduler
{
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(1);

    private readonly Events _events;
    private readonly EventExecutor _executor;
    private readonly ILogger _log;
    private readonly SchedulerSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _zone;
    private readonly object _sync = new();

    private List<Occurrence> _window = [];
    private readonly HashSet<(long EventId, DateTimeOffset At)> _handled = [];
    private readonly Dictionary<(long EventId, DateTimeOffset At), PreparedEvent> _prepared = [];
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastTick;
    private volatile bool _reloadRequested;

    /// <param name="clock">Rellotge (les proves en fan servir un de fals). Per defecte, l'hora de l'ordinador.</param>
    /// <param name="zone">Zona horària de la graella. Per defecte, la de l'ordinador.</param>
    public Scheduler(Events events, EventExecutor executor, ILogger log, SchedulerSettings? settings = null,
        Func<DateTimeOffset>? clock = null, TimeZoneInfo? zone = null)
    {
        _events = events;
        _executor = executor;
        _log = log;
        _settings = settings ?? new SchedulerSettings();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>S'ha disparat (o s'ha omès) una emissió: la línia que s'ha anotat al registre.</summary>
    public event Action<ExecutionRecord>? Fired;

    /// <summary>S'ha desat o esborrat algun esdeveniment (per avisar la interfície).</summary>
    public event Action? EventsChanged;

    public TimeZoneInfo Zone => _zone;

    /// <summary>Les emissions que encara no s'han fet dels pròxims minuts, en ordre (per a la interfície i les proves).</summary>
    public IReadOnlyList<Occurrence> Pending
    {
        get
        {
            lock (_sync) return _window.Where(o => !_handled.Contains(Key(o))).ToList();
        }
    }

    /// <summary>Els esdeveniments han canviat (s'ha desat o esborrat algun): es tornen a llegir a la pròxima volta.</summary>
    public void Reload()
    {
        _reloadRequested = true;
        EventsChanged?.Invoke();
    }

    /// <summary>Fil de control: prepara i dispara el que toqui. Barata quan no cal fer res.</summary>
    public void Tick()
    {
        var now = _clock();
        lock (_sync)
        {
            if (_reloadRequested || now >= _nextRefresh) Refresh(now);

            var due = new List<Occurrence>();
            foreach (var occurrence in _window)
            {
                if (occurrence.At > now + _settings.PrepareAhead) break; // la finestra va per ordre
                var key = Key(occurrence);
                if (_handled.Contains(key)) continue;
                if (!occurrence.SkippedClockChange && !_prepared.ContainsKey(key)) _prepared[key] = Prepare(occurrence);
                if (occurrence.At <= now) due.Add(occurrence);
            }

            foreach (var group in due.GroupBy(o => o.At))
                Handle(group.ToList(), now);

            _lastTick = now;
        }
    }

    /// <summary>Torna a llegir els esdeveniments i calcula les emissions de la finestra.</summary>
    private void Refresh(DateTimeOffset now)
    {
        _reloadRequested = false;
        _nextRefresh = now + _settings.RefreshEvery;
        List<ScheduledEvent> active;
        try
        {
            active = _events.List().Where(e => e.Enabled && !e.IsTemplate).ToList();
        }
        catch (Exception ex)
        {
            // Sense base de dades es continua amb el que ja es tenia; es torna a provar d'aquí a un minut
            _log.LogError("Planificador: no s'han pogut llegir els esdeveniments ({Message})", ex.Message);
            return;
        }

        // Des de l'última volta (amb un marge): així no es perd res si el fil s'ha encallat, i un esdeveniment desat ara
        // amb una hora que ja ha passat no s'anota com a omès. En arrencar, des de fa una hora (motor aturat o reiniciat).
        var from = (_lastTick ?? now - _settings.Lookback) - Margin;
        var to = now + _settings.Lookahead;
        _window = active
            .SelectMany(e => Recurrences.Between(e, from, to, _zone))
            .OrderBy(o => o.At)
            .ThenByDescending(o => o.Event.Priority)
            .ThenBy(o => o.Event.Id)
            .ToList();

        // Les que ja han passat i potser ja es van fer (abans de reiniciar el motor): es miren al registre
        foreach (var occurrence in _window.Where(o => o.At <= now && !_handled.Contains(Key(o))))
        {
            try
            {
                if (_events.HasExecution(occurrence.Event.Id, occurrence.At)) _handled.Add(Key(occurrence));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Planificador: no s'ha pogut consultar el registre ({Message})", ex.Message);
            }
        }

        // Neteja: el que ja no és a la finestra
        _handled.RemoveWhere(k => k.At < from - _settings.Lookback);
        foreach (var key in _prepared.Keys.Where(k => !_window.Any(o => Key(o) == k)).ToList())
            _prepared.Remove(key);
    }

    private PreparedEvent Prepare(Occurrence occurrence)
    {
        try
        {
            return _executor.Prepare(occurrence);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Planificador: no s'ha pogut preparar «{Name}» ({Message})", occurrence.Event.Name, ex.Message);
            return new PreparedEvent(occurrence, [], [], [$"No s'ha pogut preparar: {ex.Message}"]);
        }
    }

    /// <summary>Les emissions d'un mateix instant: omeses, en conflicte o disparades.</summary>
    private void Handle(List<Occurrence> group, DateTimeOffset now)
    {
        var toRun = new List<Occurrence>();
        foreach (var occurrence in group)
        {
            if (occurrence.SkippedClockChange)
                Record(occurrence, null, ExecutionResult.SkippedClockChange,
                    $"Les {occurrence.LocalTime:HH\\:mm\\:ss} no existeixen el {occurrence.Date:dd/MM/yyyy} pel canvi d'hora.");
            else if (now - occurrence.At > TimeSpan.FromSeconds(occurrence.Event.ToleranceSeconds))
                Record(occurrence, null, ExecutionResult.SkippedLate,
                    $"S'hi ha arribat {FormatLate(now - occurrence.At)} tard (tolerància: {occurrence.Event.ToleranceSeconds} s).");
            else
                toRun.Add(occurrence);
        }

        // TALLA i ENCUA no poden coincidir: guanya la prioritat més alta (la finestra ja hi va ordenada)
        Occurrence? winner = toRun.FirstOrDefault(o => IsExclusive(o.Event.Mode));
        foreach (var occurrence in toRun)
        {
            if (IsExclusive(occurrence.Event.Mode) && occurrence != winner)
                Record(occurrence, null, ExecutionResult.SkippedConflict,
                    $"Coincideix amb «{winner!.Event.Name}», de prioritat més alta ({winner.Event.Priority}).");
            else
                Run(occurrence, now);
        }
    }

    private void Run(Occurrence occurrence, DateTimeOffset now)
    {
        var key = Key(occurrence);
        var prepared = _prepared.TryGetValue(key, out var p) ? p : Prepare(occurrence);
        _prepared.Remove(key);
        (ExecutionResult result, string detail) outcome;
        try
        {
            outcome = _executor.Execute(prepared);
        }
        catch (Exception ex)
        {
            outcome = (ExecutionResult.Error, $"Error en executar-lo: {ex.Message}");
        }
        Record(occurrence, now, outcome.result, outcome.detail);
        if (outcome.result == ExecutionResult.Ok)
            _log.LogInformation("Esdeveniment «{Name}» ({Time:HH:mm:ss}): {Detail}", occurrence.Event.Name, occurrence.LocalTime, outcome.detail);
    }

    private void Record(Occurrence occurrence, DateTimeOffset? executedAt, ExecutionResult result, string detail)
    {
        _handled.Add(Key(occurrence));
        var record = new ExecutionRecord
        {
            EventId = occurrence.Event.Id,
            Name = occurrence.Event.Name,
            ScheduledFor = occurrence.At,
            ExecutedAt = executedAt,
            Result = result,
            Detail = detail,
        };
        if (result != ExecutionResult.Ok)
            _log.LogWarning("Esdeveniment «{Name}» ({Time:HH:mm:ss}): {Result}. {Detail}",
                occurrence.Event.Name, occurrence.LocalTime, Events.ResultText(result), detail);
        try
        {
            record = record with { Id = _events.AddExecution(record) };
        }
        catch (Exception ex)
        {
            _log.LogError("Planificador: no s'ha pogut anotar «{Name}» al registre ({Message})", occurrence.Event.Name, ex.Message);
        }
        try { Fired?.Invoke(record); }
        catch (Exception ex) { _log.LogWarning("Planificador: error en avisar la interfície ({Message})", ex.Message); }
    }

    private static bool IsExclusive(EventMode mode) => mode is EventMode.Cut or EventMode.Queue;

    private static (long, DateTimeOffset) Key(Occurrence o) => (o.Event.Id, o.At);

    private static string FormatLate(TimeSpan late) =>
        late < TimeSpan.FromMinutes(1) ? $"{late.TotalSeconds:0} s" : $"{(int)late.TotalMinutes} min {late.Seconds} s";
}
