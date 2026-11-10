using System.Text.Json;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Scheduling;

/// <summary>Un esdeveniment preparat per disparar-se: les pistes ja buscades a la biblioteca i els avisos de la preparació.</summary>
public sealed record PreparedEvent(Occurrence Occurrence, IReadOnlyList<EventAction> Actions, IReadOnlyList<QueueItem> Items, IReadOnlyList<string> Warnings);

/// <summary>
/// Aplica les accions d'un esdeveniment (docs/planificador.md, seccions 2 i 4). Treballa al fil de control, com l'AutoDJ:
/// mentre aplica un esdeveniment, l'AutoDJ no hi pot afegir res a mig fer (esdeveniments atòmics).
///
/// Fase 2, pas 3: modes ENCUA i SISTEMA, accions PISTA, ROTACIO i AUTODJ. La resta de modes i accions, als passos següents.
/// </summary>
public sealed class EventExecutor(Library library, QueuePlayer queue, AutoDj? autoDj, Action<long?>? rememberRotation = null)
{
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
            items.Add(QueueItem.FromTrack(track, QueueOrigin.Event, ev.Name));
        }
        return new PreparedEvent(occurrence, actions, items, warnings);
    }

    /// <summary>Aplica l'esdeveniment preparat. Retorna el resultat i el detall per al registre d'execució.</summary>
    public (ExecutionResult Result, string Detail) Execute(PreparedEvent prepared)
    {
        var ev = prepared.Occurrence.Event;
        var notes = new List<string>(prepared.Warnings);

        switch (ev.Mode)
        {
            case EventMode.Queue:
                // Tot alhora: si cal, s'esborra la cua i després el contingut va al davant, en l'ordre de la llista (D-022)
                if (ev.ClearQueue)
                {
                    int removed = queue.RemoveWhere(_ => true);
                    if (removed > 0) notes.Add($"{removed} {(removed == 1 ? "pista treta" : "pistes tretes")} de la cua");
                }
                if (prepared.Items.Count > 0)
                {
                    queue.Insert(0, [.. prepared.Items]);
                    notes.Add($"{prepared.Items.Count} {(prepared.Items.Count == 1 ? "pista" : "pistes")} a la cua");
                }
                break;

            case EventMode.System:
                if (prepared.Items.Count > 0) notes.Add("Les pistes no sonen en un esdeveniment SISTEMA (només accions sense àudio).");
                break;

            default:
                return (ExecutionResult.Error, $"El mode {Events.ModeText(ev.Mode)} encara no està disponible (fase 2, passos 4 i 5).");
        }

        // Quantes accions s'han fet i quantes no: si no se n'ha pogut fer cap, és un error
        int done = ev.Mode == EventMode.Queue ? prepared.Items.Count : 0;
        int failed = prepared.Warnings.Count;
        foreach (var action in prepared.Actions)
        {
            (bool ok, string note) = action.Type switch
            {
                EventActionType.Track => (true, ""), // ja fet
                EventActionType.Rotation => ChangeRotation(action),
                EventActionType.AutoDj => ChangeAutoDj(action),
                _ => (false, $"L'acció {Events.ActionTypeText(action.Type)} encara no està disponible: no s'ha fet."),
            };
            if (action.Type == EventActionType.Track) continue;
            if (ok) done++;
            else failed++;
            notes.Add(note);
        }

        string detail = notes.Count > 0 ? string.Join("; ", notes.Select(n => n.TrimEnd('.'))) : "sense accions";
        return (done == 0 && failed > 0 ? ExecutionResult.Error : ExecutionResult.Ok, detail);
    }

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
