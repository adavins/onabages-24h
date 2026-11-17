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
