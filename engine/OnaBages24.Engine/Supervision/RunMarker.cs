using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OnaBages24.Engine.Supervision;

/// <summary>Com va acabar la vegada anterior que va funcionar el motor.</summary>
/// <param name="LastAlive">L'última senyal de vida (null si no se sap).</param>
public sealed record UnexpectedStop(DateTimeOffset? LastAlive);

/// <summary>
/// Fitxer «en marxa» (fase 5, pas 4; D-159): mentre el motor funciona, hi escriu cada uns segons l'hora de l'última senyal
/// de vida; quan es tanca bé, l'esborra. Si en arrencar el fitxer encara hi és, la vegada anterior el motor no es va tancar
/// bé (ha petat, s'ha tancat a la força, ha marxat el llum o Windows s'ha reiniciat).
///
/// Si el fitxer és d'un altre motor que encara funciona (dos motors amb la mateixa carpeta de dades, en desenvolupament),
/// no es compta com a aturada.
/// </summary>
public sealed class RunMarker(string path, Func<DateTimeOffset>? clock = null)
{
    public const string FileName = "motor-en-marxa.json";

    /// <summary>Cada quant s'escriu l'última senyal de vida.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(10);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private readonly int _pid = Environment.ProcessId;
    private DateTimeOffset _started;

    /// <summary>
    /// En arrencar: mira si la vegada anterior es va tancar bé (retorna null) o no, i deixa el fitxer amb aquest motor.
    /// Un error de disc no atura res: es considera que es va tancar bé.
    /// </summary>
    public UnexpectedStop? Start(Func<int, bool>? isEngineRunning = null)
    {
        UnexpectedStop? stop = null;
        try
        {
            if (File.Exists(path))
            {
                DateTimeOffset? lastAlive = null;
                int? pid = null;
                try
                {
                    var saved = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    if (saved?["lastAlive"]?.GetValue<string>() is { } text
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) lastAlive = at;
                    pid = saved?["pid"]?.GetValue<int>();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
                bool otherRunning = pid is { } p && p != _pid && (isEngineRunning ?? IsEngineRunning)(p);
                if (!otherRunning) stop = new UnexpectedStop(lastAlive);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _started = _clock();
        Touch();
        return stop;
    }

    /// <summary>Escriu l'última senyal de vida (cada <see cref="TouchInterval"/>). Fora del fil de control (toca el disc).</summary>
    public void Touch()
    {
        try
        {
            var json = new JsonObject
            {
                ["pid"] = _pid,
                ["started"] = _started.ToString("O"),
                ["lastAlive"] = _clock().ToString("O"),
            };
            string temp = path + ".tmp";
            File.WriteAllText(temp, json.ToJsonString());
            File.Move(temp, path, overwrite: true); // sencer o gens: un tall de llum no el deixa a mitges
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>En tancar-se bé: s'esborra el fitxer.</summary>
    public void Stop()
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>El procés <paramref name="pid"/> és un motor d'Ona Bages 24h que encara funciona.</summary>
    private static bool IsEngineRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("OnaBages24.Engine", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// El text de la incidència: «El motor s'ha reiniciat després d'una aturada inesperada: l'última senyal de vida va ser
    /// el 16/11/2026 a les 03:12:40 (ha estat aturat 4 min)».
    /// </summary>
    public static string Text(UnexpectedStop stop, DateTimeOffset now)
    {
        if (stop.LastAlive is not { } last)
            return "El motor s'ha reiniciat després d'una aturada inesperada (no se sap quan es va aturar)";
        var local = last.ToLocalTime();
        var down = now - last;
        return $"El motor s'ha reiniciat després d'una aturada inesperada: l'última senyal de vida va ser el " +
               $"{local.Day}/{local.Month}/{local.Year} a les {local:HH:mm:ss} (ha estat aturat {Span(down)})";
    }

    /// <summary>«menys d'un minut», «4 min», «2 h 5 min», «3 d 4 h».</summary>
    private static string Span(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return "menys d'un minut";
        int min = (int)t.TotalMinutes;
        if (min < 60) return $"{min} min";
        int h = min / 60;
        if (h < 24) return min % 60 == 0 ? $"{h} h" : $"{h} h {min % 60} min";
        int d = h / 24;
        return h % 24 == 0 ? $"{d} d" : $"{d} d {h % 24} h";
    }
}
