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
    /// <summary>Durada del fitxer (de la biblioteca), si se sap.</summary>
    public long? FileDurationMs { get; init; }

    /// <summary>Quant sonarà, de l'inici al final (sense comptar el solapament amb la següent), o null si no se sap.</summary>
    public long? PlannedMs => (EndMs ?? FileDurationMs) is { } end ? Math.Max(0, end - (StartMs ?? 0)) : null;
    public QueueOrigin Origin { get; init; } = QueueOrigin.Manual;
    /// <summary>Nom de l'esdeveniment que l'ha posat a la cua, si n'és el cas.</summary>
    public string? EventName { get; init; }
    /// <summary>L'esdeveniment i l'emissió que l'han posat a la cua: si s'esborra abans de sonar, queda com a NO_EMES.</summary>
    public long? EventId { get; init; }
    public DateTimeOffset? EventAt { get; init; }

    /// <summary>
    /// Trepitjador (fase 3, pas 5): el sweeper que sona a sobre de l'inici d'aquesta pista, si el pas de la rotació en
    /// demana. El tria l'AutoDJ en posar-la a la cua; el fa sonar <see cref="SweeperOverlay"/>.
    /// </summary>
    public QueueItem? Sweeper { get; init; }

    /// <summary>
    /// La rotació i el pas (índex des de 0) d'on l'ha triada l'AutoDJ, o null si no ve d'un pas (manual, esdeveniment o
    /// música de reserva). Serveix perquè, en tornar a triar la cua, la rotació continuï exactament on era (D-097).
    /// </summary>
    public long? RotationId { get; init; }
    public int? RotationStep { get; init; }

    /// <summary>Com era el pas quan s'ha triat, p. ex. «Bossa «Generalista A» (Music / Adult 6)», per mostrar-ho a la cua.</summary>
    public string? RotationStepName { get; init; }

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
        FileDurationMs = track.DurationMs,
        Origin = origin,
        EventName = eventName,
    };

    public override string ToString() => string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}
