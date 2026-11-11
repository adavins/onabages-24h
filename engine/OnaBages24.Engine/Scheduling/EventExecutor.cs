using System.Text.Json;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Scheduling;

/// <summary>
/// Un esdeveniment preparat per disparar-se: les pistes ja buscades a la biblioteca i els avisos de la preparació.
/// En un TALLA, <paramref name="Reservation"/> és la primera pista ja oberta en una veu de reserva. <see cref="Aux"/>: els
/// fitxers de les accions AUXILIAR, ja oberts en un reproductor auxiliar si s'ha pogut.
/// </summary>
public sealed record PreparedEvent(Occurrence Occurrence, IReadOnlyList<EventAction> Actions, IReadOnlyList<QueueItem> Items,
    IReadOnlyList<string> Warnings, Guid? Reservation = null)
{
    public IReadOnlyList<PreparedAux> Aux { get; init; } = [];
}

/// <summary>Una acció AUXILIAR preparada: per quin reproductor, amb quina atenuació i, si s'ha pogut obrir, la reserva.</summary>
public sealed record PreparedAux(int Player, QueueItem Item, double? DuckingDb, Guid? Reservation);

/// <summary>Contingut d'una emissió anterior (un ENCUA) que s'ha esborrat de la cua abans de sonar: s'anota com a NO_EMES.</summary>
public sealed record NotPlayedEntry(long EventId, string Name, DateTimeOffset At, int Count);

/// <summary>Resultat d'aplicar un esdeveniment: el que s'anota al registre i el contingut d'altres emissions que ja no sonarà.</summary>
public sealed record ExecutionOutcome(ExecutionResult Result, string Detail)
{
    public IReadOnlyList<NotPlayedEntry> NotPlayed { get; init; } = [];

    /// <summary>
    /// El contingut s'ha programat a la mostra exacta i encara no ha sonat (un TALLA o un auxiliar): l'hora real i la
    /// diferència s'afegeixen a la línia del registre quan comenci (<see cref="EventExecutor.ContentStarted"/>).
    /// </summary>
    public bool AwaitsStart { get; init; }
}

/// <summary>
/// El contingut d'un esdeveniment programat a la mostra exacta ha començat a sonar (la primera pista d'un TALLA o un
/// auxiliar): quan i amb quina diferència respecte del fotograma previst.
/// </summary>
public sealed record ContentStarted(long EventId, DateTimeOffset At, double DelayMs, DateTimeOffset? HeardAt);

/// <summary>
/// Aplica les accions d'un esdeveniment (docs/planificador.md, seccions 2 i 4). Treballa al fil de control, com l'AutoDJ:
/// mentre aplica un esdeveniment, l'AutoDJ no hi pot afegir res a mig fer (esdeveniments atòmics).
///
/// Fase 2, passos 3 a 5: modes ENCUA, TALLA, SUPERPOSA i SISTEMA, accions PISTA, AUXILIAR, ROTACIO i AUTODJ. La resta, als
/// passos següents. L'acció AUXILIAR serveix en qualsevol mode (D-064): sona per sobre del que soni, a l'hora exacta.
/// </summary>
public sealed class EventExecutor
{
    /// <summary>Fos del tall si l'esdeveniment no en diu cap: el mateix que el botó «Següent» (queue.skip).</summary>
    public const int DefaultFadeMs = 1000;

    private readonly Library library;
    private readonly QueuePlayer queue;
    private readonly AutoDj? autoDj;
    private readonly Action<long?>? rememberRotation;
    private readonly SampleClock? clock;
    private readonly AuxPlayers? aux;

    /// <param name="clock">Rellotge de mostres per tallar a la mostra exacta. Sense, el TALLA talla en el moment de disparar-lo.</param>
    /// <param name="aux">Reproductors auxiliars (accions AUXILIAR). Sense, aquestes accions no es fan (avís al registre).</param>
    public EventExecutor(Library library, QueuePlayer queue, AutoDj? autoDj, Action<long?>? rememberRotation = null, SampleClock? clock = null,
        AuxPlayers? aux = null)
    {
        this.library = library;
        this.queue = queue;
        this.autoDj = autoDj;
        this.rememberRotation = rememberRotation;
        this.clock = clock;
        this.aux = aux;
        queue.Started += OnStarted;
        if (aux is not null) aux.Started += OnAuxStarted;
    }

