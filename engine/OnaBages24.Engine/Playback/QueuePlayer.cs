using OnaBages.Audio;

namespace OnaBages24.Engine.Playback;

/// <summary>Ajustos del reproductor de la cua.</summary>
public sealed record QueueSettings
{
    /// <summary>Solapament màxim entre dues pistes (D-024). 10 s per defecte, confirmat escoltant-ho (D-050).</summary>
    public TimeSpan MaxOverlap { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Quant abans del punt de mescla es prepara la pista següent (com RadioDJ: 20 s).</summary>
    public TimeSpan PrepareAhead { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Marge per començar quan no sonava res (perquè la veu ja tingui dades quan la sortida les demani).</summary>
    public TimeSpan StartMargin { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>Una pista que ha començat a sonar.</summary>
public sealed record QueueStarted(QueueItem Item, long StartFrame);

/// <summary>Una pista que s'ha acabat: quant ha sonat i si ha arribat al final previst.</summary>
public sealed record QueueEnded(QueueItem Item, TimeSpan Played, bool Complete);

/// <summary>Una pista que no s'ha pogut fer sonar (fitxer que falta o il·legible): se salta.</summary>
public sealed record QueueFailed(QueueItem Item, string Message);

/// <summary>Posició de la pista que sona: quant ha sonat, quant falta per al final i per al punt de mescla, i quant dura en total.</summary>
public sealed record QueuePosition(Guid ItemId, TimeSpan Elapsed, TimeSpan Remaining, TimeSpan UntilMix, TimeSpan Duration);

/// <summary>
/// Reproductor de la cua d'emissió (docs/disseny-motor.md, secció 4). Treballa al fil de control: el motor el crida
/// sovint (<see cref="Tick"/>) i ell decideix què ha de sonar i quan, programant les veus del <see cref="Mixer"/> en fotogrames:
/// - la pista que sona (A) acaba amb un fos entre el punt de mescla i el final;
/// - la següent (B) es prepara uns 20 s abans i es programa per començar exactament al punt de mescla d'A;
/// - si el solapament (final − mescla) passa del màxim, B entra a «final − màxim» (D-024);
/// - si B no es pot obrir, se salta i es prepara la següent, amb un avís.
///
/// Totes les operacions sobre la cua són atòmiques (un sol bloqueig): mentre es canvia, no es prepara res a mitges.
/// </summary>
public sealed class QueuePlayer : IDisposable
{
    private readonly Mixer _mixer;
    private readonly QueueSettings _settings;
    private readonly object _sync = new();
    private readonly List<QueueItem> _upcoming = [];
    private readonly List<Playing> _ending = [];
    private readonly List<MixerVoice> _toRelease = []; // veus aturades que s'alliberaran quan el fil d'àudio les doni per acabades
    private Playing? _current;
    private Playing? _next;

    public QueuePlayer(Mixer mixer, QueueSettings? settings = null)
    {
        _mixer = mixer;
        _settings = settings ?? new QueueSettings();
    }

    public event Action<QueueStarted>? Started;
    public event Action<QueueEnded>? Ended;
    public event Action<QueueFailed>? Failed;
    /// <summary>La cua ha canviat (pistes afegides, tretes, començades o acabades).</summary>
    public event Action? Changed;

    public QueueSettings Settings => _settings;

    /// <summary>La pista que sona ara (la que ha començat més tard), o null.</summary>
    public QueueItem? Current
    {
        get { lock (_sync) return _current?.Item; }
    }

    /// <summary>Les pistes que esperen per sonar, en ordre (inclosa la que ja s'està preparant).</summary>
    public IReadOnlyList<QueueItem> Upcoming
    {
        get
        {
            lock (_sync)
            {
                var list = new List<QueueItem>(_upcoming.Count + 1);
                if (_next is not null) list.Add(_next.Item);
                list.AddRange(_upcoming);
                return list;
            }
        }
    }

    /// <summary>
    /// On és la pista que sona (per a la interfície, fase 1, pas 10a), o null si no sona res. Es calcula amb el comptador
    /// de fotogrames del mesclador, el mateix rellotge que fa servir l'encadenament.
    /// </summary>
    public QueuePosition? Position
    {
        get
        {
            lock (_sync)
            {
                if (_current is not { Scheduled: true } c) return null;
                long now = _mixer.FramesRendered;
                return new QueuePosition(
                    c.Item.Id,
                    Elapsed: FramesToTime(Math.Max(0, now - c.StartFrame)),
                    Remaining: FramesToTime(Math.Max(0, c.EndFrame - now)),
                    UntilMix: FramesToTime(Math.Max(0, c.MixFrame - now)),
                    Duration: FramesToTime(Math.Max(0, c.EndFrame - c.StartFrame)));
            }
        }
    }

    /// <summary>Hi ha una pista preparant-se que encara no s'ha pogut programar (el fitxer s'està obrint). Per a les proves.</summary>
    public bool IsPreparing
    {
        get { lock (_sync) return _next is { Scheduled: false, Reader.State: ReaderState.Opening }; }
    }

    // --- Canvis a la cua ---

    /// <summary>Afegeix pistes al final de la cua.</summary>
    public void Enqueue(params QueueItem[] items)
    {
        lock (_sync) _upcoming.AddRange(items);
        Changed?.Invoke();
    }

    /// <summary>
    /// Insereix pistes a una posició de la cua (0 = la primera que sonarà). Si la que ara és la primera ja s'estava
    /// preparant, es deixa de preparar i torna a la cua darrere de les noves.
    /// </summary>
    public void Insert(int index, params QueueItem[] items)
    {
        var events = new List<Action>();
        lock (_sync)
        {
            if (index == 0) UnprepareNext(events);
            index = Math.Clamp(index - (_next is null ? 0 : 1), 0, _upcoming.Count);
            _upcoming.InsertRange(index, items);
        }
        Raise(events);
        Changed?.Invoke();
    }

    /// <summary>Treu de la cua una pista que encara no ha començat. Retorna false si no hi és (o ja sona).</summary>
    public bool Remove(Guid itemId)
    {
        var events = new List<Action>();
        bool removed;
        lock (_sync)
        {
            if (_next?.Item.Id == itemId && !_next.HasStarted)
            {
                UnprepareNext(events);
                _upcoming.RemoveAt(0); // UnprepareNext l'ha tornada al principi de la cua
                removed = true;
            }
            else
            {
                removed = _upcoming.RemoveAll(i => i.Id == itemId) > 0;
            }
        }
        Raise(events);
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Treu de la cua les pistes que encara no han començat i compleixen la condició (p. ex. totes les de l'AutoDJ, D-034).</summary>
    public int RemoveWhere(Func<QueueItem, bool> condition)
    {
        var events = new List<Action>();
        int count;
        lock (_sync)
        {
            if (_next is not null && !_next.HasStarted && condition(_next.Item)) UnprepareNext(events);
            count = _upcoming.RemoveAll(i => condition(i));
        }
        Raise(events);
        if (count > 0) Changed?.Invoke();
        return count;
    }

    /// <summary>
    /// Passa a la pista següent ara: la que sona baixa amb un fos de <paramref name="fade"/> i la següent comença de seguida
    /// (en quant estigui a punt). Si no n'hi ha cap, la que sona simplement s'acaba amb el fos.
    /// </summary>
    public void Skip(TimeSpan fade)
    {
        lock (_sync)
        {
            if (_current is null) return;
            long now = _mixer.FramesRendered;
            int fadeFrames = Frames(fade);
            _current.Voice.Stop(now, fadeFrames);
            _current.MixFrame = now;
            _current.EndFrame = now + fadeFrames;
            if (_next is { Scheduled: true, HasStarted: false })
            {
                _next.StartFrame = now + Frames(_settings.StartMargin);
                _next.Voice.Start(_next.StartFrame);
            }
        }
    }

    // --- Fil de control ---

    /// <summary>
    /// Fa la feina del reproductor: prepara, programa, passa a la pista següent i allibera les veus acabades.
    /// El motor la crida sovint (p. ex. cada 20 ms); és ràpida i no espera mai.
    /// </summary>
    public void Tick()
    {
        var events = new List<Action>();
        lock (_sync)
        {
            long now = _mixer.FramesRendered;

            // 1. La pista que s'estava preparant no es pot obrir: se salta (i es prova la següent més avall)
            if (_next is { HasStarted: false, Reader.State: ReaderState.Failed } failed)
            {
                var item = failed.Item;
                string message = failed.Reader.Error ?? "No s'ha pogut obrir el fitxer.";
                Retire(failed);
                _next = null;
                events.Add(() => Failed?.Invoke(new QueueFailed(item, message)));
                events.Add(() => Changed?.Invoke());
            }

            // 2. La següent ja ha començat a sonar: passa a ser l'actual
            if (_next is { HasStarted: true } started)
            {
                if (_current is not null) _ending.Add(_current);
                _current = started;
                _next = null;
                var item = started.Item;
                long frame = started.Voice.ActualStartFrame;
                events.Add(() => Started?.Invoke(new QueueStarted(item, frame)));
                events.Add(() => Changed?.Invoke());
            }

            // 3. Les veus que han acabat: s'alliberen
            if (_current is not null && _current.Voice.State == VoiceState.Finished)
            {
                _ending.Add(_current);
                _current = null;
            }
            for (int i = _ending.Count - 1; i >= 0; i--)
            {
                var ending = _ending[i];
                if (ending.Voice.State != VoiceState.Finished) continue;
                var item = ending.Item;
                var played = TimeSpan.FromSeconds((double)ending.Voice.FramesPlayed / _mixer.SampleRate);
                bool complete = ending.PlannedEndFrame - ending.StartFrame <= ending.Voice.FramesPlayed + Frames(TimeSpan.FromMilliseconds(50));
                Retire(ending);
                _ending.RemoveAt(i);
                events.Add(() => Ended?.Invoke(new QueueEnded(item, played, complete)));
            }

            _toRelease.RemoveAll(v => _mixer.ReleaseVoice(v));

            // 4. Preparar la següent: si no sona res, de seguida; si no, uns segons abans del punt de mescla
            if (_next is null && _upcoming.Count > 0
                && (_current is null || now >= _current.MixFrame - Frames(_settings.PrepareAhead)))
            {
                var item = _upcoming[0];
                _upcoming.RemoveAt(0);
                _next = Prepare(item, events);
            }

            // 5. Programar-la quan el fitxer ja és obert
            if (_next is { Scheduled: false } ready && ready.Reader.State is ReaderState.Ready or ReaderState.Finished)
            {
                long margin = now + Frames(_settings.StartMargin);
                long start = _current is null ? margin : Math.Max(_current.MixFrame, margin);
                Schedule(ready, start);
            }
        }
        Raise(events);
    }

    /// <summary>Reserva una veu i comença a llegir el fitxer. Si no hi ha cap veu lliure (no hauria de passar), es torna a provar al següent Tick.</summary>
    private Playing? Prepare(QueueItem item, List<Action> events)
    {
        var voice = _mixer.AcquireVoice();
        if (voice is null)
        {
            _upcoming.Insert(0, item);
            return null;
        }
        var reader = new VoiceReader(voice, item.Path, _mixer.SampleRate, Ms(item.StartMs ?? 0), item.EndMs is { } end ? Ms(end) : null);
        return new Playing(item, voice, reader);
    }

    /// <summary>
    /// Programa una pista preparada per començar al fotograma <paramref name="start"/>, i també el seu final:
    /// fos de sortida entre el punt de mescla i el final, amb el solapament màxim (D-024).
    /// </summary>
    private void Schedule(Playing p, long start)
    {
        long fileMs = (long)(p.Reader.FileDuration ?? TimeSpan.Zero).TotalMilliseconds;
        long startMs = Math.Clamp(p.Item.StartMs ?? 0, 0, fileMs);
        long endMs = Math.Clamp(p.Item.EndMs ?? fileMs, startMs, fileMs);
        long mixMs = Math.Clamp(p.Item.MixMs ?? endMs, startMs, endMs);
        long maxOverlapMs = (long)_settings.MaxOverlap.TotalMilliseconds;
        if (endMs - mixMs > maxOverlapMs) mixMs = endMs - maxOverlapMs;

        p.StartFrame = start;
        p.MixFrame = start + FramesFromMs(mixMs - startMs);
        p.EndFrame = start + FramesFromMs(endMs - startMs);
        p.PlannedEndFrame = p.EndFrame;
        p.Voice.Start(start);
        p.Voice.Stop(p.MixFrame, (int)(p.EndFrame - p.MixFrame));
        p.Scheduled = true;
    }

    /// <summary>Deixa de preparar la següent (si encara no ha començat) i la torna al principi de la cua.</summary>
    private void UnprepareNext(List<Action> events)
    {
        if (_next is null || _next.HasStarted) return;
        _upcoming.Insert(0, _next.Item);
        Retire(_next);
        _next = null;
    }

    /// <summary>
    /// Atura el lector d'una pista i n'allibera la veu. Si la veu ja sonava (casos rars, p. ex. treure una pista just
    /// quan començava), s'atura de cop i s'allibera quan el fil d'àudio la doni per acabada.
    /// </summary>
    private void Retire(Playing p)
    {
        p.Reader.Dispose();
        p.Voice.Cancel();
        if (_mixer.ReleaseVoice(p.Voice)) return;
        p.Voice.Stop(_mixer.FramesRendered);
        _toRelease.Add(p.Voice);
    }

    private void Raise(List<Action> events)
    {
        foreach (var e in events) e();
    }

    private int Frames(TimeSpan time) => (int)Math.Round(time.TotalSeconds * _mixer.SampleRate);

    private long FramesFromMs(long ms) => (long)Math.Round(ms / 1000.0 * _mixer.SampleRate);

    private TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / _mixer.SampleRate);

    private static TimeSpan Ms(long ms) => TimeSpan.FromMilliseconds(ms);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_next is not null) Retire(_next);
            if (_current is not null) Retire(_current);
            foreach (var e in _ending) Retire(e);
            _next = _current = null;
            _ending.Clear();
        }
    }

    /// <summary>Una pista amb la seva veu i el seu lector.</summary>
    private sealed class Playing(QueueItem item, MixerVoice voice, VoiceReader reader)
    {
        public QueueItem Item { get; } = item;
        public MixerVoice Voice { get; } = voice;
        public VoiceReader Reader { get; } = reader;
        public bool Scheduled { get; set; }
        public long StartFrame { get; set; }
        public long MixFrame { get; set; } = long.MaxValue;
        public long EndFrame { get; set; } = long.MaxValue;
        /// <summary>El final previst en programar-la (no canvia si se salta): per saber si ha sonat sencera.</summary>
        public long PlannedEndFrame { get; set; } = long.MaxValue;
        public bool HasStarted => Voice.ActualStartFrame >= 0;
    }
}
