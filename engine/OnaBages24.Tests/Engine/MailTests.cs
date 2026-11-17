using System.Net.Mail;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Avisos per correu (fase 5, pas 5; D-160), amb un enviador fals i un rellotge que mouen les proves.</summary>
public sealed class MailTests : IDisposable
{
    private sealed record Sent(MailSettings Settings, string? Password, string Subject, string Body);

    private readonly TempFolder _temp = new();
    private readonly List<Sent> _sent = [];
    private DateTimeOffset _now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private Exception? _fail;

    private static readonly MailSettings Ready = new()
    {
        Enabled = true, Host = "smtp.exemple.cat", User = "avisos@exemple.cat", To = ["tecnic@exemple.cat"],
    };

    public void Dispose() => _temp.Dispose();

    private MailNotifier Notifier(MailSettings? settings = null)
    {
        var mail = new MailNotifier(Send, () => _now, () => "Ràdio Prova");
        mail.Configure(settings ?? Ready, "secret", null);
        return mail;
    }

    private Task Send(MailSettings settings, string? password, string subject, string body, CancellationToken ct)
    {
        if (_fail is { } fail) throw fail;
        _sent.Add(new Sent(settings, password, subject, body));
        return Task.CompletedTask;
    }

    private Incident Incident(string kind, string key, IncidentSeverity severity = IncidentSeverity.Warning, string text = "Alguna cosa",
        DateTimeOffset? started = null, DateTimeOffset? ended = null, bool recovered = false) =>
        new() { Kind = kind, Key = key, Severity = severity, Text = text, StartedAt = started ?? _now, EndedAt = ended, Recovered = recovered };

    private async Task After(MailNotifier mail, TimeSpan span)
    {
        _now += span;
        await mail.TickAsync();
    }

    [Fact]
    public async Task Les_incidencies_que_arriben_alhora_van_en_un_sol_correu_al_cap_de_2_minuts()
    {
        var mail = Notifier();
        mail.Add(Incident(IncidentKind.File, "fitxer:a", text: "No s'ha pogut obrir a.mp3", ended: _now, recovered: true));
        await After(mail, TimeSpan.FromSeconds(30));
        mail.Add(Incident(IncidentKind.Folder, "carpeta:1", text: "La carpeta O:\\Moixiganga no hi és"));
        await After(mail, TimeSpan.FromSeconds(60));
        Assert.Empty(_sent); // encara no fa 2 minuts de la primera

        await After(mail, TimeSpan.FromSeconds(31));
        var sent = Assert.Single(_sent);
        Assert.Equal("[Ràdio Prova] 2 avisos: Fitxer que no sona, Carpeta vigilada", sent.Subject);
        Assert.Contains("No s'ha pogut obrir a.mp3 (el motor ho ha resolt sol)", sent.Body);
        Assert.Contains("La carpeta O:\\Moixiganga no hi és (continua oberta)", sent.Body);
        Assert.Equal("secret", sent.Password);

        await After(mail, TimeSpan.FromMinutes(5));
        Assert.Single(_sent); // no es torna a enviar
    }

    [Fact]
    public async Task Com_a_molt_un_correu_cada_10_minuts_excepte_si_n_hi_ha_una_de_greu()
    {
        var mail = Notifier();
        mail.Add(Incident(IncidentKind.File, "fitxer:a", ended: _now));
        await After(mail, TimeSpan.FromMinutes(2));
        Assert.Single(_sent);

        mail.Add(Incident(IncidentKind.File, "fitxer:b", ended: _now));
        await After(mail, TimeSpan.FromMinutes(3));
        Assert.Single(_sent); // fa 3 minuts de l'últim

        mail.Add(Incident(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "S'emet per la sortida d'emergència"));
        await After(mail, TimeSpan.FromMinutes(1));
        Assert.Single(_sent); // la greu també espera els 2 minuts d'agrupar
        await After(mail, TimeSpan.FromMinutes(1));
        Assert.Equal(2, _sent.Count); // però no els 10
        Assert.StartsWith("[Ràdio Prova] 1 greu, 1 avís: Sortida d'àudio, Fitxer que no sona", _sent[1].Subject);

        mail.Add(Incident(IncidentKind.File, "fitxer:c", ended: _now));
        await After(mail, TimeSpan.FromMinutes(9));
        Assert.Equal(2, _sent.Count);
        await After(mail, TimeSpan.FromMinutes(1));
        Assert.Equal(3, _sent.Count); // 10 minuts després de l'últim
    }

    [Fact]
    public async Task Quan_es_resol_una_que_s_ha_enviat_s_avisa_amb_la_durada()
    {
        var mail = Notifier();
        var open = Incident(IncidentKind.Silence, "silenci", IncidentSeverity.Danger, "Fa 15 s que no surt so");
        mail.Add(open);
        await After(mail, TimeSpan.FromMinutes(2));
        Assert.Single(_sent);

        mail.Add(open with { Text = "Fa 60 s que no surt so" }); // només canvia el text: no s'envia
        await After(mail, TimeSpan.FromMinutes(15));
        Assert.Single(_sent);

        mail.Add(open with { Text = "Fa 60 s que no surt so", EndedAt = _now });
        await After(mail, TimeSpan.FromMinutes(2));
        Assert.Equal(2, _sent.Count);
        Assert.Equal("[Ràdio Prova] 1 resolta: Silenci a la sortida", _sent[1].Subject);
        Assert.Contains("RESOLTES", _sent[1].Body);
        Assert.Contains("(ha durat 17 min)", _sent[1].Body);
    }

