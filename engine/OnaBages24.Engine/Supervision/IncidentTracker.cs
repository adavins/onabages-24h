using Microsoft.Data.Sqlite;
using OnaBages24.Data;

namespace OnaBages24.Engine.Supervision;

/// <summary>Els tipus d'incidència (columna <c>tipus</c>; docs/protocol.md, 4.13).</summary>
public static class IncidentKind
{
    /// <summary>S'emet per la sortida d'emergència, o no hi ha cap sortida (D-038).</summary>
    public const string Output = "SORTIDA";
    /// <summary>Una pista que no s'ha pogut fer sonar (fitxer que falta o il·legible) i s'ha saltat.</summary>
    public const string File = "FITXER";
    /// <summary>Un esdeveniment omès perquè s'hi ha arribat massa tard (p. ex. amb el motor aturat).</summary>
    public const string EventSkipped = "ESDEVENIMENT_OMES";
    /// <summary>Un esdeveniment que ha fallat en executar-se.</summary>
    public const string EventError = "ESDEVENIMENT_ERROR";
    /// <summary>Un esdeveniment a l'hora exacta que ha començat més tard del compte.</summary>
    public const string EventLate = "ESDEVENIMENT_TARD";
    /// <summary>Una carpeta vigilada que no es pot revisar o amb fitxers que falten.</summary>
    public const string Folder = "CARPETA";
    /// <summary>Una pausa que ha continuat sola, o una pista que no s'ha pogut tornar a obrir (D-103).</summary>
    public const string Pause = "PAUSA";
    /// <summary>El títol per a BUTT no es pot escriure.</summary>
    public const string Butt = "BUTT";
    /// <summary>L'AutoDJ no troba res per posar a la cua (o hi posa la reserva).</summary>
    public const string AutoDj = "AUTODJ";
    /// <summary>Una pista que ha deixat de rebre dades del fitxer a mitja pista i s'ha saltat, o la lectura ha fallat (D-157).</summary>
    public const string Stuck = "ENCALLADA";
    /// <summary>Una pista que ha tingut talls de so: el disc o la xarxa no han anat prou ràpid (D-157).</summary>
    public const string Dropouts = "TALLS";
    /// <summary>Silenci a la sortida: una pista en silenci que s'ha saltat, o un silenci que dura (D-158).</summary>
    public const string Silence = "SILENCI";
    /// <summary>Ha sonat una pista d'emergència (una pista no s'ha pogut obrir a temps), o la carpeta no va bé (D-159).</summary>
    public const string Emergency = "EMERGENCIA";
    /// <summary>El motor s'ha reiniciat després d'una aturada que no ha estat voluntària (D-159).</summary>
    public const string Restart = "REINICI";
    /// <summary>Els avisos per correu no es poden enviar (D-160). No s'envia mai per correu.</summary>
    public const string Mail = "CORREU";
    /// <summary>Un stream que no connecta (i s'ha saltat), que s'ha tallat o que s'ha acabat perquè arribava en silenci (D-030, D-175).</summary>
    public const string Stream = "STREAM";

    /// <summary>El nom de cada tipus en català, per als correus (els mateixos que a la interfície, <c>incidents-form.js</c>).</summary>
    public static string Name(string kind) => kind switch
    {
        Output => "Sortida d'àudio",
        File => "Fitxer que no sona",
        Stuck => "Pista encallada",
        Dropouts => "Talls de so",
        Silence => "Silenci a la sortida",
        Emergency => "Pista d'emergència",
        Restart => "Reinici inesperat",
        EventSkipped => "Esdeveniment omès",
        EventError => "Esdeveniment fallat",
        EventLate => "Esdeveniment tard",
        Folder => "Carpeta vigilada",
        AutoDj => "AutoDJ",
        Pause => "Pausa",
        Butt => "Títol per a BUTT",
        Mail => "Avisos per correu",
        Stream => "Stream",
        _ => kind,
    };
}

/// <summary>
/// Incidències (fase 5, pas 1; D-156): el lloc on el motor anota tot el que va malament a l'emissió. N'hi ha de dues menes:
/// <list type="bullet">
/// <item>les que <b>duren</b> (la sortida d'emergència, una carpeta que no es pot revisar…): <see cref="Set"/> les obre
/// quan apareixen i les tanca quan desapareixen. Mentre són obertes, surten a la barra d'avisos;</item>
/// <item>les que <b>passen un moment</b> (una pista saltada, un esdeveniment omès): <see cref="Report"/> les desa ja tancades.
/// Només surten a la llista (i, al pas 5, al correu).</item>
/// </list>
/// Si la base de dades falla, l'emissió no se'n ressent: la incidència es queda a la memòria i al registre tècnic.
/// No s'ha de cridar mai des del fil d'àudio (escriu a la base de dades).
/// </summary>
public sealed class IncidentTracker
{
    private readonly IncidentStore _store;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Lock _sync = new();
    /// <summary>Les obertes, per clau.</summary>
    private readonly Dictionary<string, Incident> _open = [];

    /// <summary>S'ha obert, canviat o tancat una incidència, o se n'ha anotat una de puntual.</summary>
    public event Action<Incident>? Changed;

