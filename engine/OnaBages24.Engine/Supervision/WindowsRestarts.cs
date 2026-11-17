using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace OnaBages24.Engine.Supervision;

/// <summary>
/// Els apagats i reinicis de Windows (D-162), per saber per què es va aturar el motor quan no ho va poder deixar escrit.
/// És l'únic lloc lligat al registre d'esdeveniments de Windows (a Linux caldria una altra manera).
/// <list type="bullet">
/// <item>1074 (User32): un apagat o reinici demanat (un usuari, un programa o Windows Update). En un reinici demanat per
/// un usuari n'hi ha dos: el de l'usuari (Explorer) i el del sistema (winlogon);</item>
/// <item>6008 (EventLog) i 41 (Kernel-Power): la vegada anterior Windows no es va tancar bé (tall de llum, penjada).</item>
/// </list>
/// </summary>
public static class WindowsRestarts
{
    /// <summary>Quan ha arrencat Windows.</summary>
    public static DateTimeOffset BootTime() => DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Els del registre «System» entre aquestes hores. Si no es pot llegir, cap (no atura res).</summary>
    public static IReadOnlyList<WindowsShutdownEvent> Read(DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<WindowsShutdownEvent>();
        if (!OperatingSystem.IsWindows()) return list;
        try
        {
            string query = "*[System[(EventID=1074 or EventID=6008 or EventID=41) and TimeCreated[" +
                           $"@SystemTime>='{Utc(from)}' and @SystemTime<='{Utc(to)}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is not { } created) continue;
                    string? process = null, user = null;
                    if (record.Id == 1074 && record.Properties.Count >= 7)
                    {
                        process = record.Properties[0].Value?.ToString();
                        string? account = record.Properties[6].Value?.ToString();
                        user = account?[(account.LastIndexOf('\\') + 1)..];
                    }
                    bool bySystem = record.UserId?.Value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20"; // SYSTEM, LOCAL SERVICE, NETWORK SERVICE
                    list.Add(new WindowsShutdownEvent(record.Id, new DateTimeOffset(created), user, process, bySystem));
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            // Sense el registre de Windows, el text dirà que no se sap per què s'ha apagat
        }
        return list;
    }

    private static string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