    [Fact]
    public async Task Una_que_s_obre_i_es_resol_abans_d_enviar_surt_en_una_sola_linia()
    {
        var mail = Notifier();
        var open = Incident(IncidentKind.Output, "sortida", IncidentSeverity.Danger, "No hi ha cap sortida");
        mail.Add(open);
        _now += TimeSpan.FromSeconds(40);
        mail.Add(open with { EndedAt = _now });
        await After(mail, TimeSpan.FromMinutes(2));

        var sent = Assert.Single(_sent);
        Assert.Contains("No hi ha cap sortida (s'ha resolt en 40 s)", sent.Body);
        Assert.DoesNotContain("RESOLTES", sent.Body);
        Assert.Contains("Ara no hi ha cap incidència oberta.", sent.Body);

        await After(mail, TimeSpan.FromMinutes(15));
        Assert.Single(_sent); // la resolució ja ha anat amb aquest correu
    }

    [Fact]
    public async Task Els_tipus_exclosos_el_correu_mateix_i_els_avisos_desactivats_no_s_envien()
    {
        var mail = Notifier();
        mail.Add(Incident(IncidentKind.Pause, "pausa", ended: _now)); // exclosa per defecte
        mail.Add(Incident(IncidentKind.Dropouts, "fitxer:a", ended: _now)); // exclosa per defecte
        mail.Add(Incident(IncidentKind.Mail, "correu")); // no arribaria
        await After(mail, TimeSpan.FromMinutes(5));
        Assert.Empty(_sent);

        mail.Configure(Ready with { ExcludedKinds = [] }, "secret", null);
        mail.Add(Incident(IncidentKind.Pause, "pausa", ended: _now));
        mail.Configure(Ready with { Enabled = false }, "secret", null); // es desactiva: els pendents es descarten
        mail.Add(Incident(IncidentKind.File, "fitxer:b", ended: _now));
        await After(mail, TimeSpan.FromMinutes(5));
        Assert.Empty(_sent);
        Assert.Null(mail.Problem);
    }

    [Fact]
    public async Task Si_no_es_pot_enviar_es_torna_a_provar_i_ho_diu()
    {
        var mail = Notifier();
        _fail = new SmtpException((SmtpStatusCode)535, "5.7.8 Username and Password not accepted");
        mail.Add(Incident(IncidentKind.File, "fitxer:a", ended: _now));
        await After(mail, TimeSpan.FromMinutes(2));
        Assert.Empty(_sent);
        Assert.StartsWith("No s'ha pogut enviar el correu d'avís: El servidor no ha acceptat l'usuari o la contrasenya", mail.Problem);

        await After(mail, TimeSpan.FromSeconds(30));
        Assert.Empty(_sent); // espera 1 minut

        _fail = null;
        await After(mail, TimeSpan.FromSeconds(30));
        Assert.Single(_sent);
        Assert.Null(mail.Problem);
    }

    [Fact]
    public void Sense_contrasenya_llegible_o_amb_la_configuracio_incompleta_ho_diu()
    {
        var mail = Notifier();
        mail.Configure(Ready, null, "No es pot llegir la contrasenya del correu desada");
        Assert.Equal("No es pot llegir la contrasenya del correu desada", mail.Problem);
        mail.Configure(Ready with { To = [] }, "secret", null);
        Assert.Equal("Avisos per correu: Falta algun destinatari.", mail.Problem);
    }

    [Fact]
    public void El_text_de_les_durades_i_dels_errors_de_xarxa()
    {
        Assert.Equal("40 s", MailNotifier.Duration(TimeSpan.FromSeconds(40)));
        Assert.Equal("7 min", MailNotifier.Duration(TimeSpan.FromMinutes(7.5)));
        Assert.Equal("2 h 5 min", MailNotifier.Duration(TimeSpan.FromMinutes(125)));
        Assert.Equal("3 dies", MailNotifier.Duration(TimeSpan.FromHours(80)));

        var notFound = new SmtpException("Failure sending mail.", new SocketException((int)SocketError.HostNotFound));
        Assert.Equal("No es troba el servidor «smtp.exemple.cat»: comprova el nom i la connexió a internet.", SmtpMail.Describe(notFound, Ready));
        Assert.Contains("port 465", SmtpMail.Describe(new OperationCanceledException(), Ready with { Port = 465 }));
        Assert.Equal("El servidor demana una connexió xifrada: tria STARTTLS.",
            SmtpMail.Describe(new SmtpException(SmtpStatusCode.MustIssueStartTlsFirst, "5.7.0 Must issue a STARTTLS command first"), Ready with { Security = MailSecurity.None }));
    }

