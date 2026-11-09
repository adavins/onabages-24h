using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Un element de la cua d'emissió: una pista, amb els seus punts d'encadenament i d'on ve (D-034).
/// Els punts són en mil·lisegons des del principi del fitxer; si en falta algun, es fa servir la durada del fitxer.
/// </summary>
public sealed record QueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long? TrackId { get; init; }
    public required string Path { get; init; }
    public string Artist { get; init; } = "";
    public string Title { get; init; } = "";
    public TrackType Type { get; init; } = TrackType.Music;
    public long? StartMs { get; init; }
    public long? MixMs { get; init; }
    public long? EndMs { get; init; }
    public QueueOrigin Origin { get; init; } = QueueOrigin.Manual;
    /// <summary>Nom de l'esdeveniment que l'ha posat a la cua, si n'és el cas.</summary>
    public string? EventName { get; init; }

    public static QueueItem FromTrack(Track track, QueueOrigin origin, string? eventName = null) => new()
    {
        TrackId = track.Id,
        Path = track.Path,
        Artist = track.Artist,
        Title = track.Title,
        Type = track.Type,
        StartMs = track.StartMs,
        MixMs = track.MixMs,
        EndMs = track.EndMs,
        Origin = origin,
        EventName = eventName,
    };

    public override string ToString() => string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}