    /// <summary>La primera pista d'un TALLA o un auxiliar d'un esdeveniment ha començat (al fil de control).</summary>
    public event Action<ContentStarted>? ContentStarted;

    private void OnStarted(QueueStarted started)
    {
        if (started.CutFrame is not { } target || started.Item.EventId is not { } eventId || started.Item.EventAt is not { } at) return;
        double delayMs = (started.StartFrame - target) * 1000.0 / queue.SampleRate;
        ContentStarted?.Invoke(new ContentStarted(eventId, at, delayMs, clock?.TimeOf(started.StartFrame)));
    }

    private void OnAuxStarted(AuxStarted started)
    {
        if (started.TargetFrame is not { } target || started.Item.EventId is not { } eventId || started.Item.EventAt is not { } at) return;
        double delayMs = (started.StartFrame - target) * 1000.0 / aux!.SampleRate;
        ContentStarted?.Invoke(new ContentStarted(eventId, at, delayMs, clock?.TimeOf(started.StartFrame)));
    }

    /// <summary>
    /// Si l'esdeveniment s'ha de disparar uns segons abans de l'hora perquè té contingut programat a la mostra exacta:
    /// un TALLA, un SUPERPOSA o qualsevol amb accions AUXILIAR.
    /// </summary>
    public static bool StartsAtExactFrame(ScheduledEvent ev) =>
        ev.Mode is EventMode.Cut or EventMode.Overlay || ev.Actions.Any(a => a.Type == EventActionType.Auxiliary);

    /// <summary>
    /// Prepara l'esdeveniment uns segons abans: busca les pistes a la biblioteca (només la base de dades; els fitxers els
    /// obre la cua, que ja salta els que falten). Si una pista no existeix, se salta amb un avís.
    /// </summary>
    public PreparedEvent Prepare(Occurrence occurrence)
    {
        var ev = occurrence.Event;
        var actions = Recurrences.ActionsFor(ev, occurrence.Date);
        var items = new List<QueueItem>();
        var warnings = new List<string>();
        foreach (var action in actions.Where(a => a.Type == EventActionType.Track))
        {
            if (ReadLong(action, "songId") is not { } id)
            {
                warnings.Add("Una acció PISTA no diu quina pista (songId).");
                continue;
            }
            var track = library.GetTrack(id);
            if (track is null)
            {
                warnings.Add($"La pista {id} no existeix a la biblioteca.");
                continue;
            }
            items.Add(QueueItem.FromTrack(track, QueueOrigin.Event, ev.Name) with { EventId = ev.Id, EventAt = occurrence.At });
        }

        // TALLA: la primera pista s'obre ja, perquè a l'hora no hi hagi cap retard per llegir el fitxer (secció 7)
        Guid? reservation = ev.Mode == EventMode.Cut && items.Count > 0 ? queue.Reserve(items[0]) : null;
        var auxItems = PrepareAux(occurrence, actions, warnings);
        return new PreparedEvent(occurrence, actions, items, warnings, reservation) { Aux = auxItems };
    }

    /// <summary>
    /// Les accions AUXILIAR: busca les pistes i les obre ja en un reproductor auxiliar, perquè a l'hora no hi hagi cap
    /// retard. Una per reproductor i esdeveniment (D-064): les sobrants no es fan.
    /// </summary>
    private List<PreparedAux> PrepareAux(Occurrence occurrence, IReadOnlyList<EventAction> actions, List<string> warnings)
    {
        var ev = occurrence.Event;
        var result = new List<PreparedAux>();
        foreach (var action in actions.Where(a => a.Type == EventActionType.Auxiliary))
        {
            if (aux is null)
            {
                warnings.Add("No hi ha reproductors auxiliars: l'acció AUXILIAR no s'ha fet.");
                continue;
            }
            if (ReadLong(action, "songId") is not { } id)
            {
                warnings.Add("Una acció AUXILIAR no diu quina pista (songId).");
                continue;
            }
            long player = ReadLong(action, "player") ?? 1;
            if (player is < 1 or > AuxPlayers.Count)
            {
                warnings.Add($"Una acció AUXILIAR diu el reproductor {player}, i només n'hi ha de l'1 al {AuxPlayers.Count}.");
                continue;
            }
            if (result.Any(a => a.Player == player))
            {
                warnings.Add($"Hi ha més d'una acció AUXILIAR pel reproductor {player}: només sona la primera.");
                continue;
            }
            var track = library.GetTrack(id);
            if (track is null)
            {
                warnings.Add($"La pista {id} (AUXILIAR) no existeix a la biblioteca.");
                continue;
            }
            // L'atenuació de l'acció té prioritat sobre la de l'esdeveniment (D-064)
            double? ducking = ReadDouble(action, "duckingDb") ?? ev.DuckingDb;
            var item = QueueItem.FromTrack(track, QueueOrigin.Event, ev.Name) with { EventId = ev.Id, EventAt = occurrence.At };
            result.Add(new PreparedAux((int)player, item, ducking, aux.Prepare((int)player, item, ducking)));
        }
        return result;
    }

