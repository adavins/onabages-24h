using System.Net.Mail;

namespace OnaBages24.Engine.Supervision;

/// <summary>Com es connecta al servidor de correu.</summary>
public enum MailSecurity
{
    /// <summary>STARTTLS (el normal, port 587: Gmail, Outlook…).</summary>
    StartTls,
    /// <summary>Sense xifrar: només per a un servidor de la xarxa local.</summary>
    None,
}

/// <summary>
/// La configuració dels avisos per correu (fase 5, pas 5; D-160). Es desa a la configuració (<c>correu</c>) amb la
/// contrasenya xifrada amb DPAPI (<see cref="MailSecret"/>); aquí la contrasenya no hi és mai.
/// </summary>
public sealed record MailSettings
{
    /// <summary>Els tipus que no s'envien si no es diu el contrari: la pausa és voluntària i dels talls en sortirien massa.</summary>
    public static readonly IReadOnlyList<string> StandardExcludedKinds = [IncidentKind.Pause, IncidentKind.Dropouts];

    public bool Enabled { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public MailSecurity Security { get; init; } = MailSecurity.StartTls;
    public string User { get; init; } = "";
    /// <summary>L'adreça del remitent; si és buida, l'usuari.</summary>
    public string From { get; init; } = "";
    public IReadOnlyList<string> To { get; init; } = [];
    /// <summary>Els tipus d'incidència que no s'envien (així, un tipus nou s'envia sense haver-lo de marcar).</summary>
    public IReadOnlyList<string> ExcludedKinds { get; init; } = StandardExcludedKinds;

    /// <summary>El remitent de debò: <see cref="From"/> o, si és buit, l'usuari.</summary>
    public string Sender => From.Length > 0 ? From : User;

    /// <summary>Si aquest tipus s'envia. Els problemes del correu mateix no (no arribarien).</summary>
    public bool Sends(string kind) => kind != IncidentKind.Mail && !ExcludedKinds.Contains(kind);

    /// <summary>Què falta o no va bé per poder enviar (null si es pot provar d'enviar).</summary>
    public string? Missing()
    {
        if (Host.Length == 0) return "Falta el servidor de correu.";
        if (Port is < 1 or > 65535) return "El port ha de ser un nombre entre 1 i 65535.";
        if (!IsAddress(Sender)) return "Falta l'adreça del remitent (o l'usuari ha de ser una adreça de correu).";
        if (To.Count == 0) return "Falta algun destinatari.";
        if (To.FirstOrDefault(a => !IsAddress(a)) is { } wrong) return $"«{wrong}» no és una adreça de correu.";
        return null;
    }

    /// <summary>Si és una adreça de correu (nom@domini).</summary>
    public static bool IsAddress(string text) =>
        text.Contains('@') && MailAddress.TryCreate(text, out var address) && address.Address == text;

    /// <summary>Una llista d'adreces escrites amb comes, punts i coma o salts de línia.</summary>
    public static IReadOnlyList<string> SplitAddresses(string text) =>
        text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}

/// <summary>
/// La contrasenya del correu, xifrada amb DPAPI de Windows (D-155): només la pot desxifrar el mateix usuari al mateix
/// ordinador. Una còpia de la base de dades no la deixa llegir. És l'únic lloc lligat a Windows dels avisos per correu
/// (a Linux caldria una altra manera de desar-la).
/// </summary>
public static class MailSecret
{
    /// <summary>La contrasenya xifrada, en Base64.</summary>
    public static string Protect(string password) => Security.Dpapi.Protect(password, "correu");

    /// <summary>La contrasenya, o null si no es pot desxifrar (una còpia d'un altre ordinador o d'un altre usuari).</summary>
    public static string? Unprotect(string protectedText) => Security.Dpapi.Unprotect(protectedText, "correu");
}
