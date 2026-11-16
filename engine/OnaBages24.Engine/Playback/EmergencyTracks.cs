using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Pistes d'emergència (fase 5, pas 4; D-159): una carpeta d'un <b>disc local</b> amb unes quantes cançons. La cua en fa
/// sonar una quan la pista que tocava no s'ha pogut obrir a temps (NAS o xarxa lents), perquè no hi hagi silenci.
///
/// La llista de fitxers es llegeix en segon pla (<see cref="Refresh"/>, cada minut); <see cref="Pick"/> només tria de la
/// llista ja llegida, sense tocar el disc: es pot cridar des del fil de control.
/// </summary>
public sealed class EmergencyTracks
{
    /// <summary>Les extensions que es fan sonar (les mateixes que llegeix Media Foundation).</summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".ogg",
    };

    private readonly Func<string, bool> _isLocal;
    private readonly object _sync = new();
    private string _folder;
    private IReadOnlyList<string> _files = [];
    private string? _problem;
    private string? _last;

    /// <param name="folder">La carpeta (ruta completa).</param>
    /// <param name="isLocal">Si una carpeta és d'un disc local (les proves en passen un de fals).</param>
    public EmergencyTracks(string folder, Func<string, bool>? isLocal = null)
    {
        _folder = folder;
        _isLocal = isLocal ?? IsLocalDisk;
    }

    /// <summary>La carpeta d'ara.</summary>
    public string Folder
    {
        get { lock (_sync) return _folder; }
    }

    /// <summary>Quantes pistes hi ha (a l'última lectura).</summary>
    public int Count
    {
        get { lock (_sync) return _files.Count; }
    }

    /// <summary>Què no va bé (carpeta que no hi és, buida o de xarxa), o null.</summary>
    public string? Problem
    {
        get { lock (_sync) return _problem; }
    }

    /// <summary>Canvia la carpeta i la torna a llegir (fora del fil de control: toca el disc).</summary>
    public void SetFolder(string folder)
    {
        lock (_sync) _folder = folder;
        Refresh();
    }

    /// <summary>Torna a llegir la carpeta. Toca el disc: mai des del fil de control.</summary>
    public void Refresh()
    {
        string folder = Folder;
        IReadOnlyList<string> files = [];
        string? problem;
        try
        {
            if (!_isLocal(folder))
                problem = $"La carpeta de pistes d'emergència ha de ser d'un disc local d'aquest ordinador (no de xarxa ni extraïble): {folder}";
            else if (!Directory.Exists(folder))
                problem = $"No es troba la carpeta de pistes d'emergència: {folder}";
            else
            {
                files = Directory.EnumerateFiles(folder)
                    .Where(f => Extensions.Contains(Path.GetExtension(f)))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                problem = files.Count == 0 ? $"No hi ha cap pista d'emergència a {folder}: si una pista tarda a obrir-se, hi haurà silenci" : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            problem = $"No es pot llegir la carpeta de pistes d'emergència ({folder}): {ex.Message}";
        }
        lock (_sync)
        {
            if (folder != _folder) return; // l'han canviat mentrestant: ja la llegirà la crida nova
            _files = files;
            _problem = problem;
        }
    }

    /// <summary>
    /// Una pista d'emergència a l'atzar, que no sigui la mateixa que l'última vegada (si n'hi ha més d'una), o null si no
    /// n'hi ha cap. No toca el disc.
    /// </summary>
    public QueueItem? Pick()
    {
        lock (_sync)
        {
            if (_files.Count == 0) return null;
            var candidates = _files.Count > 1 ? _files.Where(f => f != _last).ToList() : [.. _files];
            string path = candidates[Random.Shared.Next(candidates.Count)];
            _last = path;
            var (artist, title) = NamesFromFile(path);
            return new QueueItem { Path = path, Artist = artist, Title = title, Type = TrackType.Music, Origin = QueueOrigin.Emergency };
        }
    }

    /// <summary>«Artista - Títol.mp3» → (Artista, Títol); si no té guió, tot és el títol.</summary>
    public static (string Artist, string Title) NamesFromFile(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? (name[..dash].Trim(), name[(dash + 3)..].Trim()) : ("", name.Trim());
    }

    /// <summary>La carpeta és d'un disc fix d'aquest ordinador (no de xarxa, ni USB, ni CD).</summary>
    public static bool IsLocalDisk(string folder)
    {
        try
        {
            string full = Path.GetFullPath(folder);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false; // \\servidor\carpeta
            string? root = Path.GetPathRoot(full);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
