using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OnaBages24.Engine;

/// <summary>
/// Ordre <c>atura</c> (D-191): l'administrador atura l'aplicació sencera (la interfície i el motor) des d'una consola,
/// sense la consola de desenvolupament. És la manera de fer-ho a la versió portable: la interfície empaquetada no té
/// cap opció per aturar l'emissió (D-162). La crida l'script «Atura Ona Bages 24h.cmd» de l'arrel de la versió portable.
///
/// Ordre de les coses: (1) entrar com a administrador (si hi ha usuaris), (2) confirmar, (3) tancar la interfície a la
/// força (si no, tornaria a engegar el motor) i (4) enviar <c>engine.shutdown</c>. Si la contrasenya no és bona, no es
/// toca res. L'aturada queda al registre: el registre de canvis diu qui l'ha feta i, en tornar a arrencar, la
/// incidència «s'ha aturat amb l'ordre d'aturada» (D-162).
/// </summary>
public static class StopCommand
{
    /// <summary>Les dades de <c>engine.json</c> que calen per parlar amb el motor.</summary>
    public sealed record RuntimeInfo(int Pid, int Port, string Token);

    public static int Run(string[] args)
    {
        if (args.Length > 0) throw new ArgumentException($"Opció desconeguda: {args[0]} (ajuda: OnaBages24.Engine ajuda)");

        string dataFolder = RuntimeFile.DefaultDirectoryPath;
        RuntimeInfo? info = ReadRuntimeFile(Path.Combine(dataFolder, "engine.json"));
        if (info is not null && !ProcessExists(info.Pid)) info = null; // d'una execució que no va acabar bé
        string appFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dataFolder)) ?? dataFolder;
        List<Process> ui = InterfaceProcesses(appFolder);

        if (info is null && ui.Count == 0)
        {
            Console.WriteLine("Ona Bages 24h no està en marxa: no cal aturar res.");
            return 0;
        }
        if (info is null)
        {
            // Només la interfície (el motor ja és aturat): tancar-la no atura cap emissió
            if (!Confirm("El motor ja és aturat. Tanco la interfície?")) return Cancelled();
            CloseInterface(ui);
            return 0;
        }

        using var socket = new ClientWebSocket();
        try
        {
            socket.ConnectAsync(new Uri($"ws://127.0.0.1:{info.Port}/ws?token={Uri.EscapeDataString(info.Token)}"), Timeout(3)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            Console.WriteLine($"El motor està en marxa (procés {info.Pid}) però no respon: no s'hi pot enviar l'ordre d'aturada.");
            Console.WriteLine("Si cal aturar-lo igualment, fes-ho des de l'Administrador de tasques (OnaBages24.Engine.exe).");
            return 1;
        }
        var engine = new Connection(socket);

        // 1. L'administrador (D-164): si hi ha usuaris, cal entrar-hi
        var state = engine.Send("session.get");
        if (state["usersExist"]?.GetValue<bool>() == true)
        {
            Console.WriteLine("Per aturar Ona Bages 24h cal entrar com a administrador.");
            for (int attempt = 1; ; attempt++)
            {
                Console.Write("Usuari: ");
                string user = (Console.ReadLine() ?? "").Trim();
                if (user.Length == 0) return Cancelled();
                string password = ReadHidden("Contrasenya: ");
                try
                {
                    state = engine.Send("session.login", new JsonObject { ["user"] = user, ["password"] = password });
                    break;
                }
                catch (EngineError ex) when (ex.Code == "bad_login" && attempt < 3)
                {
                    Console.WriteLine("L'usuari o la contrasenya no són correctes. Torna-ho a provar.");
                }
                catch (EngineError ex)
                {
                    // Tercer intent fallit, o el nom bloquejat per massa intents (locked)
                    Console.WriteLine($"{ex.Message} No s'ha aturat res.");
                    return 1;
                }
            }
            var session = state["session"];
            if (session?["mustChangePassword"]?.GetValue<bool>() == true)
            {
                Console.WriteLine("Aquesta contrasenya és provisional: canvia-la primer des de la interfície. No s'ha aturat res.");
                return 1;
            }
            if ((string?)session?["role"] != "administrator")
            {
                Console.WriteLine($"Només un administrador pot aturar Ona Bages 24h ({session?["displayName"]} és {((string?)session?["roleName"])?.ToLowerInvariant()}). No s'ha aturat res.");
                return 1;
            }
        }

        // 2. Confirmació
        if (!Confirm("Això atura l'emissió: la ràdio es quedarà en silenci. Segur que vols aturar Ona Bages 24h?"))
        {
            engine.TryLogout();
            return Cancelled();
        }

        // 3. La interfície, a la força: la X de la finestra preguntaria què fer, i si es quedés oberta tornaria a engegar
        //    el motor. No té res per desar (D-162: tot és al motor).
        CloseInterface(ui);

        // 4. El motor
        try
        {
            engine.Send("engine.shutdown");
            Console.WriteLine("El motor ha rebut l'ordre d'aturada.");
        }
        catch (EngineError ex)
        {
            Console.WriteLine($"El motor no ha acceptat l'ordre d'aturada: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // El motor pot tancar la connexió abans de respondre: es mira si s'atura
        }

        if (WaitForExit(info.Pid, TimeSpan.FromSeconds(10)))
        {
            Console.WriteLine("Motor aturat. Ona Bages 24h ja no emet.");
            return 0;
        }
        Console.WriteLine($"El motor encara no s'ha aturat (procés {info.Pid}). Torna-ho a provar d'aquí a una estona.");
        return 1;
    }

    /// <summary>Llegeix <c>engine.json</c>; null si no hi és o no és bo.</summary>
    public static RuntimeInfo? ReadRuntimeFile(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            int? pid = node?["pid"]?.GetValue<int>();
            int? port = node?["port"]?.GetValue<int>();
            string? token = (string?)node?["token"];
            return pid is > 0 && port is > 0 and < 65536 && !string.IsNullOrEmpty(token) ? new RuntimeInfo(pid.Value, port.Value, token) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Si un programa és de la interfície d'aquesta còpia de l'aplicació: és dins la carpeta de l'aplicació i no és el
    /// motor. Així no es tanca la interfície d'una altra còpia (en una altra carpeta).
    /// </summary>
    public static bool IsInterfaceProgram(string? executablePath, string appFolder)
    {
        if (string.IsNullOrEmpty(executablePath)) return false;
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(executablePath).StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileName(executablePath), "OnaBages24.Engine.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Els processos de la interfície (l'aplicació empaquetada, OnaBages24.exe, o Electron en desenvolupament).</summary>
    private static List<Process> InterfaceProcesses(string appFolder)
    {
        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName("OnaBages24").Concat(Process.GetProcessesByName("electron")))
        {
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // d'un altre usuari, o ja acabat
            if (IsInterfaceProgram(path, appFolder)) found.Add(process);
            else process.Dispose();
        }
        return found;
    }

    private static void CloseInterface(List<Process> ui)
    {
        if (ui.Count == 0) return;
        foreach (var process in ui)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } // ja s'ha tancat
        }
        Console.WriteLine("Interfície tancada.");
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return true; // ja no hi és
        }
    }

    private static int Cancelled()
    {
        Console.WriteLine("No s'ha aturat res.");
        return 3;
    }

    /// <summary>Demana confirmació: només «s» (o «si», «sí») és que sí.</summary>
    private static bool Confirm(string question)
    {
        Console.Write($"{question} (s/N) ");
        string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return answer is "s" or "si" or "sí";
    }

    /// <summary>Demana una contrasenya sense que es vegi en escriure-la (si l'entrada no és el teclat, com una línia normal).</summary>
    private static string ReadHidden(string question)
    {
        Console.Write(question);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var text = new StringBuilder();
        for (;;)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
            else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }

    private static CancellationToken Timeout(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>Un error que respon el motor (codi i missatge del protocol).</summary>
    private sealed class EngineError(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// Connexió mínima amb el motor: envia una ordre i n'espera la resposta (els esdeveniments que arribin entremig es
    /// llencen). Sense fils: aquesta ordre fa una cosa darrere l'altra.
    /// </summary>
    private sealed class Connection(ClientWebSocket socket)
    {
        private int _nextId;

        public JsonObject Send(string cmd, JsonObject? parameters = null)
        {
            int id = ++_nextId;
            var message = parameters ?? new JsonObject();
            message["v"] = 1;
            message["id"] = id;
            message["cmd"] = cmd;
            socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, Timeout(5)).GetAwaiter().GetResult();

            var deadline = Timeout(15); // session.login espera 1 s si la contrasenya no és bona
            for (;;)
            {
                var reply = Receive(deadline);
                if (reply is null) throw new WebSocketException("El motor ha tancat la connexió.");
                if (reply["ev"] is not null || (int?)reply["id"] != id) continue; // un esdeveniment
                if (reply["ok"]?.GetValue<bool>() == true) return reply["result"] as JsonObject ?? new JsonObject();
                var error = reply["error"];
                throw new EngineError((string?)error?["code"] ?? "?", (string?)error?["message"] ?? "Error del motor");
            }
        }

        public void TryLogout()
        {
            try { Send("session.logout"); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or EngineError) { }
        }

        private JsonObject? Receive(CancellationToken cancel)
        {
            var buffer = new byte[16 * 1024];
            using var text = new MemoryStream();
            for (;;)
            {
                var result = socket.ReceiveAsync(buffer, cancel).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close) return null;
                text.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return JsonNode.Parse(text.ToArray()) as JsonObject;
        }
    }
}
