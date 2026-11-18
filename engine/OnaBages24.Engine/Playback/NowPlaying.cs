using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Infrastructure;

namespace OnaBages24.Engine.Playback;

/// <summary>Ajustos de l'exportació del títol (fase 1, pas 9; D-009). Es desen a la configuració (clau <c>nowplaying</c>).</summary>
public sealed record NowPlayingSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Fitxer on s'escriu. Null = <c>NowPlaying.txt</c> a la carpeta de dades.</summary>
    public string? Path { get; init; }

    /// <summary>Plantilla del text, amb <c>$artist$</c> i <c>$title$</c>.</summary>
    public string Template { get; init; } = "$artist$ - $title$";

    /// <summary>
    /// Tipus de pista que canvien el títol. Amb les altres (falques, cortines, indicatius, hores…), el fitxer es queda com
    /// estava: segueix sortint la cançó o el programa d'abans.
    /// </summary>
    public IReadOnlyList<TrackType> Types { get; init; } = [TrackType.Music, TrackType.Program];

    public string ToJson() => JsonSerializer.Serialize(new
    {
        enabled = Enabled,
        path = Path,
        template = Template,
        types = Types.Select(t => Protocol.QueueJson.Type(t)).ToArray(),
    });

    /// <summary>Llegeix els ajustos desats. Els camps que falten tenen el valor per defecte; un tipus desconegut dona error.</summary>
    public static NowPlayingSettings FromJson(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("No és un objecte JSON.");
        var settings = new NowPlayingSettings();
        if (node["enabled"] is { } enabled) settings = settings with { Enabled = enabled.GetValue<bool>() };
        if (node["path"] is { } path) settings = settings with { Path = path.GetValue<string>() };
        if (node["template"] is { } template) settings = settings with { Template = template.GetValue<string>() };
        if (node["types"] is JsonArray types)
            settings = settings with { Types = types.Select(t => ParseType(t?.GetValue<string>())).ToArray() };
        return settings;
    }

    /// <summary>Tipus de pista a partir del nom del protocol (<c>music</c>, <c>program</c>…).</summary>
    public static TrackType ParseType(string? name) =>
        Enum.TryParse<TrackType>(name, ignoreCase: true, out var type) && !int.TryParse(name, out _)
            ? type
            : throw new FormatException($"Tipus de pista desconegut: «{name}»");
}

/// <summary>
/// Títol fix per a BUTT (D-184): mentre dura (fins a <paramref name="Until"/>), <c>NowPlaying.txt</c> diu aquest text sigui quina
/// sigui la cua. <paramref name="EventName"/>: l'esdeveniment que l'ha posat (acció <c>TITOL</c>), o null si s'ha posat a mà.
/// </summary>
public sealed record FixedTitle(string Text, DateTimeOffset Until, string? EventName)
{
    public const int MaxLength = 200;

    public string ToJson() => JsonSerializer.Serialize(new { text = Text, until = Until.ToString("O"), eventName = EventName });

