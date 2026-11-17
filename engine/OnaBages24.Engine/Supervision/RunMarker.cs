using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Supervision;

/// <summary>Per què es va aturar el motor, si ho va poder deixar escrit (D-162).</summary>
public enum StopReason
{
    /// <summary>L'ordre <c>engine.shutdown</c> (manteniment o desenvolupament: la interfície empaquetada no l'ofereix).</summary>
    Command,
    /// <summary>Ctrl+C a la consola.</summary>
    CtrlC,
    /// <summary>Ctrl+Pausa a la consola.</summary>
    CtrlBreak,
    /// <summary>S'ha tancat la finestra de la consola (o un <c>taskkill</c> sense <c>/F</c>).</summary>
    ConsoleClosed,
    /// <summary>Windows s'apaga o es reinicia, i ho ha avisat.</summary>
    WindowsShutdown,
    /// <summary>S'ha tancat bé, però no se sap per què.</summary>
    Other,
}

/// <summary>
/// Com va acabar la vegada anterior que va funcionar el motor. <paramref name="LastAlive"/>: l'última senyal de vida (null
/// si no se sap). <paramref name="Reason"/>: null si no va poder deixar escrit res (ha petat, tancat a la força, tall de
/// llum, Windows s'ha apagat sense avisar-lo).
/// </summary>
public sealed record PreviousStop(DateTimeOffset? LastAlive, StopReason? Reason);

/// <summary>Un apagat o un reinici de Windows, del seu registre (<see cref="WindowsRestarts"/>).</summary>
/// <param name="Id">1074: demanat (per un usuari, un programa o Windows Update); 6008 o 41: no es va tancar bé.</param>
/// <param name="User">Qui l'ha demanat (només el nom, sense el domini), per al 1074.</param>
/// <param name="Process">El programa que l'ha demanat, per al 1074.</param>
/// <param name="BySystem">L'ha demanat el sistema (no un usuari).</param>
public sealed record WindowsShutdownEvent(int Id, DateTimeOffset At, string? User, string? Process, bool BySystem);

