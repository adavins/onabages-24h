namespace OnaBages.Audio;

/// <summary>
/// Memòria intermèdia circular de mostres (float) entre dos fils:
/// un sol fil hi escriu (el de lectura del fitxer) i un sol fil en llegeix (el d'àudio).
///
/// Cap dels dos no s'espera mai: si no hi ha lloc, s'escriu menys; si no hi ha dades,
/// es llegeix menys. No crea objectes després del constructor i no fa servir bloquejos,
/// de manera que es pot fer servir des del fil d'àudio.
/// </summary>
public sealed class SampleRingBuffer
{
    private readonly float[] _buffer;

    // Comptadors totals de mostres escrites i llegides (no es reinicien mai a zero
    // en ús normal; la posició dins del vector és el comptador mòdul la mida).
    private long _written;
    private long _read;

    public SampleRingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new float[capacity];
    }

    /// <summary>Nombre màxim de mostres que hi caben.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Mostres disponibles per llegir.</summary>
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Lloc lliure per escriure.</summary>
    public int Free => Capacity - Count;

    /// <summary>
    /// Escriu tantes mostres com hi càpiguen. Només l'ha de cridar el fil que escriu.
    /// Retorna quantes se n'han escrit.
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        long written = _written;
        long read = Volatile.Read(ref _read);
        int free = Capacity - (int)(written - read);
        int count = Math.Min(free, samples.Length);
        if (count == 0) return 0;

        int start = (int)(written % Capacity);
        int first = Math.Min(count, Capacity - start);
        samples[..first].CopyTo(_buffer.AsSpan(start, first));
        samples[first..count].CopyTo(_buffer.AsSpan(0, count - first));

        // Les dades ja són al seu lloc abans que el lector vegi el comptador nou.
        Volatile.Write(ref _written, written + count);
        return count;
    }

    /// <summary>
    /// Llegeix tantes mostres com n'hi hagi (fins a omplir <paramref name="destination"/>).
    /// Només l'ha de cridar el fil que llegeix. Retorna quantes se n'han llegit.
    /// </summary>
    public int Read(Span<float> destination)
    {
        long read = _read;
        long written = Volatile.Read(ref _written);
        int available = (int)(written - read);
        int count = Math.Min(available, destination.Length);
        if (count == 0) return 0;

        int start = (int)(read % Capacity);
        int first = Math.Min(count, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);

        Volatile.Write(ref _read, read + count);
        return count;
    }

    /// <summary>Mostres llegides des del principi (comptador total). Per situar-se dins de la memòria (D-154).</summary>
    public long ReadCount => Volatile.Read(ref _read);

    /// <summary>
    /// Copia les mostres a partir de la posició absoluta <paramref name="index"/> (comptador total, com <see cref="ReadCount"/>)
    /// sense consumir-les (D-154). Només si encara hi són: ni sobreescrites ni per escriure. Retorna false si no.
    /// Pensat per mirar la memòria d'una pista que ja no rep dades (el fil que escriu, aturat).
    /// </summary>
    public bool Peek(long index, Span<float> destination)
    {
        long written = Volatile.Read(ref _written);
        if (index < 0 || index < written - Capacity || index + destination.Length > written) return false;
        int start = (int)(index % Capacity);
        int first = Math.Min(destination.Length, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
        return true;
    }

    /// <summary>
    /// Descarta fins a <paramref name="samples"/> mostres sense llegir-les (D-153). Només l'ha de cridar el fil que llegeix.
    /// Retorna quantes se n'han descartat.
    /// </summary>
    public int Skip(int samples)
    {
        long read = _read;
        int count = Math.Min((int)(Volatile.Read(ref _written) - read), Math.Max(0, samples));
        Volatile.Write(ref _read, read + count);
        return count;
    }

    /// <summary>
    /// Buida la memòria. Només es pot cridar quan cap dels dos fils no hi treballa
    /// (per exemple, amb la sortida aturada i el fil de lectura parat).
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _read, Volatile.Read(ref _written));
    }
}