    /// <summary>El títol fix desat (per recuperar-lo si el motor es reinicia), o null si no n'hi ha o no s'entén.</summary>
    public static FixedTitle? FromJson(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node) return null;
            if (node["text"]?.GetValue<string>() is not { } text || node["until"]?.GetValue<string>() is not { } until) return null;
            return new FixedTitle(text, DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture),
                node["eventName"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Exporta el títol de la pista que sona a un fitxer de text (<c>NowPlaying.txt</c>), que llegeix BUTT per enviar-lo al
/// servidor d'streaming (D-009). Substitueix el plugin «Playing Info» de RadioDJ.
///
/// Un stream amb l'opció «títol per a BUTT» (fase 2, pas 7e; D-182): mentre sona, s'hi escriu el títol que envia el servidor
/// (ICY), i cada vegada que canvia.
///
/// Títol fix (D-184): un text que mana sobre el de la cua fins a una hora (un directe que no surt de la cua, p. ex. «Esport i
/// Punt»). Quan s'acaba, torna sol el títol del que sona. El posa l'acció <c>TITOL</c> del planificador o l'operador a mà.
///
/// El text es prepara al fil de control (quan comença una pista) i el fitxer s'escriu en un fil propi: si el disc o la
/// carpeta de xarxa tarda o falla, l'emissió no se n'assabenta. Si no es pot escriure (p. ex. BUTT el té obert just en
/// aquell moment), es torna a provar cada <see cref="RetryDelay"/>, amb un avís al registre.
/// </summary>
public sealed class NowPlaying : IDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly string _defaultPath;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _worker;
    private QueueItem? _lastItem;
    private string? _streamTitle; // el títol del stream que s'ha escrit l'últim (D-182), o null
    private string? _queueText;   // el títol del que sona (el que hi hauria sense el títol fix)
    private FixedTitle? _fixed;   // el títol fix (D-184), o null
    private bool _dirty;
    private long _writes;

    /// <param name="queue">Cua d'emissió (null a les proves: llavors s'hi crida <see cref="Show"/> directament).</param>
    /// <param name="defaultPath">Fitxer per defecte (<c>&lt;dades&gt;\NowPlaying.txt</c>).</param>
    public NowPlaying(QueuePlayer? queue, string defaultPath, NowPlayingSettings settings, ILogger log)
    {
        _defaultPath = defaultPath;
        _log = log;
        Settings = settings;
        if (queue is not null)
        {
            queue.Started += e => Show(e.Item);
            // El títol del stream (D-182): la cua avisa quan canvia
            queue.Changed += () => ShowStream(queue.StreamStatus);
        }
        // Fil propi, no del grup de fils de .NET: si el grup va carregat, el títol no s'ha d'esperar
        _worker = new Thread(Run) { IsBackground = true, Name = "Títol per a BUTT" };
        _worker.Start();
    }

    public NowPlayingSettings Settings { get; private set; }

    /// <summary>El fitxer on s'escriu (el configurat o el per defecte).</summary>
    public string FilePath => string.IsNullOrWhiteSpace(Settings.Path) ? _defaultPath : Settings.Path;

    /// <summary>L'últim text exportat (o que s'està intentant exportar); null si encara no n'hi ha cap.</summary>
    public string? Text { get; private set; }

    /// <summary>Per què no s'ha pogut escriure el fitxer (en català), o null si tot va bé.</summary>
    public string? Problem { get; private set; }

    /// <summary>Quantes vegades s'ha escrit el fitxer (per a les proves).</summary>
    public long Writes => Interlocked.Read(ref _writes);

    /// <summary>El títol fix que mana ara (D-184), o null.</summary>
    public FixedTitle? Fixed
    {
        get { lock (_sync) return _fixed; }
    }

    /// <summary>S'ha posat, s'ha tret o s'ha acabat el títol fix (null): per desar-lo. Pot arribar des del fil d'escriptura.</summary>
    public event Action<FixedTitle?>? FixedChanged;

    /// <summary>
    /// Posa un títol fix (D-184) fins a <paramref name="until"/>: substitueix el que hi hagués. El text es deixa en una línia;
    /// ha de tenir entre 1 i <see cref="FixedTitle.MaxLength"/> caràcters, i l'hora de final ha de ser més tard que ara.
    /// </summary>
    public FixedTitle SetFixed(string text, DateTimeOffset until, string? eventName)
    {
        string clean = OneLine(text ?? "");
        if (clean.Length == 0) throw new ArgumentException("Cal escriure el text del títol fix.");
        if (clean.Length > FixedTitle.MaxLength) throw new ArgumentException($"El títol fix pot tenir {FixedTitle.MaxLength} caràcters com a màxim.");
        if (until <= DateTimeOffset.Now) throw new ArgumentException("L'hora de final del títol fix ja ha passat.");
        var title = new FixedTitle(clean, until, eventName);
        Apply(title);
        _log.LogInformation("Títol per a BUTT fix: «{Text}» fins a les {Until:HH:mm} ({Who})", clean, until.ToLocalTime(),
            eventName is null ? "posat a mà" : $"esdeveniment «{eventName}»");
        FixedChanged?.Invoke(title);
        return title;
    }

    /// <summary>Recupera el títol fix desat (el motor s'ha reiniciat a mig programa). Si ja s'ha acabat, no fa res.</summary>
    public bool Restore(FixedTitle title)
    {
        if (title.Until <= DateTimeOffset.Now || string.IsNullOrWhiteSpace(title.Text)) return false;
        Apply(title with { Text = OneLine(title.Text) });
        _log.LogInformation("Títol per a BUTT fix recuperat: «{Text}» fins a les {Until:HH:mm}", title.Text, title.Until.ToLocalTime());
        return true;
    }

    /// <summary>Treu el títol fix (a mà): torna el títol del que sona. Retorna false si no n'hi havia cap.</summary>
    public bool ClearFixed()
    {
        FixedTitle? old;
        lock (_sync)
        {
            old = _fixed;
            if (old is null) return false;
            EndFixedLocked();
        }
        _log.LogInformation("Títol per a BUTT: s'ha tret el títol fix «{Text}»; torna el del que sona", old.Text);
        FixedChanged?.Invoke(null);
        return true;
    }

    private void Apply(FixedTitle title)
    {
        lock (_sync)
        {
            _fixed = title;
            Text = title.Text;
            if (Settings.Enabled) MarkDirty();
            else if (_wake.CurrentCount == 0) _wake.Release(); // perquè el fil d'escriptura sàpiga quan s'acaba
        }
    }

    /// <summary>S'acaba el títol fix: torna el del que sona (si encara no ha sonat res, el fitxer es queda com està). Dins del bloqueig.</summary>
    private void EndFixedLocked()
    {
        _fixed = null;
        Text = _queueText;
        if (Settings.Enabled && Text is not null) MarkDirty();
    }

    /// <summary>Una pista comença a sonar: si és d'un tipus que s'exporta, se n'escriu el títol.</summary>
    public void Show(QueueItem item)
    {
        lock (_sync)
        {
            // Una entrada amb el seu títol per a BUTT (D-189): aquest text, sigui quin sigui el tipus
            if (item.ButtTitle is { } buttTitle && OneLine(buttTitle) is { Length: > 0 } text)
            {
                _lastItem = null;
                _streamTitle = text;
                _queueText = text;
                if (_fixed is not null) return; // mana el títol fix (D-184)
                Text = text;
                if (Settings.Enabled) MarkDirty();
                return;
            }
            if (!Settings.Types.Contains(item.Type)) return;
            _lastItem = item;
            _streamTitle = null;
            _queueText = Format(Settings.Template, item.Artist, item.Title);
            if (_fixed is not null) return; // mana el títol fix (D-184)
            Text = _queueText;
            if (Settings.Enabled) MarkDirty();
        }
    }

    /// <summary>
    /// Com va el stream que sona (D-182): si té l'opció «títol per a BUTT» i el servidor n'envia un, s'escriu aquest títol
    /// (tal com arriba, en una línia). Si no, no es toca res.
    /// </summary>
    public void ShowStream(QueueStreamStatus? status)
    {
        if (status is not { Item.StreamTitleForButt: true, Title: { } title }) return;
        string text = OneLine(title);
        if (text.Length == 0) return;
        lock (_sync)
        {
            if (_streamTitle == text) return;
            _streamTitle = text;
            _lastItem = null;
            _queueText = text;
            if (_fixed is not null) return; // mana el títol fix (D-184)
            Text = text;
            if (Settings.Enabled) MarkDirty();
        }
    }

    /// <summary>Canvia els ajustos. Si ja havia sonat alguna pista, se'n torna a escriure el títol amb els ajustos nous.</summary>
    public void Configure(NowPlayingSettings settings)
    {
        lock (_sync)
        {
            Settings = settings;
            if (_lastItem is { } item) _queueText = Format(settings.Template, item.Artist, item.Title);
            Text = _fixed?.Text ?? _queueText;
            Problem = null;
            if (settings.Enabled && Text is not null) MarkDirty();
            else _dirty = false;
        }
    }

    /// <summary>
    /// Omple la plantilla. Si l'artista o el títol són buits, es treuen els separadors que quedarien sols
    /// (amb «$artist$ - $title$» i sense artista, surt només el títol). Sempre una sola línia.
    /// </summary>
    public static string Format(string template, string artist, string title)
    {
        artist = OneLine(artist);
        title = OneLine(title);
        string text = OneLine(template
            .Replace("$artist$", artist, StringComparison.OrdinalIgnoreCase)
            .Replace("$title$", title, StringComparison.OrdinalIgnoreCase));
        if (artist.Length == 0 || title.Length == 0) text = text.Trim(' ', '-', '–', '—', '|', '/', ':', '·');
        return text;
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    private void MarkDirty()
    {
        _dirty = true;
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>Fil d'escriptura: espera que hi hagi un text nou i l'escriu; si falla, ho torna a provar al cap d'una estona.</summary>
    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            // Fins quan s'espera: per sempre, fins a tornar a provar d'escriure o fins que s'acabi el títol fix (D-184)
            var wait = Timeout.InfiniteTimeSpan;
            lock (_sync)
            {
                if (_dirty && Problem is not null) wait = RetryDelay;
                if (_fixed is { } f)
                {
                    var left = f.Until - DateTimeOffset.Now;
                    left = left < TimeSpan.Zero ? TimeSpan.Zero : left + TimeSpan.FromMilliseconds(1);
                    if (wait == Timeout.InfiniteTimeSpan || left < wait) wait = left;
                }
            }
            try
            {
                _wake.Wait(wait, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // El títol fix s'ha acabat: torna el del que sona
            FixedTitle? ended = null;
            lock (_sync)
            {
                if (_fixed is { } f && DateTimeOffset.Now >= f.Until)
                {
                    ended = f;
                    EndFixedLocked();
                }
            }
            if (ended is not null)
            {
                _log.LogInformation("Títol per a BUTT: s'ha acabat el títol fix «{Text}»; torna el del que sona", ended.Text);
                FixedChanged?.Invoke(null);
            }

            string text, path;
            lock (_sync)
            {
                if (!_dirty || Text is null) continue;
                text = Text;
                path = FilePath;
            }

            try
            {
                AtomicFile.WriteAllText(path, text);
                Interlocked.Increment(ref _writes);
                lock (_sync)
                {
                    if (Text == text && FilePath == path) _dirty = false;
                    if (Problem is not null) _log.LogInformation("Títol per a BUTT: ja es torna a poder escriure {Path}", path);
                    Problem = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                string problem = $"No es pot escriure el títol a {path}: {ex.Message}";
                lock (_sync)
                {
                    if (problem != Problem) _log.LogWarning("{Problem} (es tornarà a provar)", problem);
                    Problem = problem;
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        _wake.Dispose();
    }
}
