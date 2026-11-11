namespace OnaBages.Audio;

/// <summary>Estat d'una veu del mesclador.</summary>
public enum VoiceState
{
    /// <summary>Lliure: es pot agafar per a una pista nova.</summary>
    Idle = 0,
    /// <summary>Agafada pel fil de control, preparant-se (s'omple la memòria); encara no està programada.</summary>
    Reserved = 1,
    /// <summary>Programada: començarà a sonar al fotograma d'inici.</summary>
    Scheduled = 2,
    /// <summary>Sonant.</summary>
    Playing = 3,
    /// <summary>Ha acabat (final programat, fos acabat o fi de les dades). El fil de control l'ha d'alliberar.</summary>
    Finished = 4,
}

/// <summary>
/// Una veu del mesclador (docs/disseny-motor.md, seccions 2 i 3): una pista, un senyal horari, un stream…
///
/// Qui la fa servir:
/// - el <b>fil de control</b> la reserva, l'omple (a través de <see cref="Buffer"/>, des d'un fil de lectura),
///   li programa l'inici i el final en <b>fotogrames</b> del mesclador i, quan acaba, l'allibera;
/// - el <b>fil d'àudio</b> (dins de <see cref="Mixer"/>) la llegeix: comença i acaba a la mostra exacta, aplica
///   el guany i els fosos, i no crea objectes ni espera mai.
///
/// Les ordres del fil de control es deixen en camps (<c>Volatile</c> / <c>Interlocked</c>) que el fil d'àudio
/// recull al bloc següent. Com que tot va en fotogrames, no importa si el fil de control arriba uns mil·lisegons tard.
/// </summary>
public sealed class MixerVoice
{
    /// <summary>Atenuació on comencen (fos d'entrada) o acaben (fos de sortida) els fosos: −60 dB, com a Play.</summary>
    public const double FadeFloorDb = -60;

    private int _state;                    // VoiceState
    private long _startFrame;              // fotograma del mesclador on ha de començar
    private int _fadeInFrames;
    private long _fadeOutStart = long.MaxValue; // fotograma on comença el fos de sortida (o l'aturada, si no n'hi ha)
    private int _fadeOutFrames;
    private volatile float _gain = 1f;
    private volatile bool _overlay;
    private volatile bool _sourceFinished;
    private long _framesPlayed;
    private long _actualStartFrame = -1;
    private int _underruns;

    // Només el fil d'àudio
    private float _currentGain = 1f;
    private float _fadeInLevel = 1f, _fadeInStep = 1f;
    private int _fadeInLeft;
    private float _fadeOutLevel = 1f, _fadeOutStep = 1f;
    private int _fadeOutLeft = -1;         // -1 = el fos encara no ha començat

    /// <param name="index">Número de la veu (per als registres i les proves).</param>
    /// <param name="bufferSamples">Mida de la memòria intermèdia, en mostres (estèreo intercalat).</param>
    public MixerVoice(int index, int bufferSamples)
    {
        Index = index;
        Buffer = new SampleRingBuffer(bufferSamples);
    }

    public int Index { get; }

    /// <summary>Memòria intermèdia de la veu (estèreo intercalat). Hi escriu un sol fil de lectura.</summary>
    public SampleRingBuffer Buffer { get; }

    /// <summary>Nivell de pic del que ha sonat (després de guany i fosos).</summary>
    public PeakMeter Meter { get; } = new();

    public VoiceState State => (VoiceState)Volatile.Read(ref _state);

    /// <summary>Fotogrames que han sonat (sense comptar el silenci d'un tall).</summary>
    public long FramesPlayed => Interlocked.Read(ref _framesPlayed);

    /// <summary>Fotograma del mesclador on ha començat a sonar de debò (-1 = encara no). Si és més tard que el programat, el fil de control va arribar tard.</summary>
    public long ActualStartFrame => Interlocked.Read(ref _actualStartFrame);

    /// <summary>Vegades que la memòria s'ha buidat abans d'hora (el disc no ha anat prou ràpid). Ha de ser 0.</summary>
    public int Underruns => Volatile.Read(ref _underruns);

    /// <summary>
    /// Veu d'un reproductor auxiliar (senyals horaris, fase 2, pas 5): sona per sobre de la resta, i l'atenuació del
    /// mesclador (<see cref="Mixer.SetDuck"/>) no l'afecta. Es posa després de reservar-la i abans de programar-la.
    /// </summary>
    public bool Overlay
    {
        get => _overlay;
        set => _overlay = value;
    }

