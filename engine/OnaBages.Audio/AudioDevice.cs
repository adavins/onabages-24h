namespace OnaBages.Audio;

/// <summary>Sentit del dispositiu: sortida (reproduir) o entrada (enregistrar).</summary>
public enum DeviceKind
{
    Output,
    Input,
}

/// <summary>Estat del dispositiu segons Windows.</summary>
public enum DeviceStatus
{
    /// <summary>Funciona i es pot fer servir.</summary>
    Active,
    /// <summary>Desactivat a Windows (Tauler de control → So).</summary>
    Disabled,
    /// <summary>La targeta hi és, però no hi ha res connectat al connector.</summary>
    Unplugged,
}

/// <summary>Format del dispositiu en mode compartit (el que es tria a Windows).</summary>
public sealed record DeviceFormat(int SampleRate, int Bits, int Channels);

/// <summary>
/// Tipus de connector, segons Windows (<c>PKEY_AudioEndpoint_FormFactor</c>). Serveix per triar la sortida
/// d'emergència (D-038): primer les analògiques, després les digitals i, al final, HDMI.
/// </summary>
public enum DeviceConnector
{
    Unknown,
    /// <summary>Altaveus, sortida de línia, auriculars.</summary>
    Analog,
    /// <summary>S/PDIF o un altre connector digital.</summary>
    Digital,
    /// <summary>HDMI o DisplayPort (pantalla).</summary>
    Hdmi,
    /// <summary>Dispositiu de xarxa o remot (p. ex. l'àudio de l'escriptori remot).</summary>
    Remote,
}

/// <summary>
/// Un dispositiu d'àudio de Windows.
/// L'<see cref="Id"/> és l'identificador estable de Windows: és el que es desa a la configuració.
/// </summary>
public sealed record AudioDevice(
    string Id,
    string Name,
    DeviceKind Kind,
    DeviceStatus Status,
    DeviceFormat? Format,
    DeviceConnector Connector = DeviceConnector.Unknown);
