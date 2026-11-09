using NAudio.CoreAudioApi;

namespace OnaBages.Audio;

/// <summary>
/// Llegeix de Windows la llista de dispositius d'àudio (sortides i entrades).
/// No obre cap flux d'àudio: només consulta.
/// </summary>
public static class DeviceList
{
    /// <summary>
    /// Retorna tots els dispositius actius, desactivats i desconnectats.
    /// Els que Windows recorda però que ja no hi són («no presents») no s'hi inclouen.
    /// Si un dispositiu dona error en llegir-lo, se salta i la resta de la llista continua.
    /// </summary>
    public static IReadOnlyList<AudioDevice> GetAll()
    {
        var result = new List<AudioDevice>();
        var states = DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged;

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, states))
        {
            using (device)
            {
                try
                {
                    result.Add(Read(device));
                }
                catch (Exception)
                {
                    // Un controlador defectuós no pot impedir llistar els altres dispositius.
                }
            }
        }

        return result;
    }

    private static AudioDevice Read(MMDevice device)
    {
        var kind = device.DataFlow == DataFlow.Render ? DeviceKind.Output : DeviceKind.Input;

        var status = device.State switch
        {
            DeviceState.Active => DeviceStatus.Active,
            DeviceState.Disabled => DeviceStatus.Disabled,
            _ => DeviceStatus.Unplugged,
        };

        return new AudioDevice(device.ID, device.FriendlyName, kind, status, ReadFormat(device), ReadConnector(device));
    }

    /// <summary>Tipus de connector (D-038), segons el «form factor» de Windows.</summary>
    private static DeviceConnector ReadConnector(MMDevice device)
    {
        try
        {
            var key = PropertyKeys.PKEY_AudioEndpoint_FormFactor;
            if (!device.Properties.Contains(key)) return DeviceConnector.Unknown;
            return ConnectorFromFormFactor(Convert.ToInt32(device.Properties[key].Value));
        }
        catch (Exception)
        {
            return DeviceConnector.Unknown;
        }
    }

    /// <summary>Valors de l'enumeració EndpointFormFactor de Windows. Pública per provar-la sense maquinari.</summary>
    public static DeviceConnector ConnectorFromFormFactor(int formFactor) => formFactor switch
    {
        0 => DeviceConnector.Remote,               // RemoteNetworkDevice
        1 or 2 or 3 or 5 => DeviceConnector.Analog, // Speakers, LineLevel, Headphones, Headset
        7 or 8 => DeviceConnector.Digital,         // UnknownDigitalPassthrough, SPDIF
        9 => DeviceConnector.Hdmi,                 // DigitalAudioDisplayDevice
        _ => DeviceConnector.Unknown,
    };

    /// <summary>
    /// Format triat a Windows (Propietats → Opcions avançades). Si no n'hi ha cap de desat,
    /// es fa servir el predeterminat del fabricant. Retorna null si no es pot saber.
    /// </summary>
    private static DeviceFormat? ReadFormat(MMDevice device)
    {
        var properties = device.Properties;
        foreach (var key in new[] { PropertyKeys.PKEY_AudioEngine_DeviceFormat, PropertyKeys.PKEY_AudioEngine_OEMFormat })
        {
            if (properties.Contains(key) && properties[key].Value is byte[] blob)
            {
                var format = ParseWaveFormat(blob);
                if (format is not null) return format;
            }
        }
        return null;
    }

    /// <summary>
    /// Interpreta una estructura WAVEFORMATEX (o WAVEFORMATEXTENSIBLE) de Windows.
    /// Pública perquè es pugui provar sense maquinari.
    /// </summary>
    public static DeviceFormat? ParseWaveFormat(byte[] data)
    {
        if (data.Length < 16) return null;

        const ushort WaveFormatExtensible = 0xFFFE;
        ushort tag = BitConverter.ToUInt16(data, 0);
        int channels = BitConverter.ToUInt16(data, 2);
        int sampleRate = BitConverter.ToInt32(data, 4);
        int bits = BitConverter.ToUInt16(data, 14);

        // En el format ampliat, els bits «vàlids» (p. ex. 24 dins de 32) són els que compten.
        if (tag == WaveFormatExtensible && data.Length >= 20)
        {
            int validBits = BitConverter.ToUInt16(data, 18);
            if (validBits > 0) bits = validBits;
        }

        if (channels <= 0 || sampleRate <= 0 || bits <= 0) return null;
        return new DeviceFormat(sampleRate, bits, channels);
    }
}