    /// <summary>Un esdeveniment preparat que no es dispararà (omès, esborrat o canviat): deixa anar les reserves.</summary>
    public void Discard(PreparedEvent prepared)
    {
        if (prepared.Reservation is { } reservation) queue.Release(reservation);
        foreach (var a in prepared.Aux)
        {
            if (a.Reservation is { } id) aux?.Release(id);
        }
    }

    /// <summary>Aplica l'esdeveniment preparat. Retorna el resultat i el detall per al registre d'execució.</summary>
    public ExecutionOutcome Execute(PreparedEvent prepared)
    {
        var occurrence = prepared.Occurrence;
        var ev = occurrence.Event;
        var notes = new List<string>(prepared.Warnings);
        IReadOnlyList<QueueItem> removed = [];

        switch (ev.Mode)
        {
            case EventMode.Queue:
                // Tot alhora: si cal, s'esborra la cua i després el contingut va al davant, en l'ordre de la llista (D-022)
                if (ev.ClearQueue)
                {
                    removed = queue.Clear();
                    if (removed.Count > 0) notes.Add($"{removed.Count} {(removed.Count == 1 ? "pista treta" : "pistes tretes")} de la cua");
                }
                if (prepared.Items.Count > 0)
                {
                    queue.Insert(0, [.. prepared.Items]);
                    notes.Add($"{prepared.Items.Count} {(prepared.Items.Count == 1 ? "pista" : "pistes")} a la cua");
                }
                break;

            case EventMode.Cut:
            {
                // Al fotograma que sonarà a l'hora exacta (D-021). Sense sortida no se sap: es talla de seguida.
                long? frame = clock?.FrameAt(occurrence.At);
                var fade = TimeSpan.FromMilliseconds(ev.FadeMs ?? DefaultFadeMs);
                removed = queue.CutAt(frame ?? queue.CurrentFrame, fade, prepared.Items, ev.ClearQueue, prepared.Reservation);
                notes.Add(frame is null
                    ? "tall de seguida (no se sap l'hora de la sortida)"
                    : $"tall a les {occurrence.LocalTime:HH\\:mm\\:ss}, amb fos de {fade.TotalMilliseconds:0} ms");
                if (removed.Count > 0) notes.Add($"{removed.Count} {(removed.Count == 1 ? "pista treta" : "pistes tretes")} de la cua");
                notes.Add(prepared.Items.Count == 0
                    ? "sense pistes"
                    : $"{prepared.Items.Count} {(prepared.Items.Count == 1 ? "pista" : "pistes")}");
                break;
            }

            case EventMode.Overlay:
                if (prepared.Items.Count > 0)
                    notes.Add("Les pistes PISTA no sonen en un esdeveniment SUPERPOSA (només les accions AUXILIAR, per sobre del que soni).");
                break;

            case EventMode.System:
                if (prepared.Items.Count > 0) notes.Add("Les pistes no sonen en un esdeveniment SISTEMA (només accions sense àudio).");
                break;

            default:
                return new ExecutionOutcome(ExecutionResult.Error, $"Mode desconegut: {ev.Mode}.");
        }

        // Quantes accions s'han fet i quantes no: si no se n'ha pogut fer cap, és un error. Un TALLA sempre fa alguna cosa (talla).
        int done = ev.Mode switch { EventMode.Queue => prepared.Items.Count, EventMode.Cut => 1, _ => 0 };
        int failed = prepared.Warnings.Count;

        // Els auxiliars: a la mostra que sonarà a l'hora exacta (com el TALLA), per sobre del que soni
        int auxStarted = 0;
        foreach (var a in prepared.Aux)
        {
            var id = a.Reservation ?? aux?.Prepare(a.Player, a.Item, a.DuckingDb); // no s'havia pogut obrir abans: ara
            if (id is null)
            {
                failed++;
                notes.Add($"«{a.Item}» (AUXILIAR) no ha sonat: no queden veus lliures al mesclador");
                continue;
            }
            long? frame = clock?.FrameAt(occurrence.At);
            aux!.StartAt(id.Value, frame);
            auxStarted++;
            notes.Add($"«{a.Item}» pel reproductor auxiliar {a.Player}" +
                (a.DuckingDb is { } db ? $", amb la música a {db:0.#} dB" : "") +
                (frame is null ? " (de seguida: no se sap l'hora de la sortida)" : ""));
        }
        done += auxStarted;
        foreach (var action in prepared.Actions)
        {
            (bool ok, string note) = action.Type switch
            {
                EventActionType.Track => (true, ""), // ja fet
                EventActionType.Auxiliary => (true, ""), // ja fet (i els avisos, a la preparació)
                EventActionType.Rotation => ChangeRotation(action),
                EventActionType.AutoDj => ChangeAutoDj(action),
                _ => (false, $"L'acció {Events.ActionTypeText(action.Type)} encara no està disponible: no s'ha fet."),
            };
            if (action.Type is EventActionType.Track or EventActionType.Auxiliary) continue;
            if (ok) done++;
            else failed++;
            notes.Add(note);
        }

        string detail = notes.Count > 0 ? string.Join("; ", notes.Select(n => n.TrimEnd('.'))) : "sense accions";
        return new ExecutionOutcome(done == 0 && failed > 0 ? ExecutionResult.Error : ExecutionResult.Ok, detail)
        {
            NotPlayed = NotPlayed(removed),
            // La línia del registre es completa quan comenci a sonar (la primera pista del tall o un auxiliar)
            AwaitsStart = ev.Mode == EventMode.Cut || auxStarted > 0,
        };
    }

