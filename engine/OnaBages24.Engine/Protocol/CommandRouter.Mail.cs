using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Supervision;

namespace OnaBages24.Engine.Protocol;

// Avisos per correu (fase 5, pas 5; D-160): la configuració (amb la contrasenya xifrada) i el correu de prova.
public sealed partial class CommandRouter
{
    private MailNotifier? _mail;

    public const string MailSettingKey = "correu";

    /// <summary>
    /// Si és una ordre que pot tardar (<c>mail.test</c> espera el servidor de correu fins a 30 s): el client la fa
    /// atendre a part, perquè no faci esperar les altres.
    /// </summary>
    public static bool IsSlow(string text)
    {
        if (!text.Contains("mail.test", StringComparison.Ordinal)) return false;
        try { return JsonNode.Parse(text) is JsonObject msg && ReadString(msg["cmd"]) == "mail.test"; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>La configuració desada i la contrasenya xifrada (o null), tal com són a la base de dades.</summary>
    public static (MailSettings Settings, string? ProtectedPassword) ReadMail(Library? library)
    {
        try
        {
            if (library?.GetSetting(MailSettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved)
            {
                static List<string> Strings(JsonNode? node) =>
                    node is JsonArray array ? array.Select(ReadString).OfType<string>().ToList() : [];
                var settings = new MailSettings
                {
                    Enabled = saved["actiu"] is JsonValue a && a.TryGetValue(out bool enabled) && enabled,
                    Host = ReadString(saved["servidor"]) ?? "",
                    Port = saved["port"] is JsonValue p && p.TryGetValue(out int port) ? port : 587,
                    Security = ReadString(saved["xifratge"]) == "cap" ? MailSecurity.None : MailSecurity.StartTls,
                    User = ReadString(saved["usuari"]) ?? "",
                    From = ReadString(saved["remitent"]) ?? "",
                    To = Strings(saved["destinataris"]),
                    ExcludedKinds = saved["exclosos"] is JsonArray ? Strings(saved["exclosos"]) : MailSettings.StandardExcludedKinds,
                };
                return (settings, ReadString(saved["contrasenya"]) is { Length: > 0 } secret ? secret : null);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return (new MailSettings(), null);
    }

    private static void SaveMail(Library library, MailSettings settings, string? protectedPassword) =>
        library.SetSetting(MailSettingKey, new JsonObject
        {
            ["actiu"] = settings.Enabled,
            ["servidor"] = settings.Host,
            ["port"] = settings.Port,
            ["xifratge"] = settings.Security == MailSecurity.None ? "cap" : "starttls",
            ["usuari"] = settings.User,
            ["remitent"] = settings.From,
            ["destinataris"] = new JsonArray(settings.To.Select(a => (JsonNode?)a).ToArray()),
            ["exclosos"] = new JsonArray(settings.ExcludedKinds.Select(k => (JsonNode?)k).ToArray()),
            ["contrasenya"] = protectedPassword,
        }.ToJsonString());

    /// <summary>
    /// Passa la configuració desada als avisos (en arrencar, en desar-la i en restaurar una còpia). Si la contrasenya no
    /// es pot desxifrar (una còpia d'un altre ordinador), els avisos ho diuen i demanen tornar-la a escriure.
    /// </summary>
    public static void ConfigureMail(MailNotifier mail, Library? library)
    {
        var (settings, secret) = ReadMail(library);
        string? password = secret is null ? null : MailSecret.Unprotect(secret);
        string? problem = secret is not null && password is null
            ? "No es pot llegir la contrasenya del correu desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure a ⚙ Configuració."
            : null;
        mail.Configure(settings, password, problem);
    }

    private JsonObject MailGet()
    {
        var mail = _mail ?? throw new BadParamsException("El motor no té avisos per correu.");
        var (settings, secret) = ReadMail(_library);
        return new JsonObject
        {
            ["enabled"] = settings.Enabled,
            ["host"] = settings.Host,
            ["port"] = settings.Port,
            ["security"] = settings.Security == MailSecurity.None ? "none" : "starttls",
            ["user"] = settings.User,
            ["from"] = settings.From,
            ["to"] = new JsonArray(settings.To.Select(a => (JsonNode?)a).ToArray()),
            ["excludedKinds"] = new JsonArray(settings.ExcludedKinds.Select(k => (JsonNode?)k).ToArray()),
            ["standardExcludedKinds"] = new JsonArray(MailSettings.StandardExcludedKinds.Select(k => (JsonNode?)k).ToArray()),
            ["hasPassword"] = secret is not null,
            ["problem"] = mail.Problem,
            ["lastSentAt"] = mail.LastSentAt?.ToString("O"),
        };
    }

    /// <summary>
    /// La configuració desada amb els camps de <paramref name="msg"/> que s'hi envien (els altres no canvien). Comprova
    /// cada camp; la contrasenya va a part.
    /// </summary>
    private MailSettings MergeMail(JsonObject msg)
    {
        var (settings, _) = ReadMail(_library);
        string Text(string name, string current)
        {
            var node = msg[name];
            if (node is null) return current;
            return ReadString(node)?.Trim() ?? throw new BadParamsException($"El camp '{name}' ha de ser un text.");
        }
        if (msg["enabled"] is { } enabledNode)
            settings = settings with
            {
                Enabled = enabledNode is JsonValue ev && ev.TryGetValue(out bool enabled)
                    ? enabled : throw new BadParamsException("El camp 'enabled' ha de ser true o false."),
            };
        settings = settings with { Host = Text("host", settings.Host), User = Text("user", settings.User), From = Text("from", settings.From) };
        if (msg["port"] is { } portNode)
        {
            if (portNode is not JsonValue pv || !pv.TryGetValue(out int port) || port is < 1 or > 65535)
                throw new BadParamsException("El camp 'port' ha de ser un nombre entre 1 i 65535.");
            settings = settings with { Port = port };
        }
        if (msg["security"] is { } securityNode)
            settings = settings with
            {
                Security = ReadString(securityNode) switch
                {
                    "starttls" => MailSecurity.StartTls,
                    "none" => MailSecurity.None,
                    _ => throw new BadParamsException("El camp 'security' ha de ser 'starttls' o 'none'."),
                },
            };
        if (msg["to"] is { } toNode)
        {
            List<string> to = toNode switch
            {
                JsonArray array => array.Select(n => ReadString(n) ?? throw new BadParamsException("El camp 'to' ha de ser una llista d'adreces."))
                    .SelectMany(MailSettings.SplitAddresses).Distinct().ToList(),
                JsonValue => MailSettings.SplitAddresses(ReadString(toNode) ?? throw new BadParamsException("El camp 'to' ha de ser una llista d'adreces.")).ToList(),
                _ => throw new BadParamsException("El camp 'to' ha de ser una llista d'adreces."),
            };
            settings = settings with { To = to };
        }
        if (msg["excludedKinds"] is { } kindsNode)
        {
            if (kindsNode is not JsonArray kinds || kinds.Any(k => ReadString(k) is not { Length: > 0 }))
                throw new BadParamsException("El camp 'excludedKinds' ha de ser una llista de tipus d'incidència.");
            settings = settings with { ExcludedKinds = kinds.Select(k => ReadString(k)!).Distinct().ToList() };
        }
        if (settings.From.Length > 0 && !MailSettings.IsAddress(settings.From))
            throw new BadParamsException($"«{settings.From}» no és una adreça de correu.");
        if (settings.To.FirstOrDefault(a => !MailSettings.IsAddress(a)) is { } wrong)
            throw new BadParamsException($"«{wrong}» no és una adreça de correu.");
        return settings;
    }

    /// <summary>
    /// <c>password</c> de <paramref name="msg"/>: un text = la nova (buit = cap); si no s'envia, la desada (desxifrada).
    /// Retorna si s'ha enviat, i la contrasenya.
    /// </summary>
    private (bool Given, string? Password) MailPassword(JsonObject msg)
    {
        if (msg["password"] is { } node)
        {
            string password = ReadString(node) ?? throw new BadParamsException("El camp 'password' ha de ser un text.");
            return (true, password.Length > 0 ? password : null);
        }
        var (_, secret) = ReadMail(_library);
        if (secret is null) return (false, null);
        return (false, MailSecret.Unprotect(secret)
            ?? throw new BadParamsException("No es pot llegir la contrasenya desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure."));
    }

    /// <summary><c>mail.set</c>: desa la configuració (els camps que no s'envien no canvien) i l'aplica de seguida.</summary>
    private JsonObject MailSet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        var mail = _mail ?? throw new BadParamsException("El motor no té avisos per correu.");
        var settings = MergeMail(msg);
        if (settings.Enabled && settings.Missing() is { } missing) throw new BadParamsException(missing);
        string? secret = ReadMail(library).ProtectedPassword;
        if (msg["password"] is not null)
        {
            var (_, password) = MailPassword(msg);
            secret = password is null ? null : MailSecret.Protect(password);
        }
        SaveMail(library, settings, secret);
        ConfigureMail(mail, library);
        return MailGet();
    }

    /// <summary>
    /// <c>mail.test</c>: envia un correu de prova amb la configuració desada i els camps de <paramref name="msg"/> (el que
    /// hi ha escrit al formulari, encara que no s'hagi desat). Espera la resposta del servidor (com a molt 30 s).
    /// </summary>
    private JsonObject MailTest(JsonObject msg)
    {
        var mail = _mail ?? throw new BadParamsException("El motor no té avisos per correu.");
        var settings = MergeMail(msg);
        if (settings.Missing() is { } missing) throw new BadParamsException(missing);
        var (_, password) = MailPassword(msg);
        string? error = mail.SendTestAsync(settings, password).GetAwaiter().GetResult();
        return new JsonObject { ["sent"] = error is null, ["error"] = error };
    }
}
