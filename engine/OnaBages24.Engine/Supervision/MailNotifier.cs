using System.Globalization;
using System.Text;
using OnaBages24.Data;

namespace OnaBages24.Engine.Supervision;

/// <summary>Envia un correu (el de debò és <see cref="SmtpMail.SendAsync"/>; les proves en fan servir un de fals).</summary>
public delegate Task MailSend(MailSettings settings, string? password, string subject, string body, CancellationToken ct);

/// <summary>Un correu ja escrit.</summary>
public sealed record MailText(string Subject, string Body);

/// <summary>Una incidència dins d'un correu: <see cref="New"/> si és nova (si no, s'hi posa perquè s'ha resolt).</summary>
public sealed record MailItem(Incident Incident, bool New);

/// <summary>
/// Avisos per correu (fase 5, pas 5; D-160). Rep totes les incidències (<see cref="Add"/>) i les envia agrupades:
/// <list type="bullet">
/// <item>el primer correu surt <see cref="GroupWait"/> després de la primera incidència nova, perquè les que arriben
/// alhora (p. ex. quan cau el NAS) vagin juntes;</item>
/// <item>després, com a molt un correu cada <see cref="MinInterval"/>, excepte si n'hi ha una de greu de nova;</item>
/// <item>les resoltes van al correu següent amb quanta estona han durat;</item>
/// <item>si no es pot enviar, es torna a provar al cap d'1, 5 i 15 minuts (i després cada 15) i <see cref="Problem"/>
/// ho diu (a la barra d'avisos, no per correu).</item>
/// </list>
/// Els avisos pendents són a la memòria: si el motor es reinicia es perden (el reinici inesperat sí que s'avisa).
/// L'enviament és en un fil a part i amb un temps màxim: el servidor de correu no pot afectar mai l'emissió.
/// </summary>
public sealed class MailNotifier
{
    public static readonly TimeSpan GroupWait = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RetryWaits = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
    /// <summary>Com a molt tants avisos esperant; els altres només es compten.</summary>
    public const int MaxPending = 200;

    private sealed class Entry
    {
        public required Incident Incident { get; set; }
        public required bool New { get; set; }
        public required DateTimeOffset AddedAt { get; init; }
    }

    private readonly MailSend _send;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string> _stationName;
    private readonly Func<IReadOnlyList<Incident>> _openNow;
    private readonly ILogger? _log;
    private readonly DateTimeOffset _startedAt;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Entry> _pending = [];
    /// <summary>Les obertes que ja s'han enviat: quan es resolguin, també s'avisarà.</summary>
    private readonly HashSet<string> _mailed = [];
    private MailSettings _settings = new();
    private string? _password;
    private string? _passwordProblem;
    private bool _sending;
    private int _failures;
    private DateTimeOffset? _retryAt;
    private string? _sendProblem;
    private int _dropped;

    public MailNotifier(MailSend send, Func<DateTimeOffset>? clock = null, Func<string>? stationName = null,
        Func<IReadOnlyList<Incident>>? openNow = null, ILogger? log = null)
    {
        _send = send;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _stationName = stationName ?? (() => "");
        _openNow = openNow ?? (() => []);
        _log = log;
        _startedAt = _clock();
    }

    public MailSettings Settings { get { lock (_sync) return _settings; } }

    /// <summary>Quan s'ha enviat l'últim correu d'avís (null si encara cap des que funciona el motor).</summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Què no va bé amb el correu (null si res, o si els avisos estan desactivats): per a la incidència <c>CORREU</c>.</summary>
    public string? Problem
    {
        get
        {
            lock (_sync)
            {
                if (!_settings.Enabled) return null;
                if (_passwordProblem is { } p) return p;
                if (_settings.Missing() is { } missing) return $"Avisos per correu: {missing}";
                return _sendProblem;
            }
        }
    }

    /// <summary>
    /// La configuració nova. <paramref name="password"/>: desxifrada (null si no n'hi ha); <paramref name="passwordProblem"/>:
    /// si n'hi ha una de desada que no es pot desxifrar.
    /// </summary>
    public void Configure(MailSettings settings, string? password, string? passwordProblem)
    {
        lock (_sync)
        {
            _settings = settings;
            _password = password;
            _passwordProblem = passwordProblem;
            // Amb una configuració nova, es prova de seguida (sense esperar el reintent)
            _failures = 0;
            _retryAt = null;
            _sendProblem = null;
            if (!settings.Enabled)
            {
                _pending.Clear();
                _dropped = 0;
            }
        }
    }

