using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Anota a l'historial cada pista que sona (quan comença i quan s'acaba) i n'actualitza la darrera reproducció, que és
/// el que fa servir l'AutoDJ per triar «la menys reproduïda recentment». Un error de la base de dades no atura res:
/// queda al registre i l'emissió continua.
/// </summary>
public sealed class PlayHistory
{
    private readonly Dictionary<Guid, long> _open = [];

    public PlayHistory(QueuePlayer queue, Library library, ILogger log)
    {
        queue.Started += e =>
        {
            try
            {
                var track = (e.Item.TrackId is { } id ? library.GetTrack(id) : null)
                            ?? new Track { Path = e.Item.Path, Artist = e.Item.Artist, Title = e.Item.Title };
                long entry = library.AddHistory(track, DateTimeOffset.UtcNow, e.Item.Origin);
                lock (_open) _open[e.Item.Id] = entry;
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
                if (!_open.Remove(e.Item.Id, out entry)) return;
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
}
