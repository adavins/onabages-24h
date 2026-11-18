namespace OnaBages.Audio;

/// <summary>
/// El que omple una veu del mesclador: un fitxer (<see cref="VoiceReader"/>) o un stream de ràdio (<see cref="StreamVoiceReader"/>,
/// D-175). La cua els tracta igual: espera que siguin a punt, programa la veu i, quan s'acaba, els atura.
/// </summary>
public interface IVoiceReader : IDisposable
{
    MixerVoice Voice { get; }

    ReaderState State { get; }

    /// <summary>Per què ha fallat (en català), o null.</summary>
    string? Error { get; }

    /// <summary>Quant dura (el fitxer sencer, o la durada prevista d'un stream), o null si encara no se sap.</summary>
    TimeSpan? FileDuration { get; }

    /// <summary>Demana que s'aturi sense esperar el fil: ja no escriurà res més a la veu. Després cal <see cref="IDisposable.Dispose"/>.</summary>
    void Cancel();

    /// <summary>Deixa de llegir, com si el disc o la xarxa no responguessin (per a les proves).</summary>
    void PauseReading();

    /// <summary>Torna a llegir després de <see cref="PauseReading"/>.</summary>
    void ResumeReading();
}

/// <summary>
/// Una font en directe que omple una veu: un stream de ràdio (<see cref="StreamVoiceReader"/>) o una entrada de línia
/// (<see cref="InputVoiceReader"/>, D-185). La cua les tracta igual: es poden tallar i recuperar, poden arribar en silenci i
/// tenen la reconnexió durant tota la durada (D-031).
/// </summary>
public interface ILiveVoiceReader : IVoiceReader
{
    /// <summary>Quant fa que arriba en silenci, seguit.</summary>
    TimeSpan SilentFor { get; }

    /// <summary>El títol que envia la font (el d'un stream), o null.</summary>
    string? Title { get; }

    /// <summary>Si ara està connectada i rebent so.</summary>
    bool Connected { get; }

    /// <summary>Per què ha fallat l'últim intent de connexió, o null.</summary>
    string? LastError { get; }

    /// <summary>Els talls i les recuperacions des de l'última vegada (per a les incidències).</summary>
    IReadOnlyList<StreamNotice> TakeNotices();
}
