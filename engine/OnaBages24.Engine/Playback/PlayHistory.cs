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

    /// <param name="aux">Si hi és, també s'anoten els sweepers del trepitjador (fase 3, pas 5), com a RadioDJ. Els
    /// reproductors auxiliars de l'operador i dels esdeveniments, no.</param>
    public PlayHistory(QueuePlayer queue, Library library, ILogger log, AuxPlayers? aux = null)
    {
        _library = library;
        _log = log;
        queue.Started += e => Open(e.Item);
        queue.Ended += e => Close(e.Item, e.Played, e.Complete);
        if (aux is not null)
        {
            aux.Started += e => { if (e.Player == AuxPlayers.SweeperPlayer) Open(e.Item); };
            aux.Ended += e => { if (e.Player == AuxPlayers.SweeperPlayer) Close(e.Item, e.Played, e.Complete); };
        }
    }

    /// <summary>Anota que comença a sonar (i n'actualitza la darrera reproducció).</summary>
    private void Open(QueueItem item)
    {
        try
        {
            var track = (item.TrackId is { } id ? _library.GetTrack(id) : null)
                        ?? new Track { Path = item.Path, Artist = item.Artist, Title = item.Title };
            var started = DateTimeOffset.UtcNow;
            long entry = _library.AddHistory(track, started, item.Origin);
            lock (_open) _open[item.Id] = (entry, started);
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning("No s'ha pogut anotar {Track} a l'historial: {Message}", item, ex.Message);
        }
    }

    /// <summary>Anota quant ha sonat i si ha sonat sencera.</summary>
    private void Close(QueueItem item, TimeSpan played, bool complete)
    {
        long entry;
        lock (_open)
        {
            if (!_open.Remove(item.Id, out var open)) return;
            entry = open.Entry;
        }
        try
        {
            _library.FinishHistory(entry, played, complete);
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogWarning("No s'ha pogut tancar {Track} a l'historial: {Message}", item, ex.Message);
        }
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
