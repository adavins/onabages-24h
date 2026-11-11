using OnaBages.Audio;

namespace OnaBages24.Engine.Playback;

/// <summary>Un auxiliar ha començat a sonar. <paramref name="TargetFrame"/>: el fotograma on havia de començar, si n'hi havia cap.</summary>
public sealed record AuxStarted(int Player, QueueItem Item, long StartFrame, long? TargetFrame);

/// <summary>Un auxiliar ha acabat: sencer, aturat o substituït per un altre al mateix reproductor.</summary>
public sealed record AuxEnded(int Player, QueueItem Item, TimeSpan Played, bool Complete);

/// <summary>No s'ha pogut fer sonar un auxiliar (el fitxer no s'obre). La música continua: no hi ha silenci.</summary>
public sealed record AuxFailed(int Player, QueueItem Item, string Message);

/// <summary>Què fa un reproductor auxiliar (per a la interfície). <paramref name="Started"/> és fals si encara espera la seva hora.</summary>
public sealed record AuxStatus(int Player, QueueItem Item, bool Started, TimeSpan Elapsed, TimeSpan Remaining, double? DuckingDb);

/// <summary>
/// Reproductors auxiliars (fase 2, pas 5; docs/planificador.md, seccions 2 i 4): fan sonar un fitxer <b>per sobre</b> del
/// que sona a la cua (els senyals horaris, sobretot), sense tallar-lo ni tocar la cua. N'hi ha <see cref="Count"/>, com a
/// RadioDJ; el 1 és el dels senyals horaris.
///
/// - Cada auxiliar fa servir una veu del mesclador marcada com a <see cref="MixerVoice.Overlay"/>.
/// - El fitxer sona sencer, des del principi (sense punts d'entrada): els senyals comencen exactament a l'hora (D-064).
/// - Amb atenuació (<c>duckingDb</c>), la música baixa en els <see cref="DuckDown"/> d'abans de començar, perquè el
///   primer so ja la trobi atenuada, i torna a pujar en <see cref="DuckUp"/> quan s'acaba.
/// - Si el reproductor ja sonava, el nou substitueix l'anterior (amb un fos curt, <see cref="ReplaceFade"/>).
///
/// Es pot obrir per endavant (<see cref="Prepare"/>, uns 30 s abans d'un esdeveniment) i programar per a un fotograma
/// exacte (<see cref="StartAt"/>), com un TALLA. Treballa al fil de control: <see cref="Tick"/> fa la feina.
/// </summary>
public sealed class AuxPlayers : IDisposable
{
    /// <summary>Nombre de reproductors auxiliars (numerats de l'1 al 3).</summary>
    public const int Count = 3;

    /// <summary>
    /// Baixada de la música, que acaba just quan comença l'auxiliar. Curta (D-064): els senyals horaris tenen el primer
    /// pip als 16 ms del fitxer, i amb 200 ms es notava que la música baixava abans.
    /// </summary>
    public static readonly TimeSpan DuckDown = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan DuckUp = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ReplaceFade = TimeSpan.FromMilliseconds(50);

    /// <summary>Marge perquè el fil d'àudio rebi la programació a temps (com <see cref="QueueSettings.StartMargin"/>).</summary>
    public static readonly TimeSpan StartMargin = TimeSpan.FromMilliseconds(50);

    /// <summary>Quants auxiliars es poden tenir oberts per endavant alhora (les veus són per a tothom).</summary>
    private const int MaxPrepared = 3;

    private readonly Mixer _mixer;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Aux> _prepared = [];
    private readonly Aux?[] _active = new Aux?[Count + 1]; // per reproductor (l'índex 0 no es fa servir)
    private readonly List<Aux> _ending = [];                // aturats o substituïts: s'alliberen quan acaben
    private readonly List<MixerVoice> _toRelease = [];
    private readonly QueueItem?[] _loaded = new QueueItem?[Count + 1]; // carregats a mà (fase 3, pas 4a)

    public AuxPlayers(Mixer mixer)
    {
        _mixer = mixer;
    }