    /// <summary>
    /// En començar, continua les que havien quedat obertes de l'execució anterior del motor (no es tanquen pel reinici: es
    /// tancaran quan el motor comprovi que el problema ja no hi és). Per això, cada tipus que dura s'ha de tornar a mirar
    /// després d'arrencar (<see cref="Set"/> o <see cref="SetAll"/>).
    /// </summary>
    public IncidentTracker(IncidentStore store, ILogger log, Func<DateTimeOffset>? clock = null)
    {
        _store = store;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        try
        {
            // Les més antigues primer: si n'hi hagués dues de la mateixa clau, es continua la més nova i es tanca l'altra
            foreach (var incident in _store.List(new IncidentFilter { Open = true, Limit = 1000 }).Incidents.Reverse())
            {
                if (_open.TryGetValue(incident.Key, out var older)) _store.Close(older.Id, _clock(), recovered: false);
                _open[incident.Key] = incident;
            }
            if (_open.Count > 0) _log.LogInformation("Incidències: {Count} obertes de l'execució anterior; es continuen", _open.Count);
        }
        catch (Exception ex) when (ex is DatabaseException or SqliteException)
        {
            _log.LogWarning("Incidències: no s'han pogut llegir les de l'execució anterior ({Message})", ex.Message);
        }
    }

    /// <summary>Les obertes, les més greus primer i, a igual gravetat, les més antigues primer.</summary>
    public IReadOnlyList<Incident> Open
    {
        get
        {
            lock (_sync)
                return _open.Values.OrderByDescending(i => i.Severity).ThenBy(i => i.StartedAt).ToList();
        }
    }

    /// <summary>
    /// Una incidència que dura: amb <paramref name="text"/>, s'obre (o, si ja ho era, se n'actualitza el text i la
    /// gravetat); amb null, si era oberta, es tanca.
    /// </summary>
    public void Set(string kind, string key, IncidentSeverity severity, string? text, string? detail = null)
    {
        Incident? changed;
        lock (_sync) changed = SetLocked(kind, key, severity, text, detail);
        if (changed is not null) Notify(changed);
    }

    /// <summary>
    /// Totes les d'un tipus alhora (p. ex. les carpetes vigilades): s'obren o s'actualitzen les de <paramref name="current"/>
    /// (clau → text) i es tanquen les altres obertes d'aquest tipus.
    /// </summary>
    public void SetAll(string kind, IncidentSeverity severity, IReadOnlyDictionary<string, string> current)
    {
        var changed = new List<Incident>();
        lock (_sync)
        {
            var keys = _open.Values.Where(i => i.Kind == kind).Select(i => i.Key).Concat(current.Keys).Distinct().ToList();
            foreach (string key in keys)
            {
                if (SetLocked(kind, key, severity, current.GetValueOrDefault(key), null) is { } incident) changed.Add(incident);
            }
        }
        foreach (var incident in changed) Notify(incident);
    }

    private Incident? SetLocked(string kind, string key, IncidentSeverity severity, string? text, string? detail)
    {
        _open.TryGetValue(key, out var open);
        if (text is null)
        {
            if (open is null) return null;
            _open.Remove(key);
            var closed = open with { EndedAt = _clock() };
            _log.LogInformation("Incidència resolta: {Text}", open.Text);
            Try(() => _store.Close(open.Id, closed.EndedAt!.Value, recovered: false), "tancar");
            return closed;
        }
        if (open is not null)
        {
            if (open.Text == text && open.Severity == severity && open.Detail == detail) return null;
            var updated = open with { Text = text, Severity = severity, Detail = detail };
            _open[key] = updated;
            Try(() => _store.Update(open.Id, severity, text, detail), "actualitzar");
            return updated;
        }
        var incident = new Incident { Kind = kind, Key = key, Severity = severity, Text = text, Detail = detail, StartedAt = _clock() };
        _log.LogWarning("Incidència: {Text}", text);
        long id = 0;
        Try(() => id = _store.Add(incident), "desar");
        incident = incident with { Id = id };
        _open[key] = incident;
        return incident;
    }

    /// <summary>
    /// Una incidència d'un moment: es desa ja tancada. <paramref name="recovered"/>: el motor ho ha resolt sol (p. ex. ha
    /// saltat la pista i l'emissió ha continuat).
    /// </summary>
    public void Report(string kind, string key, IncidentSeverity severity, string text, string? detail = null, bool recovered = false)
    {
        var now = _clock();
        var incident = new Incident
        {
            Kind = kind, Key = key, Severity = severity, Text = text, Detail = detail, StartedAt = now, EndedAt = now, Recovered = recovered,
        };
        _log.LogWarning("Incidència: {Text}", text);
        long id = 0;
        lock (_sync) Try(() => id = _store.Add(incident), "desar");
        Notify(incident with { Id = id });
    }

    /// <summary>Esborra les resoltes de fa més d'un any.</summary>
    public void Purge()
    {
        try
        {
            if (_store.Purge(_clock().AddYears(-1)) is var removed and > 0)
                _log.LogInformation("Incidències: {Count} de fa més d'un any, esborrades", removed);
        }
        catch (Exception ex) when (ex is DatabaseException or SqliteException)
        {
            _log.LogWarning("Incidències: no s'han pogut esborrar les antigues ({Message})", ex.Message);
        }
    }

    private void Try(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) when (ex is DatabaseException or SqliteException)
        {
            _log.LogWarning("Incidències: no s'ha pogut {What} a la base de dades ({Message})", what, ex.Message);
        }
    }

    private void Notify(Incident incident)
    {
        try { Changed?.Invoke(incident); }
        catch (Exception ex) { _log.LogWarning("Incidències: error en avisar ({Message})", ex.Message); }
    }
}