    /// <summary>Una incidència nova, canviada o resolta (<see cref="IncidentTracker.Changed"/>). Des de qualsevol fil.</summary>
    public void Add(Incident incident)
    {
        var now = _clock();
        lock (_sync)
        {
            if (!_settings.Enabled || !_settings.Sends(incident.Kind)) return;
            string id = Identity(incident);
            if (_pending.TryGetValue(id, out var entry))
            {
                entry.Incident = incident; // la mateixa: el text nou, o ja resolta
                return;
            }
            bool isNew = incident.IsOpen ? !_mailed.Contains(id) : incident.EndedAt == incident.StartedAt;
            // Una resolta s'avisa si se n'havia avisat l'obertura (o és d'abans que el motor arrenqués: potser sí)
            bool resolved = !incident.IsOpen && !isNew && (_mailed.Contains(id) || incident.StartedAt < _startedAt);
            if (!isNew && !resolved) return; // només ha canviat el text d'una que ja s'ha enviat
            if (_pending.Count >= MaxPending)
            {
                _dropped++;
                return;
            }
            _pending[id] = new Entry { Incident = incident, New = isNew, AddedAt = now };
        }
    }

    /// <summary>Com es reconeix una incidència entre els seus canvis (l'id pot ser 0 si la base de dades ha fallat).</summary>
    private static string Identity(Incident incident) =>
        $"{incident.Kind}|{incident.Key}|{incident.StartedAt.UtcTicks}";

    /// <summary>Mira si toca enviar i, si cal, envia (cada pocs segons, fora del fil de control).</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = _clock();
        List<(Entry Entry, Incident Sent)> batch;
        MailSettings settings;
        string? password;
        int dropped;
        lock (_sync)
        {
            if (_sending || _pending.Count == 0 || !_settings.Enabled || _passwordProblem is not null || _settings.Missing() is not null) return;
            if (_retryAt is { } retry && now < retry) return;
            if (now - _pending.Values.Min(e => e.AddedAt) < GroupWait) return;
            if (_failures == 0 && LastSentAt is { } last && now - last < MinInterval)
            {
                // Fa poc de l'últim: només si n'hi ha una de greu de nova, també agrupada amb les que arribin amb ella
                var dangers = _pending.Values.Where(e => e.New && e.Incident.Severity == IncidentSeverity.Danger).ToList();
                if (dangers.Count == 0 || now - dangers.Min(e => e.AddedAt) < GroupWait) return;
            }
            batch = _pending.Values.OrderBy(e => e.Incident.StartedAt).Select(e => (e, e.Incident)).ToList();
            settings = _settings;
            password = _password;
            dropped = _dropped;
            _sending = true;
        }

