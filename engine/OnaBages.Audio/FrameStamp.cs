using System.Diagnostics;

namespace OnaBages.Audio;

/// <summary>
/// Una parella (fotogrames, marca de temps de <see cref="Stopwatch"/>) que escriu el fil d'àudio i llegeix el fil de
/// control, sense bloquejar cap dels dos. L'altre fil mai no en pot llegir una de mig escrita: hi ha un comptador de
/// versió que és senar mentre s'escriu (si canvia durant la lectura, es torna a llegir).
/// </summary>
public sealed class FrameStamp
{
    private int _version;
    private long _frames;
    private long _timestamp;

    /// <summary>Fil d'àudio: desa la parella. No crea objectes ni espera.</summary>
    public void Write(long frames, long timestamp)
    {
        Interlocked.Increment(ref _version);
        Interlocked.Exchange(ref _frames, frames);
        Interlocked.Exchange(ref _timestamp, timestamp);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Qualsevol altre fil: l'última parella desada. Timestamp 0 vol dir que encara no se n'ha desat cap.</summary>
    public (long Frames, long Timestamp) Read()
    {
        while (true)
        {
            int before = Volatile.Read(ref _version);
            if ((before & 1) == 0)
            {
                long frames = Interlocked.Read(ref _frames);
                long timestamp = Interlocked.Read(ref _timestamp);
                if (Volatile.Read(ref _version) == before) return (frames, timestamp);
            }
            Thread.SpinWait(10);
        }
    }
}
