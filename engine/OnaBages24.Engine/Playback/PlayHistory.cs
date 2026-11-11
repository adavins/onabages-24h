using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Anota a l'historial cada pista que sona (quan comença i quan s'acaba) i n'actualitza la darrera reproducció, que és
/// el que fa servir l'AutoDJ per triar «la menys reproduïda recentment». Un error de la base de dades no atura res:
/// queda al registre i l'emissió continua.
/// </summary>
public sealed class PlayHistory
{
    private readonly Dictionary<Guid, (long Entry, DateTimeOffset Started)> _open = [];
    private readonly Library _library;
    private readonly ILogger _log;

    public PlayHistory(QueuePlayer queue, Library library, ILogger log)
    {
        _library = library;
        _log = log;
        queue.Started += e =>
        {
            try
            {
                var track = (e.Item.TrackId is { } id ? library.GetTrack(id) : null)
                            ?? new Track { Path = e.Item.Path, Artist = e.Item.Artist, Title = e.Item.Title };
                var started = DateTimeOffset.UtcNow;
                long entry = library.AddHistory(track, started, e.Item.Origin);
                lock (_open) _open[e.Item.Id] = (entry, started);
            }
            catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogWarning("No s'ha pogut anotar {Track} a l'historial: {Message}", e.Item, ex.Message);
            }
        };

        queue.Ended += e =>
        {
            long entry;
            lock (_open)
            {
                if (!_open.Remove(e.Item.Id, out var open)) return;
                entry = open.Entry;
            }
            try
            {
                library.FinishHistory(entry, e.Played, e.Complete);
            }
            catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogWarning("No s'ha pogut tancar {Track} a l'historial: {Message}", e.Item, ex.Message);
            }
        };
    }

    /// <summary>
    /// El motor s'atura (fase 3, pas 2a): les pistes que sonen es tanquen a l'historial com a tallades, amb el temps que
    /// han sonat. Si no, quedarien com a «Sona» per sempre.
    /// </summary>
    public void CloseAll()
    {
        List<(long Entry, DateTimeOffset Started)> open;
        lock (_open)
        {
            open = [.. _open.Values];
            _open.Clear();
        }
        foreach (var (entry, started) in open)
        {
            try
            {
                _library.FinishHistory(entry, DateTimeOffset.UtcNow - started, complete: false);
            }
            catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
            {
                _log.LogWarning("No s'ha pogut tancar l'historial en aturar el motor: {Message}", ex.Message);
            }
        }
    }
}
