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
/// Exporta el títol de la pista que sona a un fitxer de text (<c>NowPlaying.txt</c>), que llegeix BUTT per enviar-lo al
/// servidor d'streaming (D-009). Substitueix el plugin «Playing Info» de RadioDJ.
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
    private bool _dirty;
    private long _writes;

    /// <param name="queue">Cua d'emissió (null a les proves: llavors s'hi crida <see cref="Show"/> directament).</param>
    /// <param name="defaultPath">Fitxer per defecte (<c>&lt;dades&gt;\NowPlaying.txt</c>).</param>
    public NowPlaying(QueuePlayer? queue, string defaultPath, NowPlayingSettings settings, ILogger log)
    {
        _defaultPath = defaultPath;
        _log = log;
        Settings = settings;
        if (queue is not null) queue.Started += e => Show(e.Item);
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

    /// <summary>Una pista comença a sonar: si és d'un tipus que s'exporta, se n'escriu el títol.</summary>
    public void Show(QueueItem item)
    {
        lock (_sync)
        {
            if (!Settings.Types.Contains(item.Type)) return;
            _lastItem = item;
            Text = Format(Settings.Template, item.Artist, item.Title);
            if (Settings.Enabled) MarkDirty();
        }
    }

    /// <summary>Canvia els ajustos. Si ja havia sonat alguna pista, se'n torna a escriure el títol amb els ajustos nous.</summary>
    public void Configure(NowPlayingSettings settings)
    {
        lock (_sync)
        {
            Settings = settings;
            if (_lastItem is { } item) Text = Format(settings.Template, item.Artist, item.Title);
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
        static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
        artist = OneLine(artist);
        title = OneLine(title);
        string text = OneLine(template
            .Replace("$artist$", artist, StringComparison.OrdinalIgnoreCase)
            .Replace("$title$", title, StringComparison.OrdinalIgnoreCase));
        if (artist.Length == 0 || title.Length == 0) text = text.Trim(' ', '-', '–', '—', '|', '/', ':', '·');
        return text;
    }

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
            bool retrying;
            lock (_sync) retrying = _dirty && Problem is not null;
            try
            {
                _wake.Wait(retrying ? RetryDelay : Timeout.InfiniteTimeSpan, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
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
