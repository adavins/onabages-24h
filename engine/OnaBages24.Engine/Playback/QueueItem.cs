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

    // --- Streams (fase 2, pas 7b; D-030, D-175) ---

    /// <summary>Quant sona un stream si no se'n sap la durada (la fitxa de la pista no en té): una hora.</summary>
    public const long DefaultStreamMs = 60 * 60 * 1000;
    /// <summary>El fos de sortida d'un stream quan arriba a la durada (la següent entra a sobre del fos).</summary>
    public const long StreamFadeMs = 2000;

    /// <summary>
    /// Una font en directe: un stream de ràdio per internet (la pista és de tipus Stream o la ruta és una adreça) o una entrada
    /// de línia (<see cref="IsInput"/>). Sonen igual a la cua.
    /// </summary>
    public bool IsStream => Type == TrackType.Stream || Path.Contains("://", StringComparison.Ordinal);

    // --- Entrades de línia (D-185) ---

    /// <summary>Com comença la ruta d'una entrada de línia: <c>entrada://</c> i el nom de l'entrada (es veu així a l'historial).</summary>
    public const string InputScheme = "entrada://";

    /// <summary>Una entrada de línia de Windows (acció ENTRADA, D-185): sona com un stream.</summary>
    public bool IsInput => Path.StartsWith(InputScheme, StringComparison.Ordinal);

    /// <summary>L'identificador de Windows de l'entrada (si no hi és, es busca pel nom, <see cref="Title"/>).</summary>
    public string? InputDeviceId { get; init; }

    /// <summary>
    /// Títol per a BUTT mentre sona (acció ENTRADA, D-189): el que escriu <c>NowPlaying.txt</c>, sigui quin sigui el tipus de
    /// la pista. Null = el de sempre (una entrada no canvia el títol).
    /// </summary>
    public string? ButtTitle { get; init; }

    /// <summary>Guany fix de l'entrada, en dB (D-188). 0 = tal com arriba.</summary>
    public double InputGainDb { get; init; }

    /// <summary>Anivellador automàtic de l'entrada (AGC, D-188): l'objectiu (LUFS) i el màxim que pot apujar (dB), o null.</summary>
    public (double TargetLufs, double MaxBoostDb)? InputAgc { get; init; }

    /// <summary>
    /// AGC per defecte (D-188): el nivell al qual es porta l'entrada i el màxim que es pot apujar. L'objectiu és el de la
    /// música mesurada a la sortida («Lo que se oye», 18-11-2026: −13 LUFS); al PC 24h s'ha de tornar a mesurar.
    /// </summary>
    public const double DefaultAgcTargetLufs = -13, DefaultAgcMaxBoostDb = 12;

    /// <summary>Una entrada de línia de Windows a la cua: amb el seu identificador i el seu nom (per si l'identificador canvia).</summary>
    public static QueueItem FromInput(string? deviceId, string deviceName, QueueOrigin origin, string? eventName = null) => new()
    {
        Path = InputScheme + deviceName,
        Title = deviceName,
        Type = TrackType.Stream,
        InputDeviceId = deviceId,
        Origin = origin,
        EventName = eventName,
    };

    /// <summary>Si arriba en silenci aquests segons seguits, s'acaba i passa a la següent (D-030). Null = no (per defecte).</summary>
    public int? StreamSilenceSeconds { get; init; }

    /// <summary>«Comença pel directe» (D-175): sense la ràfega inicial del servidor. Per defecte, sí.</summary>
    public bool StreamLiveEdge { get; init; } = true;

    /// <summary>
    /// Retard de connexió (acció STREAM, D-178): en un TALLA, el tall es fa aquests segons més tard, perquè el que sona del
    /// stream quadri amb l'hora de l'origen (que arriba amb aquest retard). Null o 0 = a l'hora.
    /// </summary>
    public int? StreamDelaySeconds { get; init; }

    /// <summary>Pista de reserva (acció STREAM, D-010): si el stream no connecta, sona aquesta en el seu lloc. Null = cap (se salta).</summary>
    public QueueItem? StreamFallback { get; init; }

    /// <summary>
    /// Reconnexió durant tota la durada (acció STREAM, D-031, D-180): si cau (o no connecta a l'hora), sona música de la
    /// rotació mentre es prova de reconnectar, i el stream torna quan funciona. El que va darrere no s'avança.
    /// </summary>
    public bool StreamReconnect { get; init; }

    /// <summary>Amb <see cref="StreamReconnect"/>: quan el stream torna, espera que s'acabi la cançó que sona (si no, de seguida).</summary>
    public bool StreamReturnAfterSong { get; init; }

    /// <summary>
    /// Acció STREAM (fase 2, pas 7e; D-182): mentre sona, el títol que envia el servidor (ICY) va a <c>NowPlaying.txt</c>.
    /// Desactivat per defecte: si el stream és la mateixa emissió, el títol seria el que ja hi ha escrit.
    /// </summary>
    public bool StreamTitleForButt { get; init; }

    /// <summary>Quant sonarà, de l'inici al final (sense comptar el solapament amb la següent), o null si no se sap.</summary>
    public long? PlannedMs => (EndMs ?? FileDurationMs) is { } end ? Math.Max(0, end - (StartMs ?? 0)) : null;
    public QueueOrigin Origin { get; init; } = QueueOrigin.Manual;
    /// <summary>Nom de l'esdeveniment que l'ha posat a la cua, si n'és el cas.</summary>
    public string? EventName { get; init; }
    /// <summary>L'esdeveniment i l'emissió que l'han posat a la cua: si s'esborra abans de sonar, queda com a NO_EMES.</summary>
    public long? EventId { get; init; }
    public DateTimeOffset? EventAt { get; init; }

    /// <summary>La campanya que l'ha posat a la cua (acció CAMPANYA; fase 4, pas 3b): es desa a l'historial per al certificat.</summary>
    public long? CampaignId { get; init; }

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

    public static QueueItem FromTrack(Track track, QueueOrigin origin, string? eventName = null)
    {
        var item = new QueueItem
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
        return item.IsStream ? item.AsStream(track.DurationMs ?? DefaultStreamMs) : item;
    }

    /// <summary>
    /// Un stream que ha de sonar <paramref name="durationMs"/>: del principi, amb un fos de <paramref name="fadeMs"/> al final
    /// (per defecte <see cref="StreamFadeMs"/>; la següent entra a sobre del fos).
    /// </summary>
    public QueueItem AsStream(long durationMs, long fadeMs = StreamFadeMs)
    {
        fadeMs = Math.Clamp(fadeMs, 0, 10_000);
        long duration = Math.Max(fadeMs + 1000, durationMs);
        return this with { StartMs = 0, EndMs = duration, MixMs = duration - fadeMs, FileDurationMs = duration };
    }

    /// <summary>Per als textos: «El stream «…»» o «L'entrada «…»» (D-185).</summary>
    public string SourceName() => IsInput ? $"L'entrada «{this}»" : $"El stream «{this}»";

    public override string ToString() => string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
}