    /// <summary>Guany lineal (1 = tal com és). S'hi arriba amb una rampa curta, sense clics.</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Max(0f, value);
    }

    // --- Fil de control ---

    /// <summary>Reserva la veu (ha d'estar lliure) i la deixa buida, a punt per omplir-la.</summary>
    internal bool TryReserve()
    {
        if (Interlocked.CompareExchange(ref _state, (int)VoiceState.Reserved, (int)VoiceState.Idle) != (int)VoiceState.Idle)
            return false;
        Buffer.Clear(); // ni el fil d'àudio ni cap lector no hi treballen: la veu era lliure
        _sourceFinished = false;
        Interlocked.Exchange(ref _framesPlayed, 0);
        Interlocked.Exchange(ref _actualStartFrame, -1);
        Volatile.Write(ref _underruns, 0);
        Interlocked.Exchange(ref _fadeOutStart, long.MaxValue);
        _gain = 1f;
        _overlay = false;
        return true;
    }

    /// <summary>
    /// Programa l'inici: la veu començarà a sonar exactament al fotograma <paramref name="atFrame"/> del mesclador
    /// (si ja ha passat, al bloc següent). Abans cal haver omplert la memòria.
    /// </summary>
    /// <param name="fadeInFrames">Fos d'entrada (0 = sense).</param>
    public void Start(long atFrame, int fadeInFrames = 0)
    {
        // També es pot tornar a programar una veu que encara no ha començat (p. ex. per avançar-la). Si mentrestant
        // ja ha començat a sonar, el canvi no té efecte.
        if (State is not (VoiceState.Reserved or VoiceState.Scheduled))
            throw new InvalidOperationException($"La veu {Index} no està reservada (estat: {State}).");
        Interlocked.Exchange(ref _startFrame, atFrame);
        Volatile.Write(ref _fadeInFrames, Math.Max(0, fadeInFrames));
        Volatile.Write(ref _state, (int)VoiceState.Scheduled); // l'últim: el fil d'àudio ja veu els valors
    }

    /// <summary>
    /// Programa el final: al fotograma <paramref name="atFrame"/> comença el fos de sortida, que dura <paramref name="fadeFrames"/>
    /// (0 = s'atura de cop). Quan acaba, la veu passa a <see cref="VoiceState.Finished"/>. Es pot tornar a cridar
    /// (p. ex. per avançar-lo); un fos que ja ha començat no es reprograma.
    /// </summary>
    public void Stop(long atFrame, int fadeFrames = 0)
    {
        Volatile.Write(ref _fadeOutFrames, Math.Max(0, fadeFrames));
        Interlocked.Exchange(ref _fadeOutStart, atFrame); // després del nombre de fotogrames: el fil d'àudio llegeix aquest primer
    }

    /// <summary>
    /// Desprograma una veu que encara no ha començat a sonar: torna a quedar reservada (i es pot alliberar).
    /// Retorna false si ja sona (llavors cal aturar-la amb <see cref="Stop"/> i esperar que acabi).
    /// </summary>
    public bool Cancel()
    {
        int seen = Interlocked.CompareExchange(ref _state, (int)VoiceState.Reserved, (int)VoiceState.Scheduled);
        return seen is (int)VoiceState.Scheduled or (int)VoiceState.Reserved;
    }

    /// <summary>El fil de lectura avisa que ja ha escrit totes les dades: quan s'acabin, la veu ha acabat (no és un tall).</summary>
    public void MarkSourceFinished() => _sourceFinished = true;

    /// <summary>Allibera la veu perquè es pugui tornar a fer servir. Només si no sona (reservada o acabada) i sense cap fil de lectura que hi escrigui.</summary>
    internal bool TryRelease()
    {
        int state = Volatile.Read(ref _state);
        if (state is not ((int)VoiceState.Reserved or (int)VoiceState.Finished)) return false;
        return Interlocked.CompareExchange(ref _state, (int)VoiceState.Idle, state) == state;
    }

    // --- Fil d'àudio ---

    /// <summary>
    /// Fil d'àudio: suma la veu al bloc <paramref name="mix"/> (estèreo intercalat, <paramref name="frames"/> fotogrames),
    /// que comença al fotograma <paramref name="blockStart"/> del mesclador. <paramref name="scratch"/> és una memòria
    /// de treball del mesclador (com a mínim <paramref name="frames"/> · 2 mostres).
    /// </summary>
    internal void MixInto(Span<float> mix, Span<float> scratch, long blockStart, int frames)
    {
        int state = Volatile.Read(ref _state);
        if (state == (int)VoiceState.Scheduled)
        {
            long start = Interlocked.Read(ref _startFrame);
            if (start >= blockStart + frames) return; // encara no
            BeginPlaying(Math.Max(start, blockStart));
            state = (int)VoiceState.Playing;
        }
        if (state != (int)VoiceState.Playing) return;

        long actualStart = Interlocked.Read(ref _actualStartFrame);
        int offset = (int)Math.Max(0, actualStart - blockStart); // on comença dins d'aquest bloc
        long position = blockStart + offset;

        // On s'acaba la veu (fotograma del mesclador). Si el fos s'havia de començar en un fotograma que ja ha
        // passat (el fil de control ha arribat tard), comença ara i dura sencer.
        long fadeOutStart = Interlocked.Read(ref _fadeOutStart);
        int fadeOutFrames = Volatile.Read(ref _fadeOutFrames);
        long end;
        if (_fadeOutLeft >= 0) end = position + _fadeOutLeft;                 // el fos ja ha començat
        else if (fadeOutStart == long.MaxValue) end = long.MaxValue;         // sense final programat
        else end = Math.Max(fadeOutStart, position) + fadeOutFrames;
        if (end <= position)
        {
            Finish();
            return;
        }
        int wanted = (int)Math.Min(frames - offset, end - position);

        Span<float> samples = scratch[..(wanted * 2)];
        int got = Buffer.Read(samples) / 2;
        bool dataEnded = false;
        if (got < wanted)
        {
            if (_sourceFinished && Buffer.Count == 0)
                dataEnded = true; // final de les dades: no és un tall
            else
                Interlocked.Increment(ref _underruns); // tall: la resta del bloc, silenci
        }

        ApplyEnvelope(samples[..(got * 2)], position, Math.Max(fadeOutStart, position), fadeOutFrames);
        Meter.AddInterleaved(samples[..(got * 2)], 2);

        Span<float> target = mix.Slice(offset * 2, got * 2);
        for (int i = 0; i < target.Length; i++) target[i] += samples[i];
        Interlocked.Add(ref _framesPlayed, got);

        if (dataEnded || position + got >= end) Finish();
    }

    private void BeginPlaying(long atFrame)
    {
        Interlocked.Exchange(ref _actualStartFrame, atFrame);
        _currentGain = _gain;
        int fadeIn = Volatile.Read(ref _fadeInFrames);
        _fadeInLeft = fadeIn;
        if (fadeIn > 0)
        {
            _fadeInLevel = (float)Math.Pow(10, FadeFloorDb / 20);
            _fadeInStep = (float)Math.Pow(10, -FadeFloorDb / 20 / fadeIn); // constant en dB
        }
        _fadeOutLeft = -1;
        Volatile.Write(ref _state, (int)VoiceState.Playing);
    }

    /// <summary>Guany (amb rampa), fos d'entrada i fos de sortida, mostra a mostra, sense objectes nous.</summary>
    private void ApplyEnvelope(Span<float> stereo, long firstFrame, long fadeOutStart, int fadeOutFrames)
    {
        int frames = stereo.Length / 2;
        float target = _gain;
        float gain = _currentGain;
        float gainStep = frames > 0 ? (target - gain) / frames : 0;

        for (int i = 0; i < frames; i++)
        {
            gain += gainStep;
            float level = gain;

            if (_fadeInLeft > 0)
            {
                level *= _fadeInLevel;
                _fadeInLevel *= _fadeInStep;
                if (--_fadeInLeft == 0) _fadeInLevel = 1f;
            }

            if (_fadeOutLeft < 0 && firstFrame + i >= fadeOutStart)
            {
                // Comença el fos de sortida (o l'aturada de cop, si dura 0)
                _fadeOutLeft = fadeOutFrames;
                _fadeOutLevel = 1f;
                _fadeOutStep = fadeOutFrames > 0 ? (float)Math.Pow(10, FadeFloorDb / 20 / fadeOutFrames) : 0f;
            }
            if (_fadeOutLeft >= 0)
            {
                if (_fadeOutLeft == 0)
                {
                    level = 0;
                }
                else
                {
                    level *= _fadeOutLevel;
                    _fadeOutLevel *= _fadeOutStep;
                    _fadeOutLeft--;
                }
            }

            stereo[i * 2] *= level;
            stereo[i * 2 + 1] *= level;
        }
        _currentGain = target;
    }

    private void Finish() => Volatile.Write(ref _state, (int)VoiceState.Finished);
}
