using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace OnaBages.Audio;

/// <summary>
/// Connecta el <see cref="Mixer"/> (sempre estèreo i a la seva freqüència) amb una sortida concreta, que pot tenir una
/// altra freqüència (p. ex. una targeta d'emergència a 44,1 kHz, D-038) o més canals:
/// - si la freqüència és la mateixa, només reparteix els canals (sense remostrejar, sense objectes nous);
/// - si no, remostreja (NAudio <see cref="WdlResamplingSampleProvider"/>; crea les seves memòries la primera vegada).
/// Els fotogrames del mesclador continuen sent el rellotge, sigui quina sigui la sortida.
/// </summary>
public sealed class OutputAdapter : IWaveProvider
{
    private readonly int _channels;
    private readonly IWaveProvider? _direct;    // mateixa freqüència
    private readonly ISampleProvider? _resampled; // freqüència diferent
    private readonly float[] _stereo;

    public OutputAdapter(Mixer mixer, int sampleRate, int channels)
    {
        if (mixer.WaveFormat.Channels != 2) throw new ArgumentException("El mesclador ha de ser estèreo.", nameof(mixer));
        _channels = Math.Max(1, channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _channels);
        if (sampleRate == mixer.SampleRate)
            _direct = mixer;
        else
            _resampled = new WdlResamplingSampleProvider(new WaveToSampleProvider(mixer), sampleRate);
        _stereo = new float[sampleRate * 2]; // 1 s: més que qualsevol bloc de la sortida
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Si cal remostrejar (la sortida no va a la freqüència del mesclador).</summary>
    public bool Resampling => _resampled is not null;

    /// <summary>Fil d'àudio de Windows.</summary>
    public int Read(Span<byte> buffer)
    {
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer);
        int frames = output.Length / _channels;

        if (_direct is not null && _channels == 2)
            return _direct.Read(buffer);

        int done = 0;
        while (done < frames)
        {
            int chunk = Math.Min(frames - done, _stereo.Length / 2);
            Span<float> stereo = _stereo.AsSpan(0, chunk * 2);
            if (_direct is not null)
            {
                _direct.Read(MemoryMarshal.AsBytes(stereo));
            }
            else
            {
                int got = 0;
                while (got < stereo.Length)
                {
                    int n = _resampled!.Read(stereo[got..]);
                    if (n == 0) break;
                    got += n;
                }
                stereo[got..].Clear();
            }

            Span<float> target = output.Slice(done * _channels, chunk * _channels);
            for (int i = 0; i < chunk; i++)
            {
                float l = stereo[i * 2], r = stereo[i * 2 + 1];
                int o = i * _channels;
                if (_channels == 1)
                {
                    target[o] = (l + r) * 0.5f;
                    continue;
                }
                target[o] = l;
                target[o + 1] = r;
                if (_channels > 2) target.Slice(o + 2, _channels - 2).Clear();
            }
            done += chunk;
        }
        return buffer.Length;
    }
}