/// <summary>
/// Fitxer «en marxa» (fase 5, pas 4; D-159; pas 7, D-162): mentre el motor funciona, hi escriu cada uns segons l'hora de
/// l'última senyal de vida. Quan s'atura, hi deixa escrit el motiu (<see cref="StopReason"/>). En arrencar, el motor no
/// s'atura mai sense motiu (D-162): qualsevol aturada anterior és una incidència, amb el motiu si se sap. Si no n'hi ha cap
/// d'escrit, ha petat o l'han tancat a la força, o Windows s'ha apagat (ho diu el registre de Windows).
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
    private readonly Lock _sync = new();
    private DateTimeOffset _started;
    private StopReason? _stopped;

    /// <summary>El motiu de l'aturada d'aquesta vegada, si ja s'està aturant.</summary>
    public StopReason? Reason { get { lock (_sync) return _stopped; } }

    /// <summary>
    /// En arrencar: com va acabar la vegada anterior (null si és la primera vegada o el fitxer era d'un altre motor que
    /// encara funciona) i deixa el fitxer amb aquest motor. Un error de disc no atura res.
    /// </summary>
    public PreviousStop? Start(Func<int, bool>? isEngineRunning = null)
    {
        PreviousStop? stop = null;
        try
        {
            if (File.Exists(path))
            {
                DateTimeOffset? lastAlive = null;
                int? pid = null;
                StopReason? reason = null;
                try
                {
                    var saved = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    if (saved?["lastAlive"]?.GetValue<string>() is { } text
                        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) lastAlive = at;
                    pid = saved?["pid"]?.GetValue<int>();
                    reason = ParseReason(saved?["stopped"]?.GetValue<string>());
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
                bool otherRunning = reason is null && pid is { } p && p != _pid && (isEngineRunning ?? IsEngineRunning)(p);
                if (!otherRunning) stop = new PreviousStop(lastAlive, reason);
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
        lock (_sync) Write(_stopped);
    }

    /// <summary>
    /// En aturar-se: hi deixa escrit el motiu (la vegada següent serà una incidència d'avís, no greu). Es pot cridar més
    /// d'una vegada (el senyal de la consola i el tancament poden arribar en qualsevol ordre): es queda el més concret, i
    /// <see cref="StopReason.Other" /> només si no n'hi ha cap altre.
    /// </summary>
    public void Stop(StopReason reason)
    {
        lock (_sync)
        {
            if (_stopped is null or StopReason.Other) _stopped = reason;
            Write(_stopped);
        }
    }

    private void Write(StopReason? reason)
    {
        try
        {
            var json = new JsonObject
            {
                ["pid"] = _pid,
                ["started"] = _started.ToString("O"),
                ["lastAlive"] = _clock().ToString("O"),
            };
            if (reason is { } r) json["stopped"] = ReasonText(r);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json.ToJsonString());
            File.Move(temp, path, overwrite: true); // sencer o gens: un tall de llum no el deixa a mitges
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ReasonText(StopReason reason) => reason switch
    {
        StopReason.Command => "ordre",
        StopReason.CtrlC => "ctrl-c",
        StopReason.CtrlBreak => "ctrl-pausa",
        StopReason.ConsoleClosed => "consola-tancada",
        StopReason.WindowsShutdown => "windows",
        _ => "altre",
    };

    private static StopReason? ParseReason(string? text) => text switch
    {
        null => null,
        "ordre" => StopReason.Command,
        "ctrl-c" => StopReason.CtrlC,
        "ctrl-pausa" => StopReason.CtrlBreak,
        "consola-tancada" => StopReason.ConsoleClosed,
        "windows" => StopReason.WindowsShutdown,
        _ => StopReason.Other,
    };

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
    /// La incidència <c>REINICI</c>: la gravetat i el text, segons el motiu (D-162). <paramref name="windowsBoot"/>: quan
    /// ha arrencat Windows; <paramref name="windowsEvents"/>: els apagats i reinicis del registre de Windows des de l'última
    /// senyal de vida. Una aturada demanada (l'ordre, la consola, un reinici de l'ordinador demanat) és un avís; la que no
    /// (ha petat, tall de llum) és greu.
    /// </summary>
    public static (IncidentSeverity Severity, string Text) Describe(PreviousStop stop, DateTimeOffset now, DateTimeOffset? windowsBoot,
        IReadOnlyList<WindowsShutdownEvent> windowsEvents)
    {
        string when = stop.LastAlive is { } last ? $" el {Date(last)}" : "";
        string down = stop.LastAlive is { } l ? $" (ha estat aturat {Span(now - l)})" : "";
        // Windows ha arrencat després de l'última senyal de vida: l'ordinador s'ha apagat o reiniciat
        bool rebooted = windowsBoot is { } boot && stop.LastAlive is { } alive && boot > alive;

        switch (stop.Reason)
        {
            case StopReason.Command:
                return (IncidentSeverity.Warning, $"El motor es va aturar amb l'ordre d'aturada (manteniment o desenvolupament){when}{down}");
            case StopReason.CtrlC:
                return (IncidentSeverity.Warning, $"El motor es va aturar amb Ctrl+C a la consola{when}{down}");
            case StopReason.CtrlBreak:
                return (IncidentSeverity.Warning, $"El motor es va aturar amb Ctrl+Pausa a la consola{when}{down}");
            case StopReason.ConsoleClosed:
                return (IncidentSeverity.Warning, $"El motor es va aturar perquè es va tancar la seva consola{when}{down}");
            case StopReason.Other when !rebooted:
                return (IncidentSeverity.Warning, $"El motor es va aturar sense que se sàpiga per què{when}{down}");
        }

        if (stop.Reason == StopReason.WindowsShutdown || rebooted)
        {
            if (windowsEvents.Any(e => e.Id is 6008 or 41))
                return (IncidentSeverity.Danger, $"L'ordinador es va apagar sense tancar-se bé (tall de llum o penjada){when}{down}");
            var requested = windowsEvents.Where(e => e.Id == 1074).ToList();
            if (requested.FirstOrDefault(e => !e.BySystem && !string.IsNullOrEmpty(e.User)) is { } byUser)
                return (IncidentSeverity.Warning, $"L'ordinador es va apagar o reiniciar (ho va demanar {byUser.User}){when}{down}");
            if (requested.Any(e => IsUpdate(e.Process)))
                return (IncidentSeverity.Warning, $"L'ordinador es va reiniciar per una actualització de Windows{when}{down}");
            if (requested.Count > 0)
                return (IncidentSeverity.Warning, $"L'ordinador es va apagar o reiniciar (ho va demanar Windows){when}{down}");
            return (IncidentSeverity.Danger, $"L'ordinador es va apagar o reiniciar i Windows no diu per què{when}{down}");
        }

        if (stop.LastAlive is not { } lastAlive)
            return (IncidentSeverity.Danger, "El motor s'ha reiniciat després d'una aturada inesperada (no se sap quan es va aturar)");
        return (IncidentSeverity.Danger, $"El motor s'ha reiniciat després d'una aturada inesperada (ha petat o l'han tancat a la força): " +
                                         $"l'última senyal de vida va ser el {Date(lastAlive)}{down}");
    }

    /// <summary>Els programes de Windows que reinicien per les actualitzacions.</summary>
    private static bool IsUpdate(string? process) =>
        process is not null && new[] { "TrustedInstaller", "MoUsoCoreWorker", "UsoClient", "wuauclt", "musnotification" }
            .Any(name => process.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static string Date(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return $"{local.Day}/{local.Month}/{local.Year} a les {local:HH:mm:ss}";
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
