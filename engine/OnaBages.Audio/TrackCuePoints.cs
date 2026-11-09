namespace OnaBages.Audio;

/// <summary>
/// Punts d'encadenament d'una pista (D-006), comptats des del principi del fitxer:
/// - <see cref="Start"/>: on comença el so (es salta el silenci inicial);
/// - <see cref="Mix"/>: on comença a sonar la pista següent;
/// - <see cref="End"/>: on s'acaba la pista (la resta és silenci o gairebé).
/// Sempre es compleix Start ≤ Mix ≤ End.
/// </summary>
public sealed record TrackCuePoints(TimeSpan Start, TimeSpan Mix, TimeSpan End)
{
    /// <summary>Durada del fos de sortida: el tros entre la mescla i el final.</summary>
    public TimeSpan FadeOut => End - Mix;

    /// <summary>Quant sona la pista si es fa servir sencera (de l'inici al final).</summary>
    public TimeSpan PlayLength => End - Start;
}

/// <summary>
/// Llindars de l'analitzador, en dB respecte al màxim digital (0 dBFS).
/// Els valors per defecte són els de RadioDJ, perquè la biblioteca soni igual que ara (D-006).
/// </summary>
public sealed record CueThresholds(double StartDb = -25, double MixDb = -15, double EndDb = -30)
{
    public static CueThresholds Default { get; } = new();
}
