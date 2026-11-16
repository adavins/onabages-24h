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