    /// <summary>Les pistes d'esdeveniments que s'han tret de la cua sense sonar, agrupades per emissió (NO_EMES).</summary>
    private static IReadOnlyList<NotPlayedEntry> NotPlayed(IReadOnlyList<QueueItem> removed) =>
        removed
            .Where(i => i.EventId is not null && i.EventAt is not null)
            .GroupBy(i => (EventId: i.EventId!.Value, At: i.EventAt!.Value))
            .Select(g => new NotPlayedEntry(g.Key.EventId, g.First().EventName ?? "", g.Key.At, g.Count()))
            .ToList();

    private (bool Ok, string Note) ChangeRotation(EventAction action)
    {
        if (autoDj is null) return (false, "No hi ha AutoDJ: no s'ha canviat la rotació.");
        if (ReadLong(action, "rotacioId") is not { } id) return (false, "Una acció ROTACIO no diu quina rotació (rotacioId).");
        if (!autoDj.Configure(autoDj.Enabled, id)) return (false, $"La rotació {id} no existeix: no s'ha canviat.");
        rememberRotation?.Invoke(id); // com amb autodj.set: es manté si el motor es reinicia (D-053)
        return (true, $"rotació «{autoDj.Rotation?.Name}»");
    }

    private (bool Ok, string Note) ChangeAutoDj(EventAction action)
    {
        if (autoDj is null) return (false, "No hi ha AutoDJ.");
        if (ReadBool(action, "actiu") is not { } enabled) return (false, "Una acció AUTODJ no diu si s'activa o s'atura (actiu).");
        autoDj.Configure(enabled, autoDj.Rotation?.Id);
        return (true, enabled ? "AutoDJ activat" : "AutoDJ aturat");
    }

    private static long? ReadLong(EventAction action, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            return json.RootElement.TryGetProperty(name, out var value) && value.TryGetInt64(out long n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ReadDouble(EventAction action, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            return json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool? ReadBool(EventAction action, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            return json.RootElement.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
