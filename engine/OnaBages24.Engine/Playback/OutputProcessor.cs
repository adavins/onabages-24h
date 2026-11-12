using OnaBages.Audio;

namespace OnaBages24.Engine.Playback;

/// <summary>Un preajust del compressor de sortida: com el «Suau» del processador de RadioDJ.</summary>
/// <param name="Id">Identificador del protocol i de la configuració desada (p. ex. <c>suau</c>).</param>
/// <param name="Name">Nom per a la interfície.</param>
/// <param name="ThresholdDb">Llindar: el que passa d'aquí es comprimeix.</param>
/// <param name="Ratio">Relació de compressió (1 = no comprimeix).</param>
/// <param name="AttackMs">Com de de pressa abaixa.</param>
/// <param name="ReleaseMs">Com de a poc a poc torna a pujar.</param>
/// <param name="MakeupDb">Guany que recupera part del que ha abaixat el compressor (se suma al volum).</param>
public sealed record ProcessorPreset(string Id, string Name, double ThresholdDb, double Ratio, double AttackMs, double ReleaseMs, double MakeupDb);

/// <summary>
/// Processador d'àudio de la sortida (fase 3, pas 6; D-086): un preajust del compressor i el volum de sortida, aplicats al
/// <see cref="Mixer.Processor"/>. Sempre hi ha el <b>sostre de seguretat</b> (<see cref="CeilingDb"/>), també amb
/// «Sense compressió»: si dues pistes fortes se solapen, la suma no passa mai de 0 dB i no distorsiona a la targeta.
///
/// Els valors dels preajusts són provisionals: es decidiran escoltant-ho al PC 24h (com el «Suau» de RadioDJ).
/// </summary>
public sealed class OutputProcessor
{
    /// <summary>Sostre de seguretat de la sortida (dBFS).</summary>
    public const double CeilingDb = -0.3;

    /// <summary>Marge del volum de sortida (dB).</summary>
    public const double MinVolumeDb = -20, MaxVolumeDb = 10;

    /// <summary>El preajust amb què arrenca el motor la primera vegada (com RadioDJ ara).</summary>
    public const string StandardPresetId = "suau";

    /// <summary>Els preajusts, de menys a més compressió.</summary>
    public static IReadOnlyList<ProcessorPreset> Presets { get; } =
    [
        new("cap", "Sense compressió", ThresholdDb: 0, Ratio: 1, AttackMs: 10, ReleaseMs: 500, MakeupDb: 0),
        new("suau", "Suau", ThresholdDb: -10, Ratio: 2, AttackMs: 20, ReleaseMs: 800, MakeupDb: 2),
        new("mitja", "Mitjà", ThresholdDb: -14, Ratio: 3, AttackMs: 10, ReleaseMs: 500, MakeupDb: 4),
        new("fort", "Fort", ThresholdDb: -18, Ratio: 4, AttackMs: 5, ReleaseMs: 300, MakeupDb: 6),
    ];

    private readonly DynamicsProcessor _processor;
    private readonly object _lock = new();

    public OutputProcessor(DynamicsProcessor processor)
    {
        _processor = processor;
        Configure(StandardPresetId, 0);
    }

    /// <summary>El preajust actual.</summary>
    public ProcessorPreset Preset { get; private set; } = Presets[0];

    /// <summary>El volum de sortida actual (dB; 0 = sense canvi).</summary>
    public double VolumeDb { get; private set; }

    /// <summary>Quan canvia el preajust o el volum (per desar-ho i avisar les interfícies).</summary>
    public event Action? Changed;

    /// <summary>El preajust amb aquest identificador, o null si no n'hi ha cap.</summary>
    public static ProcessorPreset? FindPreset(string? id) => Presets.FirstOrDefault(p => p.Id == id);

    /// <summary>Canvia el preajust i el volum. Dona error si el preajust no existeix o el volum és fora de marge.</summary>
    public void Configure(string presetId, double volumeDb)
    {
        var preset = FindPreset(presetId) ?? throw new ArgumentException($"No hi ha cap preajust «{presetId}».", nameof(presetId));
        if (!double.IsFinite(volumeDb) || volumeDb < MinVolumeDb || volumeDb > MaxVolumeDb)
            throw new ArgumentOutOfRangeException(nameof(volumeDb), $"El volum ha de ser entre {MinVolumeDb} i {MaxVolumeDb} dB.");
        bool changed;
        lock (_lock)
        {
            changed = preset != Preset || volumeDb != VolumeDb;
            Preset = preset;
            VolumeDb = volumeDb;
            _processor.Settings = SettingsFor(preset, volumeDb);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Els paràmetres del compressor per a un preajust i un volum. Sempre activat, pel sostre de seguretat.</summary>
    public static DynamicsSettings SettingsFor(ProcessorPreset preset, double volumeDb) => new(
        Enabled: true,
        InputGainDb: 0,
        ThresholdDb: preset.ThresholdDb,
        Ratio: preset.Ratio,
        AttackMs: preset.AttackMs,
        ReleaseMs: preset.ReleaseMs,
        OutputGainDb: preset.MakeupDb + volumeDb,
        StereoLink: true, // els dos canals igual: la imatge estèreo no es mou
        CeilingDb: CeilingDb);

    /// <summary>L'abaixada màxima (compressor + sostre) des de l'última lectura, en dB (≥ 0). Per a l'indicador.</summary>
    public float TakeReductionDb() => _processor.TakeMaxReductionDb();
}