    [Fact]
    public void La_contrasenya_es_desa_xifrada_i_no_surt_mai_del_motor()
    {
        using var db = Database.Open(_temp.File("ob24.db"));
        var library = new Library(db);
        var mail = new MailNotifier(Send, () => _now);
        var router = new CommandRouter(new EngineState(), () => { }, library, mail: mail);
        JsonObject Send2(string json) => router.Handle(json);

        var get = Send2("""{ "v": 1, "id": 1, "cmd": "mail.get" }""")["result"]!;
        Assert.False(get["enabled"]!.GetValue<bool>());
        Assert.Equal(587, get["port"]!.GetValue<int>());
        Assert.Equal(new[] { "PAUSA", "TALLS" }, get["excludedKinds"]!.AsArray().Select(k => k!.GetValue<string>()));

        var set = Send2("""
            { "v": 1, "id": 2, "cmd": "mail.set", "enabled": true, "host": "smtp.gmail.com", "user": "avisos@exemple.cat",
              "password": "abcd efgh ijkl mnop", "to": "tecnic@exemple.cat, director@exemple.cat", "excludedKinds": ["PAUSA"] }
            """)["result"]!;
        Assert.True(set["hasPassword"]!.GetValue<bool>());
        Assert.Null(set["password"]);
        Assert.Equal(2, set["to"]!.AsArray().Count);
        string saved = library.GetSetting(CommandRouter.MailSettingKey)!;
        Assert.DoesNotContain("abcd efgh", saved); // xifrada
        Assert.True(mail.Settings.Enabled);
        Assert.Equal(new[] { "PAUSA" }, mail.Settings.ExcludedKinds);

        // Sense 'password', es conserva; la del correu de prova és la desada (desxifrada)
        Send2("""{ "v": 1, "id": 3, "cmd": "mail.set", "port": 2525 }""");
        Assert.True(CommandRouter.ReadMail(library).ProtectedPassword is not null);
        var test = Send2("""{ "v": 1, "id": 4, "cmd": "mail.test", "to": ["prova@exemple.cat"] }""")["result"]!;
        Assert.True(test["sent"]!.GetValue<bool>());
        var sent = Assert.Single(_sent);
        Assert.Equal("abcd efgh ijkl mnop", sent.Password);
        Assert.Equal(new[] { "prova@exemple.cat" }, sent.Settings.To); // el del formulari, encara que no s'hagi desat
        Assert.Equal(2525, sent.Settings.Port);
        Assert.Equal(2, CommandRouter.ReadMail(library).Settings.To.Count);

        // Una còpia d'un altre ordinador: la contrasenya no es pot desxifrar
        var copy = JsonNode.Parse(library.GetSetting(CommandRouter.MailSettingKey)!)!.AsObject();
        copy["contrasenya"] = "AAAA";
        library.SetSetting(CommandRouter.MailSettingKey, copy.ToJsonString());
        CommandRouter.ConfigureMail(mail, library);
        Assert.StartsWith("No es pot llegir la contrasenya del correu desada", mail.Problem);

        string Error(string json) => Send2(json)["error"]!["code"]!.GetValue<string>();
        Assert.Equal("bad_params", Error("""{ "v": 1, "id": 5, "cmd": "mail.set", "to": "no-es-una-adreca" }"""));
        Assert.Equal("bad_params", Error("""{ "v": 1, "id": 6, "cmd": "mail.set", "host": "" }""")); // actiu sense servidor
        Assert.Equal("bad_params", Error("""{ "v": 1, "id": 7, "cmd": "mail.set", "security": "ssl" }"""));
        Assert.Equal("bad_params", Error("""{ "v": 1, "id": 8, "cmd": "mail.set", "port": 70000 }"""));
    }

    [Fact]
    public void El_correu_de_prova_diu_l_error_en_catala()
    {
        using var db = Database.Open(_temp.File("ob24.db"));
        var mail = new MailNotifier(Send, () => _now);
        var router = new CommandRouter(new EngineState(), () => { }, new Library(db), mail: mail);
        _fail = new SmtpException("Failure sending mail.", new SocketException((int)SocketError.ConnectionRefused));
        var test = router.Handle("""
            { "v": 1, "id": 1, "cmd": "mail.test", "host": "smtp.exemple.cat", "from": "avisos@exemple.cat", "to": ["tecnic@exemple.cat"] }
            """)["result"]!;
        Assert.False(test["sent"]!.GetValue<bool>());
        Assert.Equal("El servidor «smtp.exemple.cat» no accepta connexions al port 587.", test["error"]!.GetValue<string>());
    }

    [Fact]
    public void La_contrasenya_xifrada_es_pot_tornar_a_llegir()
    {
        string secret = MailSecret.Protect("contrasenya d'aplicació");
        Assert.NotEqual("contrasenya d'aplicació", secret);
        Assert.Equal("contrasenya d'aplicació", MailSecret.Unprotect(secret));
        Assert.Null(MailSecret.Unprotect("no és base64"));
    }
}
