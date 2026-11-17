using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace OnaBages24.Engine.Supervision;

/// <summary>
/// L'enviament de debò, amb <c>System.Net.Mail</c> (porta .NET; D-155). Només STARTTLS (port 587) o sense xifrar: la
/// biblioteca no fa SSL directe (port 465).
/// </summary>
public static class SmtpMail
{
    /// <summary>Envia un correu de text pla. Llança l'error tal com ve: <see cref="Describe"/> el posa en català.</summary>
    public static async Task SendAsync(MailSettings settings, string? password, string subject, string body, CancellationToken ct)
    {
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.Security == MailSecurity.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Timeout = (int)MailNotifier.SendTimeout.TotalMilliseconds,
        };
        if (settings.User.Length > 0) client.Credentials = new NetworkCredential(settings.User, password ?? "");
        using var message = new MailMessage
        {
            From = new MailAddress(settings.Sender, "Ona Bages 24h"),
            Subject = subject,
            Body = body,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };
        foreach (string to in settings.To) message.To.Add(to);
        await client.SendMailAsync(message, ct);
    }

    /// <summary>Què ha passat, en català i amb el que cal fer (els errors de xarxa i del servidor venen en anglès).</summary>
    public static string Describe(Exception ex, MailSettings settings)
    {
        if (ex is OperationCanceledException or TimeoutException)
            return $"El servidor de correu «{settings.Host}» no ha respost a temps{(settings.Port == 465 ? " (amb el port 465 no funciona: fes servir el 587)" : "")}.";
        if (ex is SmtpFailedRecipientException recipient)
            return $"El servidor no accepta el destinatari «{recipient.FailedRecipient?.Trim('<', '>')}».";

        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                        $"No es troba el servidor «{settings.Host}»: comprova el nom i la connexió a internet.",
                    SocketError.ConnectionRefused => $"El servidor «{settings.Host}» no accepta connexions al port {settings.Port}.",
                    SocketError.TimedOut => $"El servidor de correu «{settings.Host}» no ha respost a temps.",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                        $"No hi ha connexió amb el servidor «{settings.Host}»: comprova la connexió a internet.",
                    _ => $"Error de xarxa amb el servidor «{settings.Host}» ({socket.Message}).",
                };
            }
            if (inner is AuthenticationException)
                return "No s'ha pogut establir la connexió xifrada amb el servidor: comprova el port i el xifratge.";
        }

        if (ex is SmtpException smtp)
        {
            int code = (int)smtp.StatusCode;
            string answer = Answer(smtp.Message);
            if (code == 530 && settings.Security == MailSecurity.None)
                return "El servidor demana una connexió xifrada: tria STARTTLS.";
            if (code is 530 or 534 or 535)
                return "El servidor no ha acceptat l'usuari o la contrasenya (amb Gmail cal una contrasenya d'aplicació)." + answer;
            if (code is 550 or 551 or 553 or 554)
                return "El servidor no ha acceptat alguna de les adreces." + answer;
            if (smtp.StatusCode == SmtpStatusCode.GeneralFailure)
                return $"No s'ha pogut connectar amb el servidor «{settings.Host}» al port {settings.Port}." + answer;
            return "El servidor de correu ha respost amb un error." + answer;
        }
        return ex.Message;
    }

    /// <summary>La resposta del servidor, entre parèntesis, per si cal buscar-la.</summary>
    private static string Answer(string message) =>
        string.IsNullOrWhiteSpace(message) ? "" : $" (resposta del servidor: {message.Trim()})";
}
