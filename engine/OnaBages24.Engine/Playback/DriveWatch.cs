namespace OnaBages24.Engine.Playback;

/// <summary>
/// Unitats de la música que no responen (D-197; incidència del NAS del 20-11-2026). Quan una pista falla o s'encalla per
/// un error de disc o de xarxa, el motor en fa comprovar la unitat (<see cref="Suspect"/>): l'arrel (<c>O:\</c> o
/// <c>\\nas\musica</c>), en un fil a part i amb un temps màxim (una unitat de xarxa que no respon pot tardar 20 o 30 s a
/// dir-ho). Si no respon, queda com a «no disponible» i es torna a comprovar cada pocs segons fins que torna. La cua no
/// obre les pistes d'una unitat que no respon (<see cref="IsDown"/>, barat: es pot cridar des del fil de control).
/// </summary>
public sealed class DriveWatch
{
    /// <summary>Quanta estona es dona a l'arrel per respondre.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Cada quant es torna a comprovar una unitat que no respon.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(5);

    private readonly Func<string, bool> _probe;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _recheck;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _down = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checking = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _stopped;

    /// <param name="probe">Si l'arrel respon (per defecte, <see cref="Directory.Exists(string)"/>). Les proves en passen una de falsa.</param>
    public DriveWatch(Func<string, bool>? probe = null, TimeSpan? timeout = null, TimeSpan? recheck = null, Func<DateTimeOffset>? clock = null)
    {
        _probe = probe ?? Directory.Exists;
        _timeout = timeout ?? ProbeTimeout;
        _recheck = recheck ?? RecheckEvery;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Ha deixat de respondre una unitat (l'arrel i des de quan).</summary>
    public event Action<string, DateTimeOffset>? WentDown;

    /// <summary>Torna a respondre (l'arrel i des de quan no responia).</summary>
    public event Action<string, DateTimeOffset>? CameBack;

    /// <summary>
    /// L'arrel d'una ruta: <c>O:\</c> per a <c>O:\musica\…</c>, o <c>\\nas\musica</c> per a <c>\\nas\musica\…</c>. Null si no
    /// és un fitxer (un stream, una entrada) o no se'n pot treure.
    /// </summary>
    public static string? RootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal)) return null;
        try
        {
            // «O:\» per a una lletra d'unitat; «\\nas\musica» per a una ruta de xarxa
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Si la unitat d'aquesta ruta no respon ara. Barat i sense esperar: per al fil de control.</summary>
    public bool IsDown(string path)
    {
        if (RootOf(path) is not { } root) return false;
        lock (_sync) return _down.ContainsKey(root);
    }

    /// <summary>Les unitats que no responen ara, amb des de quan.</summary>
    public IReadOnlyList<(string Root, DateTimeOffset Since)> Down
    {
        get { lock (_sync) return _down.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    /// <summary>
    /// Una pista d'aquesta ruta ha fallat o s'ha encallat: es comprova la seva unitat en un fil a part (si ja s'està
    /// comprovant o ja se sap que no respon, no cal fer res). Torna de seguida.
    /// </summary>
    public void Suspect(string path)
    {
        if (_stopped || RootOf(path) is not { } root) return;
        lock (_sync)
        {
            if (_down.ContainsKey(root) || !_checking.Add(root)) return;
        }
        _ = Task.Run(() => Check(root));
    }

    /// <summary>Deixa de comprovar (en aturar el motor).</summary>
    public void Stop() => _stopped = true;

    private async Task Check(string root)
    {
        try
        {
            if (await Responds(root)) return; // era una pista que falta: la unitat va bé
            var since = _clock();
            lock (_sync) _down[root] = since;
            WentDown?.Invoke(root, since);
            // Mentre no respongui, es torna a provar cada pocs segons
            while (!_stopped)
            {
                await Task.Delay(_recheck);
                if (!await Responds(root)) continue;
                lock (_sync) _down.Remove(root);
                CameBack?.Invoke(root, since);
                return;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { } // un avís que falla no fa caure res
        finally
        {
            lock (_sync) _checking.Remove(root);
        }
    }

    /// <summary>Si l'arrel respon dins del temps màxim. Una comprovació que no acaba es deixa córrer (pot estar esperant la xarxa).</summary>
    private async Task<bool> Responds(string root)
    {
        var probe = Task.Run(() =>
        {
            try { return _probe(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        });
        var finished = await Task.WhenAny(probe, Task.Delay(_timeout));
        return finished == probe && probe.Result;
    }
}
