namespace OnaBages24.Engine;

/// <summary>
/// Estat general del motor (fase 1, pas 6): quan ha arrencat i quants clients hi ha connectats.
/// La cua, l'AutoDJ i el mesclador s'hi afegiran als passos següents.
/// </summary>
public sealed class EngineState(Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private int _clients;

    public DateTimeOffset StartedAt { get; } = (clock ?? (() => DateTimeOffset.UtcNow))();

    /// <summary>Temps des que ha arrencat el motor.</summary>
    public TimeSpan Uptime => _clock() - StartedAt;

    public int Clients => Volatile.Read(ref _clients);

    public void SetClients(int count) => Volatile.Write(ref _clients, count);
}
