using System.Globalization;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Supervision;

/// <summary>
/// Pistes que fallen seguides (incidència del NAS del 20-11-2026): quan una unitat de xarxa desapareix, cada pista de la
/// cua falla a l'instant, i abans cadascuna era una incidència «Fitxer que no sona» (en van sortir 4.819 en dos minuts, i
/// els correus en anaven plens). Ara les primeres (fins a <see cref="QueueSettings.FailuresForEmergency"/> − 1) es
/// continuen anotant una per una, com sempre; a partir d'aquí, totes juntes en <b>una sola incidència greu</b> que dura
/// («No es poden fer sonar les pistes…»), que es va posant al dia i es tanca quan torna a sonar una pista de la cua, amb
/// el resum. Les crides es fan en ordre, una darrere l'altra, fora del fil de control (escriuen a la base de dades).
/// </summary>
public sealed class FailureBurst(IncidentTracker incidents, int threshold, Func<DateTimeOffset>? clock = null)
{
    /// <summary>La clau de la incidència (n'hi ha com a molt una d'oberta).</summary>
    public const string Key = "fitxers-seguits";

    /// <summary>Cada quant es posa al dia el text mentre continuen fallant (no a cada pista: n'hi pot haver desenes per segon).</summary>
    public static readonly TimeSpan UpdateEvery = TimeSpan.FromSeconds(1);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private DateTimeOffset? _since;
    private DateTimeOffset _updated;
    private int _count;
    private string _last = "";

    /// <summary>Una pista de la cua no s'ha pogut fer sonar. Retorna false si és de les primeres (s'anota a part, com sempre).</summary>
    public bool Failed(QueueFailed e)
    {
        if (e.Item.IsStream || e.InRow < threshold) return false;
        var now = _clock(); // l'hora de la fallada, no la de quan es desa
        Post(() =>
        {
            bool first = _since is null;
            _since ??= now;
            _count = e.InRow;
            _last = $"«{e.Item}»: {e.Sentence}";
            if (!first && now - _updated < UpdateEvery) return;
            _updated = now;
            incidents.Set(IncidentKind.File, Key, IncidentSeverity.Danger,
                $"No es poden fer sonar les pistes: des de les {Time(_since.Value)}, {_count} seguides han fallat (l'última, {_last}) "
                + "El disc o la unitat de xarxa de la música potser no responen. Mentrestant sonen pistes d'emergència, si n'hi ha.");
        });
        return true;
    }

    /// <summary>Ha començat una pista: si és de la cua (no d'emergència), la sèrie de fallades s'ha acabat.</summary>
    public void Started(QueueStarted e)
    {
        if (e.Item.Origin == QueueOrigin.Emergency) return;
        var now = _clock();
        Post(() =>
        {
            if (_since is not { } since) return;
            incidents.Set(IncidentKind.File, Key, IncidentSeverity.Danger,
                $"Entre les {Time(since)} i les {Time(now)} no s'han pogut fer sonar {_count} pistes seguides (l'última, {_last}) "
                + $"Ha tornat a sonar «{e.Item}».");
            incidents.Set(IncidentKind.File, Key, IncidentSeverity.Danger, null);
            _since = null;
            _count = 0;
        });
    }

    /// <summary>Espera que s'hagi fet tot el que hi ha pendent (per a les proves).</summary>
    public Task Drain()
    {
        lock (_sync) return _tail;
    }

    /// <summary>Fa la feina fora del fil de qui crida, en ordre: una fallada no pot passar per davant de la pista que ha tornat.</summary>
    private void Post(Action work)
    {
        lock (_sync)
        {
            _tail = _tail.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { } // una incidència que no es pot desar no fa caure res
            }, TaskScheduler.Default);
        }
    }

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