    public event Action<AuxStarted>? Started;
    public event Action<AuxEnded>? Ended;
    public event Action<AuxFailed>? Failed;
    /// <summary>S'ha carregat o tret la pista d'un reproductor (el número del reproductor).</summary>
    public event Action<int>? LoadedChanged;

    /// <summary>
    /// La pista carregada a mà a cada reproductor (fase 3, pas 4a): sona quan l'operador prem ▶ i s'hi queda carregada
    /// després, per tornar-la a fer sonar. No té res a veure amb el que preparen els esdeveniments.
    /// </summary>
    public QueueItem? Loaded(int player)
    {
        if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_sync) return _loaded[player];
    }

    /// <summary>Carrega una pista a un reproductor (o el buida, amb null). No el fa sonar ni atura el que hi soni.</summary>
    public void Load(int player, QueueItem? item)
    {
        if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_sync) _loaded[player] = item;
        LoadedChanged?.Invoke(player);
    }

    public int SampleRate => _mixer.SampleRate;

    /// <summary>Atenuació per defecte dels auxiliars si no se n'hi passa cap (D-078), en dB.</summary>
    public const double StandardDuckingDb = -10;

    /// <summary>
    /// Atenuació de la música quan un auxiliar no en diu cap (D-078): la de la configuració (<c>ducking.set</c>), o
    /// <see cref="StandardDuckingDb"/>. 0 = sense atenuació.
    /// </summary>
    public double DefaultDuckingDb { get; set; } = StandardDuckingDb;

    /// <summary>Si el número de reproductor és vàlid (1 a <see cref="Count"/>).</summary>
    public static bool IsValidPlayer(int player) => player is >= 1 and <= Count;

    /// <summary>Què fa cada reproductor que no està lliure (els que sonen o esperen la seva hora).</summary>
    public IReadOnlyList<AuxStatus> Status
    {
        get
        {
            lock (_sync)
            {
                long now = _mixer.FramesRendered;
                var list = new List<AuxStatus>();
                for (int player = 1; player <= Count; player++)
                {
                    if (_active[player] is not { } a) continue;
                    bool started = a.Voice.ActualStartFrame >= 0;
                    list.Add(new AuxStatus(player, a.Item, started,
                        Elapsed: started ? FramesToTime(Math.Max(0, now - a.StartFrame)) : TimeSpan.Zero,
                        Remaining: FramesToTime(Math.Max(0, a.EndFrame - Math.Max(now, a.StartFrame))),
                        a.DuckingDb));
                }
                return list;
            }
        }
    }

    /// <summary>
    /// Obre per endavant el fitxer de <paramref name="item"/> per al reproductor <paramref name="player"/>. No sona fins
    /// que se li digui amb <see cref="StartAt"/>. Retorna null si no queden veus lliures.
    /// </summary>
    /// <param name="duckingDb">Atenuació de la música mentre sona (negativa, en dB), o null perquè no n'hi hagi.</param>
    public Guid? Prepare(int player, QueueItem item, double? duckingDb)
    {
        if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_sync)
        {
            if (_prepared.Count >= MaxPrepared) return null;
            var voice = _mixer.AcquireVoice();
            if (voice is null) return null;
            voice.Overlay = true;
            // Sencer, des del principi del fitxer (D-064)
            var reader = new VoiceReader(voice, item.Path, _mixer.SampleRate, TimeSpan.Zero, null);
            var id = Guid.NewGuid();
            _prepared[id] = new Aux(player, item, voice, reader, duckingDb is { } db && db < 0 ? db : null);
            return id;
        }
    }

    /// <summary>
    /// Fa sonar un auxiliar preparat al fotograma <paramref name="frame"/> del mesclador (o tan aviat com es pugui, si és
    /// null o ja ha passat). Si el fitxer encara s'està obrint, començarà quan estigui a punt.
    /// </summary>
    public void StartAt(Guid id, long? frame)
    {
        lock (_sync)
        {
            if (_prepared.TryGetValue(id, out var aux)) aux.Target = frame ?? -1;
        }
    }

    /// <summary>Deixa anar un auxiliar preparat que no sonarà (esdeveniment omès o esborrat).</summary>
    public void Release(Guid id)
    {
        lock (_sync)
        {
            if (_prepared.Remove(id, out var aux)) Retire(aux);
        }
    }

    /// <summary>Fa sonar <paramref name="item"/> pel reproductor <paramref name="player"/> tan aviat com es pugui (ordre <c>aux.play</c>).</summary>
    /// <returns>Fals si no queden veus lliures.</returns>
    public bool Play(int player, QueueItem item, double? duckingDb)
    {
        lock (_sync)
        {
            if (Prepare(player, item, duckingDb) is not { } id) return false;
            StartAt(id, null);
            return true;
        }
    }

    /// <summary>Atura el reproductor <paramref name="player"/> (amb un fos). Retorna fals si no sonava res.</summary>
    public bool Stop(int player, TimeSpan fade)
    {
        if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));
        lock (_sync)
        {
            if (_active[player] is not { } a) return false;
            _active[player] = null;
            long at = _mixer.FramesRendered + Frames(StartMargin);
            EndEarly(a, at, Frames(fade));
            return true;
        }
    }

    /// <summary>Fil de control: programa els que ja estan a punt, avisa dels que comencen i allibera els que han acabat.</summary>
    public void Tick()
    {
        var events = new List<Action>();
        lock (_sync)
        {
            long now = _mixer.FramesRendered;

            // 1. Els preparats que ja s'han de programar
            foreach (var (id, aux) in _prepared.ToList())
            {
                if (aux.Target is not { } target) continue;
                switch (aux.Reader.State)
                {
                    case ReaderState.Failed:
                        _prepared.Remove(id);
                        string message = aux.Reader.Error ?? "No s'ha pogut obrir el fitxer.";
                        Retire(aux);
                        events.Add(() => Failed?.Invoke(new AuxFailed(aux.Player, aux.Item, message)));
                        break;
                    case ReaderState.Ready or ReaderState.Finished:
                        _prepared.Remove(id);
                        Schedule(aux, target, now);
                        break;
                }
            }

            // 2. Els que han començat a sonar
            for (int player = 1; player <= Count; player++)
            {
                if (_active[player] is not { Reported: false } a || a.Voice.ActualStartFrame < 0) continue;
                a.Reported = true;
                long frame = a.Voice.ActualStartFrame;
                long? targetFrame = a.Target >= 0 ? a.Target : null;
                events.Add(() => Started?.Invoke(new AuxStarted(a.Player, a.Item, frame, targetFrame)));
            }

            // 3. Els que han acabat
            for (int player = 1; player <= Count; player++)
            {
                if (_active[player] is not { } a || a.Voice.State != VoiceState.Finished) continue;
                _active[player] = null;
                // Si ha acabat abans del previst, la música torna a pujar ara
                long end = a.Voice.ActualStartFrame + a.Voice.FramesPlayed;
                if (a.Duck is not null && end < a.EndFrame) SetDuck(a, a.Duck.WithEnd(end));
                Finish(a, complete: a.Voice.FramesPlayed + Frames(TimeSpan.FromMilliseconds(50)) >= a.EndFrame - a.StartFrame, events);
            }
            for (int i = _ending.Count - 1; i >= 0; i--)
            {
                var a = _ending[i];
                if (a.Voice.State is VoiceState.Finished or VoiceState.Reserved)
                {
                    _ending.RemoveAt(i);
                    Finish(a, complete: false, events);
                }
            }
            _toRelease.RemoveAll(v => _mixer.ReleaseVoice(v));
        }
        foreach (var e in events) e();
    }

    /// <summary>Programa un auxiliar a punt: si el reproductor sonava, el substitueix.</summary>
    private void Schedule(Aux aux, long target, long now)
    {
        long earliest = now + Frames(StartMargin);
        int down = aux.DuckingDb is null ? 0 : Frames(DuckDown);
        // Sense hora fixa: es deixa temps perquè la música baixi abans de començar
        long start = target >= 0 ? Math.Max(target, earliest) : earliest + down;
        // Si s'arriba just, la baixada s'escurça (mai un salt de volum a mitja baixada)
        down = (int)Math.Min(down, Math.Max(0, start - earliest));

        long length = (long)Math.Round((aux.Reader.FileDuration ?? TimeSpan.Zero).TotalSeconds * _mixer.SampleRate);
        aux.StartFrame = start;
        aux.EndFrame = start + length;

        if (_active[aux.Player] is { } previous)
        {
            // El nou substitueix l'anterior: fos curt just quan comença el nou
            _active[aux.Player] = null;
            EndEarly(previous, start, Frames(ReplaceFade));
        }

        aux.Voice.Start(start);
        _active[aux.Player] = aux;
        if (aux.DuckingDb is { } db)
            SetDuck(aux, new DuckEnvelope(start, aux.EndFrame, db, down, Frames(DuckUp)));
        else if (_mixer.GetDuck(aux.Player - 1) is { } old && old.End > start)
            _mixer.SetDuck(aux.Player - 1, old.WithEnd(start)); // l'anterior atenuava: la música torna a pujar
    }

    /// <summary>Atura un auxiliar abans d'hora (o el desprograma, si encara no sonava) i el passa a la llista dels que acaben.</summary>
    private void EndEarly(Aux a, long at, int fadeFrames)
    {
        if (a.Voice.ActualStartFrame < 0 && a.Voice.Cancel())
        {
            // Encara no sonava: no cal cap fos
            if (a.Duck is not null) _mixer.SetDuck(a.Player - 1, null);
        }
        else
        {
            a.Voice.Stop(at, fadeFrames);
            if (a.Duck is not null) SetDuck(a, a.Duck.WithEnd(at + fadeFrames));
        }
        _ending.Add(a); // el lector s'atura quan s'alliberi (Retire)
    }

    private void Finish(Aux a, bool complete, List<Action> events)
    {
        var played = FramesToTime(a.Voice.FramesPlayed);
        bool started = a.Voice.ActualStartFrame >= 0;
        Retire(a);
        if (started) events.Add(() => Ended?.Invoke(new AuxEnded(a.Player, a.Item, played, complete)));
    }

    private void SetDuck(Aux a, DuckEnvelope duck)
    {
        a.Duck = duck;
        _mixer.SetDuck(a.Player - 1, duck);
    }

    /// <summary>Atura el lector i allibera la veu (si encara sona, quan el fil d'àudio la doni per acabada).</summary>
    private void Retire(Aux a)
    {
        a.Reader.Dispose();
        a.Voice.Cancel();
        if (_mixer.ReleaseVoice(a.Voice)) return;
        a.Voice.Stop(_mixer.FramesRendered);
        _toRelease.Add(a.Voice);
    }

    private int Frames(TimeSpan time) => (int)Math.Round(time.TotalSeconds * _mixer.SampleRate);

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / _mixer.SampleRate);

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var a in _prepared.Values) Retire(a);
            _prepared.Clear();
            for (int player = 1; player <= Count; player++)
            {
                if (_active[player] is { } a) Retire(a);
                _active[player] = null;
            }
            foreach (var a in _ending) Retire(a);
            _ending.Clear();
            for (int slot = 0; slot < Mixer.DuckSlots; slot++) _mixer.SetDuck(slot, null);
        }
    }

    /// <summary>Un fitxer d'un reproductor auxiliar, amb la seva veu i el seu lector.</summary>
    private sealed class Aux(int player, QueueItem item, MixerVoice voice, VoiceReader reader, double? duckingDb)
    {
        public int Player { get; } = player;
        public QueueItem Item { get; } = item;
        public MixerVoice Voice { get; } = voice;
        public VoiceReader Reader { get; } = reader;
        public double? DuckingDb { get; } = duckingDb;
        /// <summary>null = encara no se li ha dit que soni; −1 = tan aviat com es pugui; si no, el fotograma.</summary>
        public long? Target { get; set; }
        public long StartFrame { get; set; }
        public long EndFrame { get; set; } = long.MaxValue;
        public DuckEnvelope? Duck { get; set; }
        /// <summary>Ja s'ha avisat que ha començat.</summary>
        public bool Reported { get; set; }
    }
}
