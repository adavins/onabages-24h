using OnaBages.Audio;
using OnaBages24.Data;

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

    /// <summary>Pausa (D-103): com a molt, quant pot durar; després la pista continua sola (mai un silenci llarg per oblit).</summary>
    public TimeSpan MaxPause { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Fos de la pausa: en fer pausa la pista baixa en aquest temps, i en continuar puja en el mateix temps.</summary>
    public TimeSpan PauseFade { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Encadenament en anar a un altre punt de la pista (D-103): la veu vella baixa i la nova puja en aquest temps, sense clic.</summary>
    public TimeSpan SeekCrossfade { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// La pista en pausa (D-103): quina és, on s'ha aturat (des del punt d'inici de la pista), quant hi ha de l'inici fins a
/// la mescla i fins al final, i d'aquí a quant continuarà sola.
/// </summary>
public sealed record QueuePause(QueueItem Item, TimeSpan Position, TimeSpan UntilMix, TimeSpan Duration, TimeSpan AutoResumeIn);

/// <summary>
/// Una pista que ha començat a sonar. Si és la primera d'un tall (TALLA), <paramref name="CutFrame"/> és el fotograma
/// on havia de començar: la diferència amb <paramref name="StartFrame"/> és el retard del tall.
/// </summary>
public sealed record QueueStarted(QueueItem Item, long StartFrame, long? CutFrame = null);

/// <summary>Una pista que s'ha acabat: quant ha sonat i si ha arribat al final previst.</summary>
public sealed record QueueEnded(QueueItem Item, TimeSpan Played, bool Complete);

/// <summary>Una pista que no s'ha pogut fer sonar (fitxer que falta o il·legible): se salta.</summary>
public sealed record QueueFailed(QueueItem Item, string Message);

/// <summary>Posició de la pista que sona: quant ha sonat, quant falta per al final i per al punt de mescla, i quant dura en total.</summary>
/// <summary>Previsió de la cua: d'aquí a quant s'acaba la que sona i d'aquí a quant comença cada pista que espera (null = no se sap).</summary>
public sealed record QueueForecast(TimeSpan? CurrentEndsIn, IReadOnlyList<(Guid Id, TimeSpan? StartsIn)> Starts);

public sealed record QueuePosition(Guid ItemId, TimeSpan Elapsed, TimeSpan Remaining, TimeSpan UntilMix, TimeSpan Duration)
{
    /// <summary>La pista està en pausa (D-103): la posició no avança.</summary>
    public bool Paused { get; init; }
}

/// <summary>
/// Reproductor de la cua d'emissió (docs/disseny-motor.md, secció 4). Treballa al fil de control: el motor el crida
/// sovint (<see cref="Tick"/>) i ell decideix què ha de sonar i quan, programant les veus del <see cref="Mixer"/> en fotogrames:
/// - la pista que sona (A) acaba amb un fos entre el punt de mescla i el final;
/// - la següent (B) es prepara uns 20 s abans i es programa per començar exactament al punt de mescla d'A;
/// - si el solapament (final − mescla) passa del màxim, B entra a «final − màxim» (D-024);
/// - si B no es pot obrir, se salta i es prepara la següent, amb un avís.
///
/// Totes les operacions sobre la cua són atòmiques (un sol bloqueig): mentre es canvia, no es prepara res a mitges.
///
/// Talls (TALLA, fase 2, pas 4): <see cref="Reserve"/> obre la primera pista uns 30 s abans en una veu de reserva, i
/// <see cref="CutAt"/>, uns segons abans de l'hora, ho programa tot alhora: la que sona comença el fos al fotograma de
/// l'hora i la nova comença en aquell mateix fotograma (D-021).
/// </summary>
public sealed class QueuePlayer : IDisposable
{
    private readonly Mixer _mixer;
    private readonly QueueSettings _settings;
    private readonly object _sync = new();
    private readonly List<QueueItem> _upcoming = [];
    private readonly List<Playing> _ending = [];
    private readonly List<MixerVoice> _toRelease = []; // veus aturades que s'alliberaran quan el fil d'àudio les doni per acabades
    private readonly Dictionary<Guid, Playing> _reserved = []; // primeres pistes de talls, obertes per endavant
    private Playing? _current;
    private Playing? _next;
    private PendingCut? _cut;  // tall programat que encara no s'ha pogut aplicar (la següent ja començava abans)
    private long _cutFrame;    // si no és 0: la pròxima pista no pot començar abans d'aquest fotograma (és la del tall)

    // Pausa i salt dins de la pista (D-103)
    private Paused? _paused;   // la pista en pausa (llavors _current és null)
    private Playing? _resume;  // la mateixa pista oberta en un altre punt: substitueix _current (o la pausa) quan és a punt
    private long _pauseChangedAt; // última vegada que s'ha avisat del canvi de la previsió durant la pausa (fotograma)

    /// <summary>
    /// Avís de l'última pausa que ha continuat sola (o que no s'ha pogut continuar), en català; null si no n'hi ha. Es
    /// manté fins a la pausa següent.
    /// </summary>
    public string? PauseNotice { get; private set; }

    /// <summary>Un problema en fer pausa o anar a un altre punt (el fitxer no s'ha pogut tornar a obrir…), per al registre.</summary>
    public event Action<string>? Problem;

    /// <summary>Quantes veus es poden reservar per a talls alhora (n'hi ha 8: cal deixar lloc per a la cua).</summary>
    private const int MaxReserved = 2;

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

    /// <summary>Fotogrames per segon del mesclador.</summary>
    public int SampleRate => _mixer.SampleRate;

    /// <summary>El fotograma que el mesclador mescla ara (per tallar «de seguida» quan no se sap l'hora de la sortida).</summary>
    public long CurrentFrame => _mixer.FramesRendered;

    /// <summary>La pista que sona ara (la que ha començat més tard), o null.</summary>
    public QueueItem? Current
    {
        get { lock (_sync) return _current?.Item ?? _paused?.Item; } // la que està en pausa també és «la que sona» (D-103)
    }

    /// <summary>Les pistes que esperen per sonar, en ordre (inclosa la que ja s'està preparant).</summary>
    public IReadOnlyList<QueueItem> Upcoming
    {
        get
        {
            lock (_sync)
            {
                var list = new List<QueueItem>(_upcoming.Count + 2);
                if (_next is not null) list.Add(_next.Item);
                if (_cut?.First is { } first) list.Add(first.Item);
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
                if (_paused is { } p) // en pausa (D-103): la posició no avança
                    return new QueuePosition(p.Item.Id, FramesToTime(p.OffsetFrames), FramesToTime(Math.Max(0, p.EndOffsetFrames - p.OffsetFrames)),
                        FramesToTime(Math.Max(0, p.MixOffsetFrames - p.OffsetFrames)), FramesToTime(p.EndOffsetFrames)) { Paused = true };
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

    /// <summary>
    /// Previsió de la cua (fase 3, pas 1): d'aquí a quant començarà cada pista que espera, en l'ordre de <see cref="Upcoming"/>,
    /// i d'aquí a quant s'acaba la que sona. Amb els mateixos punts de mescla i el mateix solapament màxim que
    /// l'encadenament; la que ja està programada, amb els seus fotogrames exactes. Si d'una pista no se sap quant dura, les
    /// de després ja no tenen hora (null).
    /// </summary>
    public QueueForecast Forecast()
    {
        lock (_sync)
        {
            long now = _mixer.FramesRendered;
            var starts = new List<(Guid Id, TimeSpan? StartsIn)>();

            // On comença la següent: al punt de mescla de la que sona, o de seguida si no sona res
            long? nextStart = _current is { Scheduled: true } c ? Math.Max(now, c.MixFrame) : now;
            TimeSpan? currentEndsIn = _current is { Scheduled: true } cur ? FramesToTime(Math.Max(0, cur.EndFrame - now)) : null;
            if (_paused is { } p)
            {
                // En pausa (D-103): com si continués ara mateix (la previsió es mou mentre dura la pausa)
                nextStart = now + Math.Max(0, p.MixOffsetFrames - p.OffsetFrames);
                currentEndsIn = FramesToTime(Math.Max(0, p.EndOffsetFrames - p.OffsetFrames));
            }

            // Les pistes en l'ordre de Upcoming, amb el fotograma on han de començar si ja se sap (la preparada o la d'un tall)
            var entries = new List<(QueueItem Item, Playing? Prepared, long? FixedStart)>();
            if (_next is not null) entries.Add((_next.Item, _next, _cutFrame > 0 ? _cutFrame : null));
            long? cutStart = _next is null && _cutFrame > 0 ? _cutFrame : null; // tall aplicat: la pròxima que es prepari hi comença
            if (_cut is { } pending)
            {
                if (pending.First is { } first) entries.Add((first.Item, first, pending.Frame));
                else cutStart = pending.Frame; // tall sense pista oberta: la primera de la cua comença al fotograma del tall
            }
            foreach (var item in _upcoming)
            {
                entries.Add((item, null, cutStart));
                cutStart = null;
            }

            foreach (var (item, prepared, fixedStart) in entries)
            {
                long? start;
                long? mix;
                if (prepared is { Scheduled: true })
                {
                    start = prepared.StartFrame;
                    mix = prepared.MixFrame;
                }
                else
                {
                    start = fixedStart is { } f ? Math.Max(f, now) : nextStart;
                    mix = start is { } s && MixDurationFrames(item) is { } m ? s + m : null;
                }
                starts.Add((item.Id, start is { } st ? FramesToTime(Math.Max(0, st - now)) : null));
                nextStart = mix;
            }
            return new QueueForecast(currentEndsIn, starts);
        }
    }

    /// <summary>De l'inici al punt de mescla d'una pista (on entra la següent), en fotogrames, o null si no se sap quant dura.</summary>
    private long? MixDurationFrames(QueueItem item)
    {
        long startMs = item.StartMs ?? 0;
        if ((item.EndMs ?? item.FileDurationMs) is not { } endMs) return null;
        endMs = Math.Max(endMs, startMs);
        long mixMs = Math.Clamp(item.MixMs ?? endMs, startMs, endMs);
        long maxOverlapMs = (long)_settings.MaxOverlap.TotalMilliseconds;
        if (endMs - mixMs > maxOverlapMs) mixMs = endMs - maxOverlapMs;
        return FramesFromMs(mixMs - startMs);
    }

    /// <summary>
    /// La pista següent, si ja està programada i encara no sona: quina és i a quin fotograma començarà (per al
    /// trepitjador, fase 3, pas 5). El fotograma pot canviar (p. ex. si l'operador salta a la següent).
    /// </summary>
    public (QueueItem Item, long StartFrame)? ScheduledNext
    {
        get { lock (_sync) return _next is { Scheduled: true, HasStarted: false } n ? (n.Item, n.StartFrame) : null; }
    }

    /// <summary>La pista que sona i el fotograma on ha començat de debò, o null si no sona res.</summary>
    public (QueueItem Item, long StartFrame)? CurrentStart
    {
        get { lock (_sync) return _current is { HasStarted: true } c ? (c.Item, c.Voice.ActualStartFrame) : null; }
    }

    /// <summary>
    /// La pista següent si ja s'ha començat a preparar per sonar (s'obre el fitxer uns 20 s abans de la mescla) i encara no
    /// sona; null si encara no. L'AutoDJ no la canvia quan torna a triar la cua (D-097): podria no ser a temps.
    /// </summary>
    public Guid? NextInPreparation
    {
        get { lock (_sync) return _next is { HasStarted: false } n ? n.Item.Id : null; }
    }

    /// <summary>Hi ha una pista preparant-se que encara no s'ha pogut programar (el fitxer s'està obrint). Per a les proves.</summary>
    public bool IsPreparing
    {
        get
        {
            lock (_sync)
                return _next is { Scheduled: false, Reader.State: ReaderState.Opening }
                    || _resume is { Scheduled: false, Reader.State: ReaderState.Opening }; // pausa o salt (D-103)
        }
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
            // La primera pista d'un tall programat no es treu: el que s'insereix al davant va just darrere seu
            bool cutPending = _cutFrame > 0 || _next?.CutFrame is not null;
            if (index == 0 && !cutPending) UnprepareNext(events);
            index = Math.Clamp(index - (_next is null ? 0 : 1), 0, _upcoming.Count);
            _upcoming.InsertRange(index, items);
        }
        Raise(events);
        Changed?.Invoke();
    }

    /// <summary>Treu de la cua una pista que encara no ha començat. Retorna false si no hi és (o ja sona).</summary>
    public bool Remove(Guid itemId) => Take(itemId) is not null;

    /// <summary>Com <see cref="Remove"/>, però retorna la pista treta (per anotar-la com a no emesa si venia d'un esdeveniment).</summary>
    public QueueItem? Take(Guid itemId)
    {
        QueueItem? taken = null;
        var events = new List<Action>();
        lock (_sync)
        {
            if (_next?.Item.Id == itemId && !_next.HasStarted)
            {
                taken = _next.Item;
                UnprepareNext(events);
                _upcoming.RemoveAt(0); // UnprepareNext l'ha tornada al principi de la cua
            }
            else if (_upcoming.FindIndex(i => i.Id == itemId) is var index and >= 0)
            {
                taken = _upcoming[index];
                _upcoming.RemoveAt(index);
            }
        }
        Raise(events);
        if (taken is not null) Changed?.Invoke();
        return taken;
    }

    /// <summary>Quant abans de començar una pista ja preparada no es pot moure (tornar-la a obrir podria fer un buit).</summary>
    private static readonly TimeSpan LockedBeforeStart = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Mou una pista de la cua una posició amunt (−1) o avall (+1) (fase 3, pas 2a). Si cal, la que s'estava preparant torna
    /// a la cua i es prepara la que toqui. Retorna el motiu si no es pot (en català), o null si s'ha mogut.
    /// </summary>
    public string? Move(Guid itemId, int delta)
    {
        var events = new List<Action>();
        string? problem = null;
        lock (_sync)
        {
            if (_cut is not null) problem = "Hi ha un tall programat d'aquí a uns segons: espera que comenci.";
            else if (_next is { HasStarted: true }) problem = "La pista següent ja ha començat a sonar.";
            else if (_next is { Scheduled: true } n && FramesToTime(n.StartFrame - _mixer.FramesRendered) < LockedBeforeStart
                     && (n.Item.Id == itemId || delta < 0))
                problem = "La pista següent està a punt de començar: ja no es pot canviar l'ordre del principi de la cua.";
            else
            {
                if (_next is not null) UnprepareNext(events); // torna al principi de la cua: tot es mou en una sola llista
                int index = _upcoming.FindIndex(i => i.Id == itemId);
                int target = index + Math.Sign(delta);
                if (index < 0) problem = "Aquesta pista ja no és a la cua.";
                else if (target < 0 || target >= _upcoming.Count) problem = null; // ja és a la punta: no cal fer res
                else (_upcoming[index], _upcoming[target]) = (_upcoming[target], _upcoming[index]);
            }
        }
        Raise(events);
        if (problem is null) Changed?.Invoke();
        return problem;
    }

    /// <summary>
    /// «Reprodueix ara» (fase 3, pas 2a; D-073): la que sona baixa amb un fos i la pista triada comença de seguida. Les
    /// que hi havia entremig es treuen de la cua i es retornen (per anotar com a no emeses les dels esdeveniments).
    /// Retorna null si la pista no és a la cua, o si hi ha un tall programat d'aquí a uns segons.
    /// </summary>
    public IReadOnlyList<QueueItem>? PlayNow(Guid itemId, TimeSpan fade)
    {
        var events = new List<Action>();
        var skipped = new List<QueueItem>();
        lock (_sync)
        {
            if (_cut is not null || _next is { HasStarted: true }) return null;
            if (_next?.Item.Id != itemId)
            {
                if (_next is not null) UnprepareNext(events);
                int index = _upcoming.FindIndex(i => i.Id == itemId);
                if (index < 0) return null;
                skipped.AddRange(_upcoming.Take(index));
                _upcoming.RemoveRange(0, index);
            }
            SkipLocked(fade, events); // la que sona fa el fos; la triada és ara la primera de la cua i es prepara de seguida
        }
        Raise(events);
        Changed?.Invoke();
        return skipped;
    }

    /// <summary>Treu de la cua les pistes que encara no han començat i compleixen la condició (p. ex. totes les de l'AutoDJ, D-034).</summary>
    /// <summary>
    /// La pista <paramref name="track"/> ha canviat (fitxer substituït o punts nous, fase 4, pas 1; D-012): les pistes de la
    /// cua que en són prenen la durada i els punts nous. Si la següent ja s'estava preparant amb els d'abans, es torna a
    /// preparar, però només si falten més de 5 s perquè comenci (si no, sona com estava: l'última versió bona).
    /// Retorna quantes s'han actualitzat.
    /// </summary>
    public int RefreshTrack(Track track)
    {
        QueueItem Updated(QueueItem i) => i with { StartMs = track.StartMs, MixMs = track.MixMs, EndMs = track.EndMs, FileDurationMs = track.DurationMs };
        bool Differs(QueueItem i) => i.TrackId == track.Id &&
            (i.StartMs != track.StartMs || i.MixMs != track.MixMs || i.EndMs != track.EndMs || i.FileDurationMs != track.DurationMs);
        var events = new List<Action>();
        int count = 0;
        lock (_sync)
        {
            if (_next is { HasStarted: false } next && Differs(next.Item) && _cut?.First != next
                && (!next.Scheduled || next.StartFrame - _mixer.FramesRendered > Frames(TimeSpan.FromSeconds(5))))
                UnprepareNext(events); // torna al principi de la cua i es prepara de nou al pròxim Tick
            for (int i = 0; i < _upcoming.Count; i++)
            {
                if (!Differs(_upcoming[i])) continue;
                _upcoming[i] = Updated(_upcoming[i]);
                count++;
            }
        }
        Raise(events);
        if (count > 0) Changed?.Invoke();
        return count;
    }

    public int RemoveWhere(Func<QueueItem, bool> condition) => TakeWhere(condition).Count;

    /// <summary>Com <see cref="RemoveWhere"/>, però retorna les pistes que ha tret, en l'ordre que tenien a la cua.</summary>
    public IReadOnlyList<QueueItem> TakeWhere(Func<QueueItem, bool> condition)
    {
        var events = new List<Action>();
        List<QueueItem> removed;
        lock (_sync)
        {
            if (_next is not null && !_next.HasStarted && condition(_next.Item)) UnprepareNext(events);
            removed = _upcoming.Where(condition).ToList();
            _upcoming.RemoveAll(i => condition(i));
        }
        Raise(events);
        if (removed.Count > 0) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Esborra la cua: treu totes les pistes que encara no han començat i les retorna (per anotar les d'esdeveniments
    /// que no sonaran, NO_EMES). La del tall programat no, perquè ja és com si sonés.
    /// </summary>
    public IReadOnlyList<QueueItem> Clear()
    {
        var events = new List<Action>();
        List<QueueItem> removed;
        lock (_sync)
        {
            UnprepareNext(events);
            removed = [.. _upcoming];
            _upcoming.Clear();
        }
        Raise(events);
        if (removed.Count > 0) Changed?.Invoke();
        return removed;
    }

    // --- Talls (TALLA) ---

    /// <summary>
    /// Obre per endavant la primera pista d'un tall (uns 30 s abans), en una veu de reserva. Retorna la reserva, o null
    /// si no hi ha veus lliures (llavors el tall l'obrirà en el moment, uns segons abans de l'hora).
    /// </summary>
    public Guid? Reserve(QueueItem item)
    {
        lock (_sync)
        {
            if (_reserved.Count >= MaxReserved) return null;
            var playing = Open(item);
            if (playing is null) return null;
            var id = Guid.NewGuid();
            _reserved[id] = playing;
            return id;
        }
    }

    /// <summary>Deixa anar una reserva que ja no es farà servir (esdeveniment omès o esborrat).</summary>
    public void Release(Guid reservation)
    {
        lock (_sync)
        {
            if (_reserved.Remove(reservation, out var playing)) Retire(playing);
        }
    }

    /// <summary>
    /// Programa un tall (TALLA, D-021) al fotograma <paramref name="frame"/>, tot alhora:
    /// - si <paramref name="clearQueue"/>, s'esborra la cua (es retorna el que s'ha tret);
    /// - el contingut (<paramref name="items"/>) va al davant de la cua; el primer, amb la <paramref name="reservation"/> si n'hi ha;
    /// - la que sona comença el fos (<paramref name="fade"/>) al fotograma, i el primer element comença en aquell mateix fotograma.
    /// Si el fotograma ja ha passat (s'hi arriba tard), es talla de seguida. Si la pista següent ja estava programada per
    /// començar abans del tall, se la deixa començar i és ella la que es talla.
    /// </summary>
    public IReadOnlyList<QueueItem> CutAt(long frame, TimeSpan fade, IReadOnlyList<QueueItem> items, bool clearQueue, Guid? reservation = null)
    {
        var events = new List<Action>();
        var removed = new List<QueueItem>();
        lock (_sync)
        {
            // Un TALLA sona sempre a la seva hora (D-103): si hi ha una pausa, s'acaba; un salt pendent ja no es fa
            EndPause(events);
            Playing? reserved = null;
            if (reservation is { } r && _reserved.Remove(r, out var res)) reserved = res;
            if (_cut?.First is { } previous) Retire(previous); // dos talls seguits en pocs segons: mana el darrer

            // La següent: si encara no sona i havia de començar després del tall, torna a la cua
            if (_next is { HasStarted: false } n && (!n.Scheduled || n.StartFrame >= frame)) UnprepareNext(events);
            if (clearQueue)
            {
                removed.AddRange(_upcoming);
                _upcoming.Clear();
            }

            Playing? first = null;
            if (items.Count > 0)
            {
                _upcoming.InsertRange(0, items.Skip(1));
                if (reserved is not null && reserved.Item == items[0] && reserved.Reader.State != ReaderState.Failed)
                {
                    first = reserved;
                    reserved = null;
                }
                else
                {
                    first = Open(items[0]);
                    if (first is null) _upcoming.Insert(0, items[0]); // sense veu lliure (no hauria de passar): sonarà en quant n'hi hagi
                }
            }
            if (reserved is not null) Retire(reserved);

            _cut = new PendingCut(frame, Frames(fade), first);
            ApplyCut();
        }
        Raise(events);
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Aplica el tall programat quan ja no hi ha cap pista següent pel mig.</summary>
    private void ApplyCut()
    {
        if (_cut is not { } cut || _next is not null) return;
        if (_current is { } c && cut.Frame < c.MixFrame)
        {
            c.Voice.Stop(cut.Frame, cut.FadeFrames);
            c.MixFrame = cut.Frame;
            c.EndFrame = cut.Frame + cut.FadeFrames;
        }
        _cutFrame = cut.Frame;
        _next = cut.First; // si és null, la pròxima de la cua començarà al fotograma del tall
        _cut = null;
    }

    /// <summary>
    /// Passa a la pista següent ara: la que sona baixa amb un fos de <paramref name="fade"/> i la següent comença de seguida
    /// (en quant estigui a punt). Si no n'hi ha cap, la que sona simplement s'acaba amb el fos.
    /// </summary>
    public void Skip(TimeSpan fade)
    {
        var events = new List<Action>();
        lock (_sync) SkipLocked(fade, events);
        Raise(events);
    }

    /// <summary><see cref="Skip"/> dins del bloqueig. Si hi ha una pausa (D-103), s'acaba: la pista en pausa es dona per acabada.</summary>
    private void SkipLocked(TimeSpan fade, List<Action> events)
    {
        EndPause(events);
        if (_current is null) return;
        long now = _mixer.FramesRendered;
        int fadeFrames = Frames(fade);
        _current.Voice.Stop(now, fadeFrames);
        _current.MixFrame = now;
        _current.EndFrame = now + fadeFrames;
        _cutFrame = 0; // qui passa a la següent a mà mana més que el tall
        if (_next is { Scheduled: true, HasStarted: false })
        {
            _next.StartFrame = now + Frames(_settings.StartMargin);
            _next.CutFrame = null;
            _next.Voice.Start(_next.StartFrame);
        }
    }

    // --- Pausa i salt dins de la pista (D-103) ---

    /// <summary>La pista que sona està en pausa.</summary>
    public bool IsPaused
    {
        get { lock (_sync) return _paused is not null; }
    }

    /// <summary>La pausa d'ara (quina pista, on, quant falta perquè continuï sola), o null si no n'hi ha.</summary>
    public QueuePause? Pause
    {
        get
        {
            lock (_sync)
            {
                if (_paused is not { } p) return null;
                long autoResume = Math.Max(0, p.Since + Frames(_settings.MaxPause) - _mixer.FramesRendered);
                return new QueuePause(p.Item, FramesToTime(p.OffsetFrames), FramesToTime(Math.Max(0, p.MixOffsetFrames - p.OffsetFrames)),
                    FramesToTime(p.EndOffsetFrames), FramesToTime(autoResume));
            }
        }
    }

    /// <summary>
    /// Fa pausa a la pista que sona: baixa amb un fos curt i es recorda on era. La següent es deixa de preparar (la previsió
    /// es mou mentre dura la pausa). Retorna el motiu si no es pot (en català), o null.
    /// </summary>
    public string? PauseCurrent()
    {
        var events = new List<Action>();
        lock (_sync)
        {
            if (_paused is not null) return null;
            if (_cut is not null || _cutFrame > 0) return "Hi ha un tall programat d'aquí a uns segons: no es pot fer pausa ara.";
            if (_current is not { HasStarted: true } c || c.Voice.State == VoiceState.Finished) return "No sona cap pista.";
            if (_next is { HasStarted: true }) return "La pista següent ja ha començat a sonar.";
            long now = _mixer.FramesRendered;
            long stop = now + Frames(_settings.StartMargin);
            if (stop >= c.MixFrame) return "La pista ja s'està acabant.";

            if (_resume is not null) // un salt que encara no s'havia fet: ja no cal
            {
                Retire(_resume);
                _resume = null;
            }
            UnprepareNext(events);
            c.Voice.Stop(stop, Frames(_settings.PauseFade));
            c.Silent = true;
            _ending.Add(c);
            _current = null;
            _paused = new Paused(c.Item, stop - c.StartFrame, c.MixFrame - c.StartFrame, c.EndFrame - c.StartFrame, c.Played, now);
            _pauseChangedAt = now;
            PauseNotice = null;
        }
        Raise(events);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Continua la pista en pausa des d'on era (amb un fos d'entrada curt). Retorna el motiu si no es pot, o null.</summary>
    public string? ResumePaused()
    {
        lock (_sync) return StartResume();
    }

    /// <summary>
    /// Va a un altre punt de la pista que sona (o de la que està en pausa): <paramref name="position"/> des del punt d'inici
    /// de la pista. La mateixa pista s'obre en una veu nova en aquell punt i, quan és a punt, substitueix l'altra amb un
    /// encadenament molt curt. Retorna el motiu si no es pot (en català), o null.
    /// </summary>
    public string? Seek(Guid itemId, TimeSpan position)
    {
        var events = new List<Action>();
        lock (_sync)
        {
            if (_paused is { } p)
            {
                if (p.Item.Id != itemId) return "Aquesta pista ja no és la que sona.";
                _paused = p with { OffsetFrames = Math.Clamp(Frames(position), 0, Math.Max(0, p.EndOffsetFrames - Frames(TimeSpan.FromSeconds(1)))) };
            }
            else
            {
                if (_cut is not null || _cutFrame > 0) return "Hi ha un tall programat d'aquí a uns segons: no es pot moure la pista ara.";
                if (_current is not { HasStarted: true } c || c.Item.Id != itemId || c.Voice.State == VoiceState.Finished)
                    return "Aquesta pista ja no és la que sona.";
                if (_next is { HasStarted: true }) return "La pista següent ja ha començat a sonar.";
                if (_next is { Scheduled: true } n && FramesToTime(n.StartFrame - _mixer.FramesRendered) < LockedBeforeStart)
                    return "La pista següent està a punt de començar.";
                long offset = Math.Clamp(Frames(position), 0, Math.Max(0, c.EndFrame - c.StartFrame - Frames(TimeSpan.FromSeconds(1))));
                if (_resume is not null) Retire(_resume);
                UnprepareNext(events); // el punt de mescla canviarà: es tornarà a preparar
                _resume = OpenAt(c.Item, offset, c.Played);
                if (_resume is null) return "No hi ha cap veu lliure (torna-ho a provar).";
            }
        }
        Raise(events);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Obre la pista en pausa al seu punt (dins del bloqueig). La pausa s'acaba quan la veu nova comença a sonar.</summary>
    private string? StartResume()
    {
        if (_paused is not { } p) return null;
        if (_resume is not null) return null; // ja s'està obrint
        _resume = OpenAt(p.Item, p.OffsetFrames, p.Played);
        return _resume is null ? "No hi ha cap veu lliure (torna-ho a provar)." : null;
    }

    /// <summary>
    /// Obre una pista a un punt (<paramref name="offsetFrames"/> des del punt d'inici) per continuar la mateixa emissió:
    /// <see cref="Playing.Played"/> és el comptador compartit. El punt es recorda a <c>ResumeOffset</c>.
    /// </summary>
    private Playing? OpenAt(QueueItem item, long offsetFrames, PlayCounter played)
    {
        var voice = _mixer.AcquireVoice();
        if (voice is null) return null;
        long startMs = (item.StartMs ?? 0) + (long)Math.Round(offsetFrames * 1000.0 / _mixer.SampleRate);
        var reader = new VoiceReader(voice, item.Path, _mixer.SampleRate, Ms(startMs), item.EndMs is { } end ? Ms(end) : null);
        return new Playing(item, voice, reader) { Played = played, ResumeOffset = offsetFrames };
    }

    /// <summary>
    /// S'acaba la pausa sense continuar (s'ha passat a una altra pista o hi ha un tall): la pista es dona per acabada amb
    /// el que havia sonat (no sencera).
    /// </summary>
    private void EndPause(List<Action> events)
    {
        if (_resume is not null)
        {
            Retire(_resume);
            _resume = null;
        }
        if (_paused is not { } p) return;
        _paused = null;
        var played = FramesToTime(p.Played.Frames);
        events.Add(() => Ended?.Invoke(new QueueEnded(p.Item, played, false)));
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
                long? cutFrame = started.CutFrame;
                events.Add(() => Started?.Invoke(new QueueStarted(item, frame, cutFrame)));
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
                long playedFrames = ending.Played.Frames + ending.Voice.FramesPlayed; // també el que havia sonat abans d'una pausa o d'un salt
                var played = TimeSpan.FromSeconds((double)playedFrames / _mixer.SampleRate);
                bool complete = ending.PlannedEndFrame - ending.StartFrame <= playedFrames + Frames(TimeSpan.FromMilliseconds(50));
                bool silent = ending.Silent;
                if (silent) ending.Played.Frames = playedFrames; // la continua una altra veu (D-103): encara no s'ha acabat
                Retire(ending);
                _ending.RemoveAt(i);
                if (!silent) events.Add(() => Ended?.Invoke(new QueueEnded(item, played, complete)));
            }

            _toRelease.RemoveAll(v => _mixer.ReleaseVoice(v));

            // Un tall que esperava que comencés la pista següent: ara ja es pot aplicar
            ApplyCut();

            // Pausa i salt (D-103)
            if (_paused is { } paused)
            {
                // Massa estona en pausa: continua sola (mai un silenci llarg per oblit)
                if (_resume is null && now - paused.Since >= Frames(_settings.MaxPause))
                {
                    PauseNotice = $"La pista «{paused.Item}» ha estat en pausa {_settings.MaxPause.TotalMinutes:0} minuts i ha continuat sola.";
                    StartResume();
                    events.Add(() => Problem?.Invoke(PauseNotice));
                }
                // La previsió de la cua es mou mentre dura la pausa: s'avisa cada segon
                if (now - _pauseChangedAt >= _mixer.SampleRate)
                {
                    _pauseChangedAt = now;
                    events.Add(() => Changed?.Invoke());
                }
            }
            if (_resume is { } resume)
            {
                if (resume.Reader.State == ReaderState.Failed)
                {
                    // No s'ha pogut tornar a obrir: la que sonava continua; si era en pausa, s'acaba i passa a la següent
                    string message = $"No s'ha pogut tornar a obrir «{resume.Item}»: {resume.Reader.Error}";
                    Retire(resume);
                    _resume = null;
                    if (_paused is not null)
                    {
                        PauseNotice = message;
                        EndPause(events);
                    }
                    events.Add(() => Problem?.Invoke(message));
                    events.Add(() => Changed?.Invoke());
                }
                else if (resume.Reader.State is ReaderState.Ready or ReaderState.Finished)
                {
                    long start = now + Frames(_settings.StartMargin);
                    if (_current is { } old)
                    {
                        // Salt: la veu vella baixa just quan la nova puja (encadenament molt curt, sense clic)
                        int crossfade = Frames(_settings.SeekCrossfade);
                        old.Voice.Stop(start, crossfade);
                        old.Silent = true;
                        _ending.Add(old);
                        Schedule(resume, start, resume.ResumeOffset, crossfade);
                    }
                    else
                    {
                        Schedule(resume, start, resume.ResumeOffset, Frames(_settings.PauseFade)); // continua després d'una pausa
                    }
                    _current = resume;
                    _resume = null;
                    _paused = null;
                    events.Add(() => Changed?.Invoke());
                }
            }

            // 4. Preparar la següent: si no sona res, de seguida; si no, uns segons abans del punt de mescla.
            // No durant una pausa ni mentre s'obre la pista en un altre punt (el punt de mescla encara no se sap).
            if (_next is null && _upcoming.Count > 0 && _paused is null && _resume is null
                && (_current is null || now >= _current.MixFrame - Frames(_settings.PrepareAhead)))
            {
                var item = _upcoming[0];
                _upcoming.RemoveAt(0);
                _next = Open(item);
                if (_next is null) _upcoming.Insert(0, item); // cap veu lliure: es torna a provar al pròxim Tick
            }

            // 5. Programar-la quan el fitxer ja és obert
            if (_next is { Scheduled: false } ready && ready.Reader.State is ReaderState.Ready or ReaderState.Finished)
            {
                long margin = now + Frames(_settings.StartMargin);
                long start = _current is null ? margin : Math.Max(_current.MixFrame, margin);
                if (_cutFrame > 0)
                {
                    // És la del tall: comença al fotograma del tall (o de seguida, si ja ha passat)
                    start = Math.Max(_cutFrame, margin);
                    ready.CutFrame = _cutFrame;
                    _cutFrame = 0;
                }
                Schedule(ready, start);
            }
        }
        Raise(events);
    }

    /// <summary>Reserva una veu i comença a llegir el fitxer. Retorna null si no hi ha cap veu lliure (no hauria de passar).</summary>
    private Playing? Open(QueueItem item)
    {
        var voice = _mixer.AcquireVoice();
        if (voice is null) return null;
        var reader = new VoiceReader(voice, item.Path, _mixer.SampleRate, Ms(item.StartMs ?? 0), item.EndMs is { } end ? Ms(end) : null);
        return new Playing(item, voice, reader);
    }

    /// <summary>
    /// Programa una pista preparada per començar al fotograma <paramref name="start"/>, i també el seu final:
    /// fos de sortida entre el punt de mescla i el final, amb el solapament màxim (D-024).
    /// </summary>
    /// <param name="offsetFrames">Si la veu no comença pel punt d'inici de la pista sinó més endavant (pausa o salt, D-103):
    /// quants fotogrames més endavant. <see cref="Playing.StartFrame"/> és llavors on hauria començat la pista sencera, de
    /// manera que la posició, el punt de mescla i el final es calculen igual.</param>
    /// <param name="fadeInFrames">Fos d'entrada (0 = sense).</param>
    private void Schedule(Playing p, long start, long offsetFrames = 0, int fadeInFrames = 0)
    {
        long fileMs = (long)(p.Reader.FileDuration ?? TimeSpan.Zero).TotalMilliseconds;
        long startMs = Math.Clamp(p.Item.StartMs ?? 0, 0, fileMs);
        long endMs = Math.Clamp(p.Item.EndMs ?? fileMs, startMs, fileMs);
        long mixMs = Math.Clamp(p.Item.MixMs ?? endMs, startMs, endMs);
        long maxOverlapMs = (long)_settings.MaxOverlap.TotalMilliseconds;
        if (endMs - mixMs > maxOverlapMs) mixMs = endMs - maxOverlapMs;

        long trackStart = start - Math.Max(0, offsetFrames);
        p.StartFrame = trackStart;
        p.MixFrame = trackStart + FramesFromMs(mixMs - startMs);
        p.EndFrame = trackStart + FramesFromMs(endMs - startMs);
        p.PlannedEndFrame = p.EndFrame;
        p.Voice.Start(start, fadeInFrames);
        // Si s'ha saltat més enllà del punt de mescla, el fos comença de seguida (i la següent entra en quant sigui a punt)
        p.Voice.Stop(Math.Max(p.MixFrame, start), (int)Math.Max(0, p.EndFrame - Math.Max(p.MixFrame, start)));
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
            if (_resume is not null) Retire(_resume);
            _resume = null;
            _paused = null;
            if (_cut?.First is { } cutFirst) Retire(cutFirst);
            foreach (var r in _reserved.Values) Retire(r);
            _reserved.Clear();
            _cut = null;
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
        /// <summary>Si és la primera d'un tall, el fotograma on havia de començar.</summary>
        public long? CutFrame { get; set; }

        /// <summary>
        /// Fotogrames que ha sonat la mateixa pista en veus anteriors (D-103: abans d'una pausa o d'un salt). Compartit
        /// entre totes les veus de la mateixa emissió, perquè a l'historial sigui una sola emissió amb el temps sencer.
        /// </summary>
        public PlayCounter Played { get; init; } = new();

        /// <summary>La substitueix una altra veu de la mateixa pista (pausa o salt): en acabar, no és el final de l'emissió.</summary>
        public bool Silent { get; set; }

        /// <summary>Si continua una emissió (D-103), el punt on comença (fotogrames des de l'inici de la pista); si no, -1.</summary>
        public long ResumeOffset { get; init; } = -1;
    }

    /// <summary>Fotogrames sonats d'una emissió en veus que ja s'han acabat (D-103).</summary>
    private sealed class PlayCounter
    {
        public long Frames { get; set; }
    }

    /// <summary>Una pista en pausa (D-103): on s'ha aturat (fotogrames des de l'inici de la pista) i quan.</summary>
    private sealed record Paused(QueueItem Item, long OffsetFrames, long MixOffsetFrames, long EndOffsetFrames, PlayCounter Played, long Since);

    /// <summary>Un tall programat: on, amb quin fos i quina pista comença (null si l'esdeveniment no en té cap o no s'ha pogut obrir).</summary>
    private sealed record PendingCut(long Frame, int FadeFrames, Playing? First);
}
