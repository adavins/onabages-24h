using OnaBages24.Data;
using OnaBages24.Engine.LibraryTools;

namespace OnaBages24.Engine.Supervision;

/// <summary>
/// Què és una incidència (fase 5, pas 1; D-156): quines línies del registre del planificador ho són i els textos de les
/// carpetes vigilades. Separat del motor perquè es pugui provar sol.
/// </summary>
public static class IncidentRules
{
    /// <summary>
    /// Un esdeveniment a l'hora exacta que comença més tard que això és una incidència. Normalment comença a la mostra
    /// prevista (es prepara 30 s abans, D-008): 2 s tard vol dir que alguna cosa ha anat malament de debò.
    /// </summary>
    public const double LateThresholdMs = 2000;

    /// <summary>La incidència d'una línia del registre d'execució, o null si no n'és cap.</summary>
    /// <remarks>
    /// No en són: els omesos per un conflicte (en sonava un de més prioritat), pel canvi d'hora o perquè els esdeveniments
    /// estaven aturats a mà; ni el contingut d'un ENCUA tret de la cua: tot això és volgut.
    /// </remarks>
    public static (string Kind, IncidentSeverity Severity, string Text)? FromExecution(ExecutionRecord record)
    {
        string name = $"«{record.Name}» de les {TimeZoneInfo.ConvertTime(record.ScheduledFor, TimeZoneInfo.Local):HH:mm}";
        string detail = string.IsNullOrWhiteSpace(record.Detail) ? "" : $": {record.Detail}";
        return record.Result switch
        {
            ExecutionResult.SkippedLate => (IncidentKind.EventSkipped, IncidentSeverity.Warning, $"L'esdeveniment {name} no s'ha fet{detail}"),
            ExecutionResult.Error => (IncidentKind.EventError, IncidentSeverity.Danger, $"L'esdeveniment {name} ha fallat{detail}"),
            ExecutionResult.Ok when record.StartDelayMs > LateThresholdMs =>
                (IncidentKind.EventLate, IncidentSeverity.Warning,
                    $"L'esdeveniment {name} ha començat {record.StartDelayMs.Value / 1000:0.0} s tard"),
            _ => null,
        };
    }

    /// <summary>«m:ss» o «h:mm:ss».</summary>
    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>El text d'una pista encallada (D-157): s'ha saltat, o la lectura ha fallat i s'ha acabat abans d'hora.</summary>
    public static string StuckText(string track, TimeSpan position, string? error) => error is null
        ? $"«{track}» s'ha encallat a {Clock(position)}: el fitxer ha deixat de llegir-se (disc o xarxa) i s'ha passat a la següent"
        : $"«{track}» s'ha acabat abans d'hora a {Clock(position)}: ha fallat la lectura del fitxer ({error})";

    /// <summary>El text d'una pista d'emergència que ha sonat perquè la que tocava no s'obria (D-159).</summary>
    public static string EmergencyText(string waiting, string emergency) =>
        $"«{waiting}» no s'ha pogut obrir a temps (disc o xarxa lents): ha sonat «{emergency}», del disc local";

    /// <summary>El text d'una pista que sonava en silenci i s'ha saltat (D-158).</summary>
    public static string SilenceSkipText(string track, TimeSpan position, double seconds) =>
        $"«{track}» sonava en silenci a {Clock(position)} (més de {seconds:0} s sense so) i s'ha passat a la següent";

    /// <summary>El text d'una pista que ha tingut talls (D-157).</summary>
    public static string DropoutsText(string track, int dropouts) =>
        $"«{track}» ha tingut talls de so ({dropouts} {(dropouts == 1 ? "vegada" : "vegades")} sense dades): el disc o la xarxa no han anat prou ràpid";

    /// <summary>Les carpetes vigilades amb problemes (clau <c>carpeta:id</c> → text), com les mostrava la interfície.</summary>
    public static Dictionary<string, string> Folders(IEnumerable<FolderWatchStatus> folders)
    {
        var result = new Dictionary<string, string>();
        foreach (var f in folders)
        {
            string? text = f.Problem is { } problem ? $"Carpeta vigilada de «{f.Name}»: {problem}"
                : f.Missing > 0 ? $"Carpeta vigilada de «{f.Name}»: {f.Missing} {(f.Missing == 1 ? "pista té" : "pistes tenen")} el fitxer que falta"
                : null;
            if (text is not null) result[$"carpeta:{f.SubcategoryId}"] = text;
        }
        return result;
    }
}
