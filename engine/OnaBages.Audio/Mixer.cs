using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace OnaBages.Audio;

/// <summary>
/// Mesclador intern d'Ona Bages 24h (D-005, D-039; docs/disseny-motor.md): suma les veus que sonen i les envia a
/// <b>una sola sortida</b>. És el que llegeix la sortida de Windows i, per tant, l'únic codi que corre al fil d'àudio:
/// no crea objectes, no espera, no accedeix al disc ni escriu registres.
///
/// Compta els fotogrames que han sortit (<see cref="FramesRendered"/>): és el rellotge amb què es programen les veus.
/// </summary>
public sealed class Mixer : IWaveProvider
{
    /// <summary>Fotogrames que es mesclen de cop, com a molt (un bloc més gran es fa a trossos).</summary>
    public const int MaxChunkFrames = 8192;

    private readonly MixerVoice[] _voices;
    private readonly float[] _mix = new float[MaxChunkFrames * 2];
    private readonly float[] _scratch = new float[MaxChunkFrames * 2];
    private readonly int _outChannels;
    private long _framesRendered;
    private long _lastRenderTimestamp;
    private readonly FrameStamp _snapshot = new(); // la parella (fotogrames, hora) de l'últim bloc

    /// <param name="sampleRate">Freqüència de la sortida (la del mesclador de Windows).</param>
    /// <param name="outChannels">Canals de la sortida (normalment 2; si en té més, els altres van en silenci).</param>
    /// <param name="voiceCount">Nombre de veus (D-039: 8).</param>
    /// <param name="bufferSeconds">Memòria intermèdia de cada veu, en segons (D-039: 5).</param>
    public Mixer(int sampleRate, int outChannels = 2, int voiceCount = 8, int bufferSeconds = 5)
    {
        if (outChannels < 1) throw new ArgumentOutOfRangeException(nameof(outChannels));
        SampleRate = sampleRate;
        _outChannels = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, outChannels);
        _voices = new MixerVoice[voiceCount];
        for (int i = 0; i < voiceCount; i++)
            _voices[i] = new MixerVoice(i + 1, sampleRate * 2 * bufferSeconds);
    }

    public WaveFormat WaveFormat { get; }

    public int SampleRate { get; }

    public IReadOnlyList<MixerVoice> Voices => _voices;

    /// <summary>Nivell de pic de la sortida (la suma de totes les veus).</summary>
    public PeakMeter Meter { get; } = new();

    /// <summary>Fotogrames (mostres per canal) que han sortit des que es va crear el mesclador.</summary>
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Quan va mesclar l'últim bloc (marca de <see cref="Stopwatch"/>; 0 = mai). Per saber si el fil d'àudio funciona.</summary>
    public long LastRenderTimestamp => Interlocked.Read(ref _lastRenderTimestamp);

    /// <summary>Temps des de l'últim bloc mesclat (infinit si encara no n'ha mesclat cap).</summary>
    public TimeSpan SinceLastRender
    {
        get
        {
            long last = LastRenderTimestamp;
            return last == 0 ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(last);
        }
    }

    /// <summary>
    /// Fil de control: quants fotogrames s'havien mesclat en acabar l'últim bloc, i quan (marca de <see cref="Stopwatch"/>).
    /// Les dues dades són del mateix bloc. Timestamp 0 vol dir que encara no s'ha mesclat res.
    /// </summary>
    public (long Frames, long Timestamp) RenderSnapshot() => _snapshot.Read();

    /// <summary>Fil de control: agafa una veu lliure (o null si no n'hi ha cap). La veu queda reservada i buida.</summary>
    public MixerVoice? AcquireVoice()
    {
        foreach (var voice in _voices)
        {
            if (voice.TryReserve()) return voice;
        }
        return null;
    }

    /// <summary>
    /// Fil de control: torna una veu que ja no sona (reservada sense fer servir, o acabada). Abans cal haver aturat
    /// el fil que hi escrivia. Retorna false si encara sona (cal aturar-la i esperar que acabi).
    /// </summary>
    public bool ReleaseVoice(MixerVoice voice) => voice.TryRelease();

    /// <summary>Fil d'àudio de Windows. Sempre omple tot el que es demana (amb silenci si no sona res).</summary>
    public int Read(Span<byte> buffer)
    {
        Span<float> output = MemoryMarshal.Cast<byte, float>(buffer);
        int totalFrames = output.Length / _outChannels;
        int done = 0;
        while (done < totalFrames)
        {
            int frames = Math.Min(MaxChunkFrames, totalFrames - done);
            Span<float> mix = _mix.AsSpan(0, frames * 2);
            mix.Clear();

            long blockStart = Interlocked.Read(ref _framesRendered);
            foreach (var voice in _voices)
                voice.MixInto(mix, _scratch, blockStart, frames);

            Meter.AddInterleaved(mix, 2);
            WriteOutput(mix, output.Slice(done * _outChannels, frames * _outChannels), frames);

            Interlocked.Add(ref _framesRendered, frames);
            done += frames;
        }
        long now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastRenderTimestamp, now);

        // La parella per al rellotge de mostres, escrita de manera que l'altre fil no en pugui llegir una de mig feta
        _snapshot.Write(Interlocked.Read(ref _framesRendered), now);
        return buffer.Length;
    }

    /// <summary>Copia la mescla estèreo a la sortida: tal qual (2 canals), barrejada (1 canal) o amb els altres canals en silenci.</summary>
    private void WriteOutput(ReadOnlySpan<float> stereo, Span<float> output, int frames)
    {
        if (_outChannels == 2)
        {
            stereo.CopyTo(output);
            return;
        }
        for (int i = 0; i < frames; i++)
        {
            float l = stereo[i * 2], r = stereo[i * 2 + 1];
            int o = i * _outChannels;
            if (_outChannels == 1)
            {
                output[o] = (l + r) * 0.5f;
                continue;
            }
            output[o] = l;
            output[o + 1] = r;
            output.Slice(o + 2, _outChannels - 2).Clear();
        }
    }
}