        try
        {
            var text = Compose(batch.Select(b => new MailItem(b.Sent, b.Entry.New)).ToList(), SafeOpenNow(), SafeStationName(),
                Environment.MachineName, dropped);
            string? error = await TrySendAsync(settings, password, text, ct);
            lock (_sync)
            {
                if (error is null)
                {
                    foreach (var (entry, sent) in batch)
                    {
                        string id = Identity(sent);
                        if (sent.IsOpen) _mailed.Add(id); else _mailed.Remove(id);
                        if (ReferenceEquals(entry.Incident, sent)) _pending.Remove(id);
                        else entry.New = false; // ha canviat mentre s'enviava (p. ex. s'ha resolt): anirà al següent
                    }
                    _dropped -= dropped;
                    _failures = 0;
                    _retryAt = null;
                    _sendProblem = null;
                    LastSentAt = now;
                }
                else
                {
                    _failures++;
                    var wait = RetryWaits[Math.Min(_failures, RetryWaits.Length) - 1];
                    _retryAt = now + wait;
                    _sendProblem = $"No s'ha pogut enviar el correu d'avís: {error} Es tornarà a provar a les {(now + wait).ToLocalTime():HH:mm}.";
                }
            }
            if (error is null) _log?.LogInformation("Avisos per correu: enviat «{Subject}»", text.Subject);
            else _log?.LogWarning("Avisos per correu: no s'ha pogut enviar ({Error})", error);
        }
        finally
        {
            lock (_sync) _sending = false;
        }
    }

    /// <summary>Envia el correu de prova amb aquesta configuració (encara que no estigui desada). Retorna l'error, o null.</summary>
    public async Task<string?> SendTestAsync(MailSettings settings, string? password, CancellationToken ct = default)
    {
        string station = SafeStationName();
        var now = _clock().ToLocalTime();
        var text = new MailText($"[{(station.Length > 0 ? station : "Ona Bages 24h")}] Correu de prova",
            $"Aquest és un correu de prova dels avisos d'Ona Bages 24h, enviat des de l'ordinador {Environment.MachineName} " +
            $"el {now:dd/MM/yyyy} a les {now:HH:mm}.\r\n\r\nSi el reps, els avisos arribaran a aquesta adreça.\r\n");
        string? error = await TrySendAsync(settings, password, text, ct);
        if (error is null) _log?.LogInformation("Avisos per correu: correu de prova enviat");
        else _log?.LogWarning("Avisos per correu: el correu de prova no s'ha pogut enviar ({Error})", error);
        return error;
    }

    private async Task<string?> TrySendAsync(MailSettings settings, string? password, MailText text, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SendTimeout);
        try
        {
            await Task.Run(() => _send(settings, password, text.Subject, text.Body, timeout.Token), timeout.Token);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return SmtpMail.Describe(ex, settings);
        }
    }

    private string SafeStationName()
    {
        try { return _stationName().Trim(); }
        catch (Exception) { return ""; }
    }

    private IReadOnlyList<Incident> SafeOpenNow()
    {
        try { return _openNow(); }
        catch (Exception) { return []; }
    }

    // --- El text del correu ---

    /// <summary>
    /// El correu: a l'assumpte, quantes n'hi ha de noves de cada gravetat i de quins tipus; al cos, les noves, les resoltes
    /// i les que són obertes ara (text pla).
    /// </summary>
    public static MailText Compose(IReadOnlyList<MailItem> items, IReadOnlyList<Incident> openNow, string station, string machine, int dropped = 0)
    {
        string name = station.Length > 0 ? station : "Ona Bages 24h";
        var news = items.Where(i => i.New).Select(i => i.Incident).ToList();
        var resolved = items.Where(i => !i.New).Select(i => i.Incident).ToList();

        var counts = new List<string>();
        void Count(int n, string one, string many) { if (n > 0) counts.Add($"{n} {(n == 1 ? one : many)}"); }
        Count(news.Count(i => i.Severity == IncidentSeverity.Danger), "greu", "greus");
        Count(news.Count(i => i.Severity == IncidentSeverity.Warning), "avís", "avisos");
        Count(news.Count(i => i.Severity == IncidentSeverity.Info), "informació", "informacions");
        Count(resolved.Count, "resolta", "resoltes");
        var kinds = news.OrderByDescending(i => i.Severity).Concat(resolved).Select(i => IncidentKind.Name(i.Kind)).Distinct().ToList();
        string subject = $"[{name}] {string.Join(", ", counts)}: {string.Join(", ", kinds.Take(3))}{(kinds.Count > 3 ? "…" : "")}";

        var body = new StringBuilder();
        void Line(string text) => body.Append(text).Append("\r\n");
        Line($"Avisos d'Ona Bages 24h — {name}, ordinador {machine}");
        if (news.Count > 0)
        {
            Line("");
            Line("NOVES");
            foreach (var i in news)
            {
                string state = i.IsOpen ? "continua oberta"
                    : i.EndedAt == i.StartedAt ? (i.Recovered ? "el motor ho ha resolt sol" : "")
                    : $"s'ha resolt en {Duration(i.EndedAt!.Value - i.StartedAt)}";
                Line($"- [{SeverityLabel(i.Severity)}] {Time(i.StartedAt)} · {IncidentKind.Name(i.Kind)}: {i.Text}{(state.Length > 0 ? $" ({state})" : "")}");
            }
        }
        if (resolved.Count > 0)
        {
            Line("");
            Line("RESOLTES");
            foreach (var i in resolved)
                Line($"- {Time(i.EndedAt!.Value)} · {IncidentKind.Name(i.Kind)}: {i.Text} (ha durat {Duration(i.EndedAt!.Value - i.StartedAt)})");
        }
        if (dropped > 0)
        {
            Line("");
            Line($"N'hi ha {dropped} més que no hi caben: mira «Incidències» a l'aplicació.");
        }
        Line("");
        if (openNow.Count == 0) Line("Ara no hi ha cap incidència oberta.");
        else
        {
            Line($"OBERTES ARA ({openNow.Count})");
            foreach (var i in openNow)
                Line($"- [{SeverityLabel(i.Severity)}] {IncidentKind.Name(i.Kind)}: {i.Text} (des del {Time(i.StartedAt)})");
        }
        return new MailText(subject, body.ToString());
    }

    private static string SeverityLabel(IncidentSeverity severity) => severity switch
    {
        IncidentSeverity.Danger => "GREU",
        IncidentSeverity.Warning => "AVÍS",
        _ => "INFO",
    };

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Una durada en paraules: «40 s», «7 min», «2 h 5 min», «3 dies».</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return $"{Math.Max(0, (int)span.TotalSeconds)} s";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min";
        if (span < TimeSpan.FromDays(1)) return span.Minutes > 0 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{(int)span.TotalHours} h";
        int days = (int)span.TotalDays;
        return days == 1 ? "1 dia" : $"{days} dies";
    }
}
