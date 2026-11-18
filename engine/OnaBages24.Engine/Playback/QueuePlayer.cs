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

    /// <summary>
    /// Pista encallada (fase 5, pas 2; D-157): si la que sona fa aquesta estona que no rep dades del fitxer (el disc o la
    /// xarxa no responen), se salta a la següent.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pistes d'emergència (fase 5, pas 4; D-159): si la pista següent ja hauria de sonar i fa aquesta estona que encara no
    /// s'ha pogut obrir, sona una pista d'emergència del disc local.
    /// </summary>
    public TimeSpan EmergencyWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Quan la pista que tocava ja és a punt, la d'emergència baixa en aquest temps i entra la que tocava.</summary>
    public TimeSpan EmergencyFade { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Un fitxer que fa aquesta estona que s'està obrint es dona per perdut i se salta (fase 5, pas 4).</summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Un stream que no connecta abans de començar es torna a provar aquesta estona; després se salta (D-030). Si es talla
    /// mentre sona, el temps per recuperar-se és <see cref="StallTimeout"/> (com una pista encallada).
    /// </summary>
    public TimeSpan StreamConnectFor { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sense rebre dades d'un stream aquesta estona, es dona per tallat i s'hi torna a connectar (cada intent també espera
    /// com a molt això). Ha de ser més curt que <see cref="StallTimeout"/>, perquè la reconnexió hi càpiga.
    /// </summary>
    public TimeSpan StreamTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Reconnexió durant tota la durada (D-031): mentre el stream és caigut, cada quant es torna a provar de connectar-s'hi.</summary>
    public TimeSpan StreamRetryEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Reconnexió (D-031): quanta estona ha de rebre àudio seguit (i, si té silenci, sense silenci) perquè torni a sonar.</summary>
    public TimeSpan StreamStableFor { get; init; } = TimeSpan.FromSeconds(5);
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

/// <summary>
/// Una pista que s'ha acabat: quant ha sonat i si ha arribat al final previst. <paramref name="Dropouts"/>: quantes
/// vegades la veu s'ha quedat sense dades a mitja pista (talls: el disc o la xarxa no han anat prou ràpid; fase 5, pas 2).
/// </summary>
public sealed record QueueEnded(QueueItem Item, TimeSpan Played, bool Complete, int Dropouts = 0);

/// <summary>
/// Una pista encallada (fase 5, pas 2; D-157): ha deixat de rebre dades del fitxer a <paramref name="Position"/> i s'ha
/// passat a la següent; o la lectura ha fallat a mitja pista (<paramref name="Error"/>) i s'ha acabat abans d'hora.
/// </summary>
public sealed record QueueStuck(QueueItem Item, TimeSpan Position, string? Error);

/// <summary>Una pista que no s'ha pogut fer sonar (fitxer que falta o il·legible): se salta.</summary>
/// <summary>Una pista que no s'ha pogut obrir i s'ha saltat; si és un stream amb pista de reserva, <paramref name="Fallback"/> sona al seu lloc (D-178).</summary>
public sealed record QueueFailed(QueueItem Item, string Message, QueueItem? Fallback = null)
{
    /// <summary>El motiu acabat en punt (per posar-hi una frase darrere sense «..»).</summary>
    public string Sentence => Message.TrimEnd() is var m && m.EndsWith('.') ? m : m + ".";
}

/// <summary>
/// Un stream que sona (fase 2, pas 7b; D-030): s'ha tallat (<see cref="Kind"/> «tall»; es prova de reconnectar), s'ha
/// recuperat («recuperat») o s'ha acabat perquè arribava en silenci («silenci»). Amb reconnexió durant tota la durada
/// (D-031, D-180): ha caigut i sona música mentre es prova de reconnectar («caigut»), torna a sonar («tornat») o s'ha
/// acabat el temps previst, amb els talls i el temps sense senyal («resum»).
/// </summary>
public sealed record QueueStreamNotice(QueueItem Item, string Kind, string Message);

/// <summary>
/// Ha començat una pista d'emergència (fase 5, pas 4; D-159): <paramref name="Waiting"/> ja hauria de sonar però encara no
/// s'ha pogut obrir.
/// </summary>
public sealed record QueueEmergency(QueueItem Waiting, QueueItem Emergency);

/// <summary>Posició de la pista que sona: quant ha sonat, quant falta per al final i per al punt de mescla, i quant dura en total.</summary>
/// <summary>Previsió de la cua: d'aquí a quant s'acaba la que sona i d'aquí a quant comença cada pista que espera (null = no se sap).</summary>
public sealed record QueueForecast(TimeSpan? CurrentEndsIn, IReadOnlyList<(Guid Id, TimeSpan? StartsIn)> Starts);

/// <summary>
/// Com va un stream (fase 2, pas 7e), per a la interfície: el que sona o el que s'espera que torni (D-180).
/// <paramref name="State"/>: <c>live</c> (en directe), <c>silent</c> (connectat, però arriba en silenci), <c>cut</c> (tallat:
/// es reconnecta), <c>down</c> (ha caigut i sona música mentre es prova de reconnectar) o <c>returning</c> (ja torna: és la
/// següent). <paramref name="Elapsed"/> i <paramref name="Remaining"/>: des de l'inici previst i fins a l'hora prevista de
/// final (on comença la resta). <paramref name="Probe"/>, amb <c>down</c>: <c>trying</c> (no connecta), <c>receiving</c> (rep
/// àudio, encara no fa prou estona) o <c>ready</c> (funciona: tornarà en acabar la cançó).
/// </summary>
public sealed record QueueStreamStatus(QueueItem Item, string State, string? Title, TimeSpan Elapsed, TimeSpan Remaining,
    bool Reconnects, bool ReturnAfterSong, int Cuts, TimeSpan? DownFor, string? Probe);

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
    private string _streamSignature = ""; // l'últim estat dels streams que s'ha avisat (fase 2, pas 7e)
    private Playing? _current;
    private Playing? _next;
    private PendingCut? _cut;  // tall programat que encara no s'ha pogut aplicar (la següent ja començava abans)
    private long _cutFrame;    // si no és 0: la pròxima pista no pot començar abans d'aquest fotograma (és la del tall)

    // Pistes d'emergència (fase 5, pas 4; D-159)
    private Playing? _filler;        // la pista d'emergència que s'està obrint (encara no sona)
    private long _waitingSince = -1; // des de quin fotograma la pista següent ja hauria de sonar i no és a punt (-1 = no espera)

    /// <summary>
    /// D'on surten les pistes d'emergència (fase 5, pas 4): retorna una pista del disc local, o null si no n'hi ha. Es crida
    /// des del fil de control i no ha de tocar el disc. Si és null, no n'hi ha.
    /// </summary>
    public Func<QueueItem?>? EmergencyPicker { get; set; }

    /// <summary>Ha començat una pista d'emergència perquè la que tocava no s'ha pogut obrir a temps.</summary>
    public event Action<QueueEmergency>? EmergencyStarted;

    // Reconnexió durant tota la durada (D-031, D-180): el stream que ha caigut i s'espera que torni (null = cap)
    private StreamHold? _hold;

    /// <summary>Per entrar en reconnexió, ha de quedar com a mínim això a més de <see cref="QueueSettings.PrepareAhead"/> (si no, se salta, com abans).</summary>
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Un stream amb reconnexió ha caigut i cal música de la rotació per omplir (D-031): l'AutoDJ hi posa una pista amb
    /// <see cref="AddFill"/>. N'hi ha com a molt una d'avançada, al davant de la cua.
    /// </summary>
    public bool WantsFill
    {
        get { lock (_sync) return _hold is { Down: true, Returning: false } h && !FillFront(h); }
    }

    /// <summary>
    /// Posa una pista de farciment (D-031) al davant de la cua, mentre el stream és caigut. Quan el stream torna o s'acaba
    /// el temps previst, les que no han sonat es treuen. Retorna false (i no fa res) si ja no cal.
    /// </summary>
    public bool AddFill(QueueItem item)
    {
        lock (_sync)
        {
            if (_hold is not { Down: true, Returning: false } h || FillFront(h)) return false;
            _upcoming.Insert(0, item);
            h.Fill.Add(item.Id);
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>La primera de la cua és de farciment.</summary>
    private bool FillFront(StreamHold h) => _upcoming.Count > 0 && h.Fill.Contains(_upcoming[0].Id);

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
    /// <summary>Talls, recuperacions i final per silenci dels streams (fase 2, pas 7b).</summary>
    public event Action<QueueStreamNotice>? StreamNotice;
    /// <summary>Una pista encallada, o que ha deixat de llegir-se a mitja pista (fase 5, pas 2).</summary>
    public event Action<QueueStuck>? Stuck;

    /// <summary>Una veu que encara sona aquesta estona després del seu final previst s'atura de cop (fase 5, pas 2).</summary>
    private static readonly TimeSpan OverdueMargin = TimeSpan.FromSeconds(1);
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
            bool holdAhead = _hold is not null; // reconnexió (D-031): la resta comença a l'hora prevista de final del stream
            foreach (var item in _upcoming)
            {
                long? fixedStart = cutStart;
                if (holdAhead && !_hold!.Fill.Contains(item.Id))
                {
                    fixedStart ??= _hold.MixFrame;
                    holdAhead = false;
                }
                entries.Add((item, null, fixedStart));
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
            // Mentre sona un stream amb reconnexió, l'operador en fa sonar una altra: ja no s'espera que torni (D-031)
            if (_hold is { } h && _current?.Item.Id == h.Stream.Id) FinishHold(_mixer.FramesRendered, events, reachedEnd: false);
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
        // Un stream no (D-180): la seva durada és la de l'esdeveniment, no la de la biblioteca (es desfaria la tornada)
        bool Differs(QueueItem i) => !i.IsStream && i.TrackId == track.Id &&
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
            FinishHold(_mixer.FramesRendered, events, reachedEnd: false); // i ja no s'espera que torni cap stream (D-031)
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
        lock (_sync)
        {
            // L'operador passa a la següent mentre sona un stream amb reconnexió: ja no s'espera que torni (D-031)
            if (_hold is { } h && _current?.Item.Id == h.Stream.Id) FinishHold(_mixer.FramesRendered, events, reachedEnd: false);
            SkipLocked(fade, events);
        }
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
            if (c.Item.IsStream) return "Un stream en directe no es pot posar en pausa.";
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
                if (c.Item.IsStream) return "Un stream en directe no es pot moure.";
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

            // 0. Un stream que espera per sonar i no ha pogut connectar, si encara no és l'hora (D-179): es torna a obrir
            if (_next is { HasStarted: false, Scheduled: false, Returning: false, Item.IsStream: true, Reader.State: ReaderState.Failed } early && StreamHasTime(now))
            {
                Retire(early);
                _next = Open(early.Item);
                if (_next is null) _upcoming.Insert(0, early.Item); // cap veu lliure: es torna a provar al pròxim Tick
            }

            // 1. La pista que s'estava preparant no es pot obrir: se salta (i es prova la següent més avall)
            if (_next is { HasStarted: false, Reader.State: ReaderState.Failed } failed)
            {
                var item = failed.Item;
                string message = failed.Reader.Error ?? "No s'ha pogut obrir el fitxer.";
                Retire(failed);
                _next = null;
                if (_hold is { } h && h.Stream.Id == item.Id)
                {
                    // El stream que tornava ha tornat a caure abans de sonar (D-031): continua la música i es torna a provar
                    h.Returning = false;
                    h.RetryAt = now;
                    events.Add(() => Changed?.Invoke());
                }
                else if (!EnterHoldAt(item, DueFrame(now), now, $"No s'ha pogut connectar a l'hora: {Sentence(message)}", events))
                {
                    // Un stream que no connecta i té pista de reserva (D-010): sona la reserva al seu lloc
                    var fallback = item.StreamFallback;
                    if (fallback is not null) _upcoming.Insert(0, fallback);
                    events.Add(() => Failed?.Invoke(new QueueFailed(item, message, fallback)));
                    events.Add(() => Changed?.Invoke());
                }
            }

            // 1a. Streams i puntualitat (D-179):
            // - el stream d'un tall obert per endavant que no ha pogut connectar es torna a obrir, i així es va provant fins a
            //   l'hora (si l'emissió comença uns segons abans, sona puntual);
            foreach (var (id, reserved) in _reserved.ToList())
            {
                if (!reserved.Item.IsStream || reserved.Reader.State != ReaderState.Failed) continue;
                Retire(reserved);
                if (Open(reserved.Item) is { } again) _reserved[id] = again;
                else _reserved.Remove(id);
            }
            // - un stream que ja hauria de sonar (l'hora del tall, o el punt de mescla de la que sona) i encara no és a punt es
            //   dona per perdut de seguida: la reserva (o la següent) entra a l'hora, sense esperar que acabi de provar-ho.
            if (_next is { HasStarted: false, Scheduled: false, Reader: ILiveVoiceReader { State: ReaderState.Opening } late } waiting && StreamDue(now))
            {
                var item = waiting.Item;
                string message = late.LastError is { } why ? $"A l'hora de començar encara no s'hi havia pogut connectar: {why}"
                    : "A l'hora de començar encara no s'hi havia pogut connectar.";
                long due = DueFrame(now);
                Retire(waiting);
                _next = null;
                if (!EnterHoldAt(item, due, now, Sentence(message), events))
                {
                    var fallback = item.StreamFallback;
                    if (fallback is not null) _upcoming.Insert(0, fallback);
                    events.Add(() => Failed?.Invoke(new QueueFailed(item, message, fallback)));
                    events.Add(() => Changed?.Invoke());
                }
            }

            // 1b. Un fitxer que fa massa estona que s'està obrint (fase 5, pas 4; D-159): la xarxa no respon. Es dona per perdut
            // i se salta; el fil de lectura es deixa anar sense esperar-lo (pot estar encallat).
            if (_next is { HasStarted: false, Scheduled: false, Reader.State: ReaderState.Opening } slow
                && now - slow.OpenedFrame >= Frames(_settings.OpenTimeout))
            {
                var item = slow.Item;
                string message = $"No s'ha pogut obrir en {_settings.OpenTimeout.TotalSeconds:0} s: el disc o la xarxa no responen.";
                slow.Stuck = true;
                Retire(slow);
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
                if (started.Returning && _hold is { } h && h.Stream.Id == item.Id)
                {
                    // Torna a sonar (D-031): s'acaba el temps sense senyal
                    var gap = FramesToTime(Math.Max(0, frame - h.DownSince));
                    h.DownFrames += Math.Max(0, frame - h.DownSince);
                    h.Down = false;
                    h.Returning = false;
                    events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, "tornat",
                        $"Ha tornat després de {Span(gap)} sense senyal i torna a sonar en directe.")));
                }
            }

            // 3. Xarxa de seguretat (fase 5, pas 2; D-157): una veu que encara sona més d'un segon després del seu final previst
            // s'atura de cop. Passa si es queda sense dades durant el fos (el fos només avança amb dades): si no, no s'acabaria
            // mai i es quedaria la veu ocupada.
            if (_current is { Scheduled: true } overdue && overdue.Voice.State == VoiceState.Playing && now > overdue.EndFrame + Frames(OverdueMargin))
            {
                overdue.Voice.Stop(now);
                if (!overdue.Stuck)
                {
                    overdue.Stuck = true;
                    var item = overdue.Item;
                    var position = FramesToTime(Math.Max(0, now - overdue.StartFrame));
                    events.Add(() => Stuck?.Invoke(new QueueStuck(item, position, null)));
                }
            }
            foreach (var ending in _ending)
            {
                if (ending.Voice.State == VoiceState.Playing && now > ending.EndFrame + Frames(OverdueMargin))
                {
                    ending.Voice.Stop(now);
                    ending.Stuck = true; // el fil de lectura pot estar encallat: no s'espera en retirar-la
                }
            }

            // 3b. Les veus que han acabat: s'alliberen
            if (_current is not null && _current.Voice.State == VoiceState.Finished)
            {
                // La lectura ha fallat a mitja pista: s'ha acabat abans d'hora (la següent es prepara de seguida, més avall)
                if (_current.Reader.State == ReaderState.Failed && !_current.Stuck)
                {
                    _current.Stuck = true;
                    var item = _current.Item;
                    var position = FramesToTime(Math.Max(0, now - _current.StartFrame));
                    string? error = _current.Reader.Error;
                    events.Add(() => Stuck?.Invoke(new QueueStuck(item, position, error)));
                }
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
                // Els talls d'una encallada ja surten com a encallada
                int dropouts = ending.Stuck ? 0 : ending.Voice.Underruns;
                bool silent = ending.Silent;
                if (silent) ending.Played.Frames = playedFrames; // la continua una altra veu (D-103): encara no s'ha acabat
                Retire(ending);
                _ending.RemoveAt(i);
                if (!silent) events.Add(() => Ended?.Invoke(new QueueEnded(item, played, complete, dropouts)));
            }

            // 4. Pista encallada (fase 5, pas 2; D-157): la que sona fa estona que no rep dades del fitxer (el rellotge del
            // mesclador avança però no sona res). Es passa a la següent. Després del punt de mescla ja no cal: la següent hi és.
            if (_current is { Scheduled: true, HasStarted: true, Stuck: false } cur && cur.Voice.State == VoiceState.Playing && now < cur.MixFrame)
            {
                long playedNow = cur.Voice.FramesPlayed;
                if (playedNow != cur.LastPlayed)
                {
                    cur.LastPlayed = playedNow;
                    cur.LastProgressFrame = now;
                }
                else if (now - cur.LastProgressFrame >= Frames(_settings.StallTimeout))
                {
                    cur.Stuck = true;
                    var item = cur.Item;
                    var position = FramesToTime(Math.Max(0, now - cur.StartFrame));
                    long mix = cur.MixFrame, end = cur.EndFrame; // abans del salt, que els canvia
                    SkipLocked(TimeSpan.Zero, events); // de cop: no té dades (un fos sense dades no avançaria)
                    // Un stream amb reconnexió (D-031): sona música mentre es prova de reconnectar
                    if (!EnterHold(item, mix, end, now,
                        $"S'ha tallat al minut {(int)position.TotalMinutes}:{position.Seconds:00} i no s'ha recuperat en {_settings.StallTimeout.TotalSeconds:0} s.", events))
                        events.Add(() => Stuck?.Invoke(new QueueStuck(item, position, null)));
                    events.Add(() => Changed?.Invoke());
                }
            }

            // 4c. Streams (fase 2, pas 7b; D-030): els talls i les recuperacions, i el final per silenci
            foreach (var p in new[] { _current, _next })
            {
                if (p?.Reader is not ILiveVoiceReader stream) continue;
                foreach (var notice in stream.TakeNotices())
                {
                    var item = p.Item;
                    events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, notice.Cut ? "tall" : "recuperat", notice.Message)));
                }
            }
            if (_current is { Scheduled: true, HasStarted: true, Stuck: false, Reader: ILiveVoiceReader live } quiet
                && quiet.Item.StreamSilenceSeconds is int silenceSeconds and > 0 && now < quiet.MixFrame
                && live.SilentFor >= TimeSpan.FromSeconds(silenceSeconds))
            {
                var item = quiet.Item;
                var position = FramesToTime(Math.Max(0, now - quiet.StartFrame));
                long mix = quiet.MixFrame, end = quiet.EndFrame;
                SkipLocked(TimeSpan.FromMilliseconds(QueueItem.StreamFadeMs), events);
                // Amb reconnexió (D-031), el silenci és com un tall: sona música fins que torni amb so
                if (!EnterHold(item, mix, end, now,
                    $"Arribava en silenci des de fa {silenceSeconds} s (minut {(int)position.TotalMinutes}:{position.Seconds:00}).", events))
                    events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, "silenci",
                        $"Arribava en silenci des de fa {silenceSeconds} s: s'ha acabat al minut {(int)position.TotalMinutes}:{position.Seconds:00} i ha passat a la següent.")));
                events.Add(() => Changed?.Invoke());
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

            // Reconnexió durant tota la durada (D-031, D-180)
            if (_hold is not null) TickHold(now, events);

            // 4. Preparar la següent: si no sona res, de seguida; si no, uns segons abans del punt de mescla.
            // No durant una pausa ni mentre s'obre la pista en un altre punt (el punt de mescla encara no se sap).
            if (_next is null && _upcoming.Count > 0 && _paused is null && _resume is null
                && (_current is null || now >= _current.MixFrame - Frames(_settings.PrepareAhead))
                && !WaitingForFill(now))
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
                else if (_current is { Item.Origin: QueueOrigin.Emergency, Scheduled: true } emergency)
                {
                    // Sona una pista d'emergència (D-159): baixa amb un fos i la que tocava entra de seguida
                    start = margin;
                    int fade = Frames(_settings.EmergencyFade);
                    if (emergency.EndFrame > start + fade)
                    {
                        emergency.Voice.Stop(start, fade);
                        emergency.MixFrame = Math.Min(emergency.MixFrame, start);
                        emergency.EndFrame = start + fade;
                    }
                }
                else if (ready.Returning && _hold is { } hold)
                {
                    // El stream torna (D-031): de seguida (la música baixa amb un fos) o al punt de mescla de la cançó que
                    // sona. S'acaba a l'hora prevista del principi, no més tard.
                    if (ready.ReturnNow || _current is not { Scheduled: true })
                    {
                        start = now + Frames(StreamVoiceReader.FillBefore);
                        int fade = Frames(Ms(QueueItem.StreamFadeMs));
                        if (_current is { Scheduled: true } song && song.EndFrame > start + fade)
                        {
                            song.Voice.Stop(start, fade);
                            song.MixFrame = Math.Min(song.MixFrame, start);
                            song.EndFrame = start + fade;
                        }
                    }
                    ready.Until = (hold.MixFrame, hold.EndFrame);
                }
                Schedule(ready, start);
                DropFiller(); // la que tocava ja és a punt: la d'emergència que s'obria ja no cal
            }

            // 6. Pistes d'emergència (fase 5, pas 4; D-159)
            TickEmergency(now, events);

            // 7. L'estat dels streams ha canviat (connectat, tallat, en silenci, el títol…): la interfície ho ha de saber
            string signature = StreamSignature(now);
            if (signature != _streamSignature)
            {
                _streamSignature = signature;
                events.Add(() => Changed?.Invoke());
            }
        }
        Raise(events);
    }

    /// <summary>
    /// Pistes d'emergència (D-159): si la pista següent ja hauria de sonar (no sona res, o la que sona ja és al punt de
    /// mescla) i fa <see cref="QueueSettings.EmergencyWait"/> que no és a punt, se n'obre una del disc local. Quan és a
    /// punt, comença de seguida; quan comença, passa a ser la que sona. Dins del bloqueig.
    /// </summary>
    private void TickEmergency(long now, List<Action> events)
    {
        if (_filler is { } f)
        {
            bool timedOut = f.Reader.State == ReaderState.Opening && now - f.OpenedFrame >= Frames(_settings.OpenTimeout);
            if (f.Reader.State == ReaderState.Failed || timedOut)
            {
                var item = f.Item;
                string message = f.Reader.Error ?? $"No s'ha pogut obrir en {_settings.OpenTimeout.TotalSeconds:0} s.";
                f.Stuck = timedOut;
                Retire(f);
                _filler = null;
                _waitingSince = now; // es torna a esperar abans de provar-ne una altra
                events.Add(() => Failed?.Invoke(new QueueFailed(item, message)));
                return;
            }
            if (!f.Scheduled && f.Reader.State is ReaderState.Ready or ReaderState.Finished)
            {
                // De seguida; si la que sona és una altra d'emergència, al seu punt de mescla (encadenades, sense silenci)
                long margin = now + Frames(_settings.StartMargin);
                Schedule(f, _current is { Scheduled: true } c ? Math.Max(margin, c.MixFrame) : margin);
            }
            if (f.HasStarted)
            {
                if (_current is not null) _ending.Add(_current); // la d'abans ja feia el fos final
                _current = f;
                _filler = null;
                _waitingSince = -1;
                var waiting = _next?.Item;
                var item = f.Item;
                long frame = f.Voice.ActualStartFrame;
                events.Add(() => Started?.Invoke(new QueueStarted(item, frame)));
                if (waiting is not null) events.Add(() => EmergencyStarted?.Invoke(new QueueEmergency(waiting, item)));
                events.Add(() => Changed?.Invoke());
            }
            return;
        }

        // Si ja sona una pista d'emergència i la que tocava encara no és a punt, la següent d'emergència es prepara abans,
        // com qualsevol pista, perquè entri al seu punt de mescla
        bool chaining = _current is { Item.Origin: QueueOrigin.Emergency, Scheduled: true } e && now >= e.MixFrame - Frames(_settings.PrepareAhead);
        bool needed = _next is { Scheduled: false } && _paused is null && _resume is null
            && (_current is null || now >= _current.MixFrame || chaining);
        if (!needed)
        {
            _waitingSince = -1;
            return;
        }
        if (_waitingSince < 0) _waitingSince = now;
        if (!chaining && now - _waitingSince < Frames(_settings.EmergencyWait)) return;
        _waitingSince = now; // si no n'hi ha cap o no hi ha veu lliure, es torna a provar d'aquí a una estona
        if (EmergencyPicker?.Invoke() is { } emergencyItem) _filler = Open(emergencyItem);
    }

    // --- Reconnexió durant tota la durada (D-031, D-180) ---

    /// <summary>
    /// <see cref="EnterHold"/> per a un stream que no ha pogut començar: havia de començar a <paramref name="startFrame"/>, i
    /// la resta comença on hauria acabat.
    /// </summary>
    private bool EnterHoldAt(QueueItem item, long startFrame, long now, string message, List<Action> events)
    {
        long startMs = item.StartMs ?? 0;
        long endMs = item.EndMs ?? QueueItem.DefaultStreamMs;
        return EnterHold(item, startFrame + FramesFromMs(PlannedMixMs(item)), startFrame + FramesFromMs(endMs - startMs), now, message, events);
    }

    /// <summary>D'un stream, quant hi ha de l'inici al punt de mescla (on comença la resta), en mil·lisegons.</summary>
    private static long PlannedMixMs(QueueItem item)
    {
        long startMs = item.StartMs ?? 0;
        long endMs = item.EndMs ?? QueueItem.DefaultStreamMs;
        return Math.Clamp(item.MixMs ?? endMs, startMs, endMs) - startMs;
    }

    /// <summary>
    /// Un stream amb reconnexió (D-031) ha caigut, no ha connectat a l'hora o arriba en silenci: fins a
    /// <paramref name="mixFrame"/> sona música de farciment (primer la pista de reserva, si en té) i es prova de reconnectar.
    /// Si torna a caure el mateix stream, es torna a començar el cicle. Retorna false si no té reconnexió o queda massa poc
    /// temps: llavors es fa com sempre (se salta). Dins del bloqueig.
    /// </summary>
    private bool EnterHold(QueueItem item, long mixFrame, long endFrame, long now, string message, List<Action> events)
    {
        StreamHold h;
        bool first = false;
        if (_hold is { } same && same.Stream.Id == item.Id)
        {
            h = same;
            h.Cuts++;
        }
        else
        {
            if (!item.StreamReconnect || !IsLive(item) || mixFrame - now < Frames(_settings.PrepareAhead + MinHold)) return false;
            FinishHold(now, events, reachedEnd: false); // un altre stream que s'esperava (no hauria de passar)
            h = new StreamHold(item, mixFrame - FramesFromMs(PlannedMixMs(item)), mixFrame, endFrame) { Cuts = 1 };
            _hold = h;
            first = true;
            if (item.StreamFallback is { } fallback)
            {
                _upcoming.Insert(0, fallback);
                h.Fill.Add(fallback.Id);
            }
        }
        h.Down = true;
        h.Returning = false;
        h.DownSince = now;
        h.RetryAt = now;
        h.StableSince = -1;
        h.NoFillSince = -1;
        h.ProbeStatus = null;

        string music = first && item.StreamFallback is not null ? "Sona la pista de reserva i després música de la rotació" : "Sona música de la rotació";
        string text = $"{message} {music} mentre es prova de reconnectar (queden {Span(FramesToTime(h.MixFrame - now))}).";
        events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, "caigut", text)));
        events.Add(() => Changed?.Invoke());
        return true;
    }

    /// <summary>
    /// Cada volta mentre s'espera un stream (D-031): la connexió de prova (cada <see cref="QueueSettings.StreamRetryEvery"/>),
    /// la comprovació que funciona (<see cref="QueueSettings.StreamStableFor"/> rebent àudio seguit) i la tornada; i, uns
    /// segons abans de l'hora prevista de final, s'acaba. Dins del bloqueig.
    /// </summary>
    private void TickHold(long now, List<Action> events)
    {
        var h = _hold!;
        if (now >= h.MixFrame - Frames(_settings.PrepareAhead))
        {
            FinishHold(now, events, reachedEnd: true);
            return;
        }
        if (!h.Down || h.Returning) return;
        h.NoFillSince = FillFront(h) ? -1 : h.NoFillSince < 0 ? now : h.NoFillSince;

        if (h.Probe is null)
        {
            if (now < h.RetryAt) return;
            h.Probe = Open(h.Stream); // una veu que no sona: només es connecta i es mira si arriba àudio
            h.StableSince = -1;
            return;
        }
        var probe = h.Probe;
        if (probe.Reader.State == ReaderState.Failed || probe.Reader is not ILiveVoiceReader reader)
        {
            ProbeSays(h, $"La connexió de prova no s'ha pogut connectar: {Sentence(probe.Reader.Error ?? "sense motiu")} Es torna a provar cada {_settings.StreamRetryEvery.TotalSeconds:0} s.", events);
            Retire(probe);
            h.Probe = null;
            h.RetryAt = now + Frames(_settings.StreamRetryEvery);
            return;
        }

        // Funciona? Connectat, rebent àudio seguit i, si té silenci, sense silenci; si es talla, es torna a comptar
        bool cut = reader.TakeNotices().Any(n => n.Cut);
        bool silent = h.Stream.StreamSilenceSeconds is > 0 && reader.SilentFor >= TimeSpan.FromSeconds(1);
        if (cut || silent || reader.State != ReaderState.Ready || !reader.Connected)
        {
            if (cut) ProbeSays(h, "La connexió de prova s'ha tornat a tallar.", events);
            else if (silent && reader.Connected) ProbeSays(h, "La connexió de prova rep el stream, però arriba en silenci.", events);
            h.StableSince = -1;
            return;
        }
        if (h.StableSince < 0) h.StableSince = now;
        if (now - h.StableSince < Frames(_settings.StreamStableFor))
        {
            ProbeSays(h, "La connexió de prova torna a rebre àudio.", events);
            return;
        }
        ProbeSays(h, h.Stream.StreamReturnAfterSong
            ? $"Fa {_settings.StreamStableFor.TotalSeconds:0} s que arriba bé: tornarà al punt de mescla de la cançó que sona."
            : $"Fa {_settings.StreamStableFor.TotalSeconds:0} s que arriba bé: torna ara.", events);

        // Torna. Si la següent ja comença a sonar (s'estan encadenant), un moment; si ha d'esperar la cançó, fins que s'acosti la mescla.
        if (_next is { HasStarted: true }) return;
        if (h.Stream.StreamReturnAfterSong && _current is { Scheduled: true } song && now < song.MixFrame - Frames(_settings.PrepareAhead)) return;
        if (_next is { } prepared)
        {
            if (h.Fill.Contains(prepared.Item.Id))
            {
                Retire(prepared);
                _next = null;
            }
            else UnprepareNext(events);
        }
        DropFiller();
        _upcoming.RemoveAll(i => h.Fill.Contains(i.Id)); // la música que encara no ha sonat ja no cal
        probe.Returning = true;
        probe.ReturnNow = !h.Stream.StreamReturnAfterSong;
        probe.EventStartFrame = h.StartFrame;
        _next = probe;
        h.Probe = null;
        h.Returning = true;
        events.Add(() => Changed?.Invoke());
    }

    /// <summary>
    /// Anota al registre com va la connexió de prova (D-180), només quan canvia: per saber per què un stream no ha tornat.
    /// </summary>
    private void ProbeSays(StreamHold h, string status, List<Action> events)
    {
        if (h.ProbeStatus == status) return;
        h.ProbeStatus = status;
        var item = h.Stream;
        events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, "prova", status)));
    }

    /// <summary>
    /// Mentre el stream és caigut, la pista següent ha de ser de farciment (no la resta de l'esdeveniment, que espera l'hora
    /// prevista): s'espera que l'AutoDJ n'hi posi una. Si en <see cref="QueueSettings.EmergencyWait"/> no n'hi ha cap, sona
    /// el que hi hagi a la cua (mai silenci).
    /// </summary>
    private bool WaitingForFill(long now) =>
        _hold is { Down: true, Returning: false } h && !FillFront(h) && (h.NoFillSince < 0 || now - h.NoFillSince < Frames(_settings.EmergencyWait));

    /// <summary>
    /// Ja no s'espera el stream (D-031): s'acaba el temps previst (<paramref name="reachedEnd"/>, uns segons abans de l'hora
    /// de final), hi ha un tall nou o l'operador l'ha saltat. La música de farciment que no ha sonat es treu (si hi ha res més
    /// a la cua) i, a l'hora de final, la cançó de farciment que sona es talla perquè la resta comenci a l'hora. Dins del bloqueig.
    /// </summary>
    private void FinishHold(long now, List<Action> events, bool reachedEnd)
    {
        if (_hold is not { } h) return;
        _hold = null;
        if (h.Probe is not null) Retire(h.Probe);
        if (_next is { HasStarted: false, Returning: true } back && back.Item.Id == h.Stream.Id)
        {
            Retire(back);
            _next = null;
        }

        bool restAhead = _upcoming.Any(i => !h.Fill.Contains(i.Id));
        if (restAhead)
        {
            _upcoming.RemoveAll(i => h.Fill.Contains(i.Id));
            if (_next is { HasStarted: false } fill && h.Fill.Contains(fill.Item.Id))
            {
                Retire(fill);
                _next = null;
            }
            // A l'hora de final, la cançó de farciment que encara sonaria es talla (si acaba abans, la resta hi entra a sobre)
            bool fillPlaying = (_current is { Scheduled: true } c && h.Fill.Contains(c.Item.Id) && c.MixFrame > h.MixFrame)
                || (_next is { HasStarted: true } n && h.Fill.Contains(n.Item.Id));
            if (reachedEnd && fillPlaying && _cut is null)
            {
                _cut = new PendingCut(h.MixFrame, Frames(Ms(QueueItem.StreamFadeMs)), null);
                ApplyCut();
            }
        }

        if (h.Down) h.DownFrames += Math.Max(0, (reachedEnd ? h.MixFrame : now) - h.DownSince);
        var item = h.Stream;
        string cuts = h.Cuts == 1 ? "1 tall" : $"{h.Cuts} talls";
        string text = reachedEnd
            ? $"S'ha acabat el temps previst: {cuts}, {Span(FramesToTime(h.DownFrames))} sense senyal."
            : $"Ja no s'espera que torni (un tall nou o l'operador l'ha saltat): {cuts}, {Span(FramesToTime(h.DownFrames))} sense senyal.";
        events.Add(() => StreamNotice?.Invoke(new QueueStreamNotice(item, "resum", text)));
        events.Add(() => Changed?.Invoke());
    }

    // --- Estat dels streams per a la interfície (fase 2, pas 7e) ---

    /// <summary>Com va el stream que sona o el que s'espera que torni (D-180), o null si no n'hi ha cap.</summary>
    public QueueStreamStatus? StreamStatus
    {
        get { lock (_sync) return StreamStatusLocked(_mixer.FramesRendered); }
    }

    /// <summary>
    /// Els streams que esperen per sonar i ja s'estan connectant (el que es prepara i els dels talls oberts per endavant):
    /// <c>connecting</c> o <c>ready</c>, per id de l'element de la cua.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> UpcomingStreamStates
    {
        get { lock (_sync) return UpcomingStreamStatesLocked(); }
    }

    /// <summary>Un stream es dona per «en silenci» a la pantalla quan fa aquesta estona que no se sent res.</summary>
    private static readonly TimeSpan ShowSilentAfter = TimeSpan.FromSeconds(3);

    private QueueStreamStatus? StreamStatusLocked(long now)
    {
        if (_hold is { } h)
        {
            string state;
            string? title = null, probe = null;
            if (h.Down)
            {
                state = h.Returning ? "returning" : "down";
                if (!h.Returning)
                    probe = h.Probe?.Reader is not ILiveVoiceReader { Connected: true } ? "trying"
                        : h.StableSince >= 0 && now - h.StableSince >= Frames(_settings.StreamStableFor) ? "ready" : "receiving";
            }
            else if (_current is { Reader: ILiveVoiceReader live } c && c.Item.Id == h.Stream.Id) (state, title) = LiveState(live);
            else state = "live";
            return new QueueStreamStatus(h.Stream, state, title, FramesToTime(Math.Max(0, now - h.StartFrame)),
                FramesToTime(Math.Max(0, h.MixFrame - now)), Reconnects: true, h.Stream.StreamReturnAfterSong, h.Cuts,
                h.Down ? FramesToTime(Math.Max(0, now - h.DownSince)) : null, probe);
        }
        if (_current is { Scheduled: true, Reader: ILiveVoiceReader reader } current)
        {
            var (state, title) = LiveState(reader);
            long start = current.EventStartFrame >= 0 ? current.EventStartFrame : current.StartFrame;
            return new QueueStreamStatus(current.Item, state, title, FramesToTime(Math.Max(0, now - start)),
                FramesToTime(Math.Max(0, current.MixFrame - now)), current.Item.StreamReconnect, current.Item.StreamReturnAfterSong,
                Cuts: 0, DownFor: null, Probe: null);
        }
        return null;
    }

    /// <summary>Un stream que sona: en directe, tallat (es reconnecta) o en silenci; i el títol que envia.</summary>
    private static (string State, string? Title) LiveState(ILiveVoiceReader reader) =>
        (!reader.Connected ? "cut" : reader.SilentFor >= ShowSilentAfter ? "silent" : "live", reader.Title);

    private Dictionary<Guid, string> UpcomingStreamStatesLocked()
    {
        var states = new Dictionary<Guid, string>();
        void Add(Playing? p)
        {
            if (p is { HasStarted: false, Reader: ILiveVoiceReader reader })
                states[p.Item.Id] = reader.State is ReaderState.Ready or ReaderState.Finished ? "ready" : "connecting";
        }
        Add(_next);
        foreach (var reserved in _reserved.Values) Add(reserved);
        return states;
    }

    /// <summary>Un resum de l'estat dels streams sense els temps, per saber quan ha canviat. Dins del bloqueig.</summary>
    private string StreamSignature(long now)
    {
        if (_hold is null && _reserved.Count == 0 && _next?.Reader is not ILiveVoiceReader && _current?.Reader is not ILiveVoiceReader)
            return "";
        var s = StreamStatusLocked(now);
        var upcoming = string.Join(",", UpcomingStreamStatesLocked().Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{s?.Item.Id}|{s?.State}|{s?.Probe}|{s?.Title}|{s?.Cuts}|{upcoming}";
    }

    /// <summary>«45 s», «7 min», «1 h 05 min».</summary>
    private static string Span(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))} s"
        : t.TotalMinutes < 60 ? $"{(int)Math.Round(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes:00} min";

    /// <summary>El text acabat en punt.</summary>
    private static string Sentence(string text) => text.TrimEnd() is var m && m.EndsWith('.') ? m : m + ".";

    /// <summary>Deixa anar la pista d'emergència que s'estava obrint (si n'hi ha). Dins del bloqueig.</summary>
    private void DropFiller()
    {
        if (_filler is null) return;
        Retire(_filler);
        _filler = null;
    }

    /// <summary>Reserva una veu i comença a llegir el fitxer. Retorna null si no hi ha cap veu lliure (no hauria de passar).</summary>
    private Playing? Open(QueueItem item)
    {
        var voice = _mixer.AcquireVoice();
        if (voice is null) return null;
        bool hold = HoldOpeningForTests?.Invoke(item.Path) == true;
        IVoiceReader reader = item.IsInput
            ? new InputVoiceReader(voice, () => OpenInput(item), _mixer.SampleRate, InputOptionsFor(item), () => _mixer.FramesRendered)
            : IsNetStream(item, out var url)
            ? new StreamVoiceReader(voice, url, _mixer.SampleRate, StreamOptionsFor(item), () => _mixer.FramesRendered)
            : new VoiceReader(voice, item.Path, _mixer.SampleRate, Ms(item.StartMs ?? 0), item.EndMs is { } end ? Ms(end) : null, hold);
        return new Playing(item, voice, reader) { OpenedFrame = _mixer.FramesRendered };
    }

    /// <summary>
    /// Obre l'entrada de línia d'un element de la cua (D-185): per l'identificador de Windows o, si no hi és, pel nom. Les
    /// proves hi posen una entrada falsa (<see cref="InputOpener"/>).
    /// </summary>
    private IAudioInput OpenInput(QueueItem item) =>
        InputOpener?.Invoke(item) ?? LineInputReader.OpenDevice(item.InputDeviceId, item.Title);

    /// <summary>Per a les proves: obre una entrada falsa en lloc de la de Windows (null = la de Windows).</summary>
    public Func<QueueItem, IAudioInput?>? InputOpener { get; set; }

    /// <summary>Com ha de sonar una entrada de la cua (D-185).</summary>
    private InputOptions InputOptionsFor(QueueItem item) => new()
    {
        Duration = Ms(item.EndMs ?? QueueItem.DefaultStreamMs),
        ConnectFor = _settings.StreamConnectFor,
    };

    /// <summary>Una font en directe que pot caure i tornar (D-031): un stream per internet o una entrada de línia (D-185).</summary>
    private static bool IsLive(QueueItem item) => item.IsInput || IsNetStream(item, out _);

    /// <summary>Un stream per internet (http o https): s'obre amb el lector de streams.</summary>
    private static bool IsNetStream(QueueItem item, out Uri url)
    {
        url = null!;
        return item.IsStream && Uri.TryCreate(item.Path, UriKind.Absolute, out url!) && url.Scheme is "http" or "https";
    }

    /// <summary>On havia de començar la pista que es prepara (el tall, el punt de mescla de la que sona o ara).</summary>
    private long DueFrame(long now) =>
        _cutFrame > 0 ? _cutFrame : _current is { Scheduled: true } playing ? Math.Min(playing.MixFrame, now) : now;

    /// <summary>
    /// Si la pista que es prepara ja hauria de sonar (D-179): és l'hora del tall o el punt de mescla de la que sona. Si no
    /// sona res i no hi ha cap tall, no (un stream té <see cref="QueueSettings.StreamConnectFor"/> per connectar-se).
    /// </summary>
    private bool StreamDue(long now) =>
        _cutFrame > 0 ? now >= _cutFrame : _current is { Scheduled: true } playing && now >= playing.MixFrame;

    /// <summary>Si encara falta per a l'hora en què ha de sonar (hi ha un tall programat o una pista que sona, i no hi hem arribat).</summary>
    private bool StreamHasTime(long now) => (_cutFrame > 0 || _current is { Scheduled: true }) && !StreamDue(now);

    /// <summary>Com ha de sonar un stream de la cua (D-030, D-175).</summary>
    private StreamOptions StreamOptionsFor(QueueItem item) => new()
    {
        Duration = Ms(item.EndMs ?? QueueItem.DefaultStreamMs),
        LiveEdge = item.StreamLiveEdge,
        Timeout = _settings.StreamTimeout,
        ConnectFor = _settings.StreamConnectFor,
    };

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
        if (p.Until is { } until) // el stream que torna (D-031): fins a l'hora prevista
        {
            p.MixFrame = until.Mix;
            p.EndFrame = until.End;
        }
        p.PlannedEndFrame = p.EndFrame;
        p.Voice.Start(start, fadeInFrames);
        // Si s'ha saltat més enllà del punt de mescla, el fos comença de seguida (i la següent entra en quant sigui a punt)
        p.Voice.Stop(Math.Max(p.MixFrame, start), (int)Math.Max(0, p.EndFrame - Math.Max(p.MixFrame, start)));
        p.Scheduled = true;
    }

    /// <summary>Deixa de preparar la següent (si encara no ha començat) i la torna al principi de la cua.</summary>
    private void UnprepareNext(List<Action> events)
    {
        DropFiller();
        if (_next is null || _next.HasStarted) return;
        if (_next.Returning && _hold is { } h)
        {
            // El stream que tornava (D-031) no va a la cua com una pista qualsevol: es torna a provar i tornarà quan toqui
            // (p. ex. l'operador ha mogut la cançó que sona: la mescla és en un altre punt)
            h.Returning = false;
            h.RetryAt = _mixer.FramesRendered;
        }
        else _upcoming.Insert(0, _next.Item);
        Retire(_next);
        _next = null;
    }

    /// <summary>
    /// Atura el lector d'una pista i n'allibera la veu. Si la veu ja sonava (casos rars, p. ex. treure una pista just
    /// quan començava), s'atura de cop i s'allibera quan el fil d'àudio la doni per acabada.
    /// </summary>
    private void Retire(Playing p)
    {
        if (p.Stuck)
        {
            // El fil de lectura pot estar encallat esperant la xarxa: no s'espera aquí (fil de control). Després de Cancel
            // ja no escriu res més a la veu.
            p.Reader.Cancel();
            _ = Task.Run(p.Reader.Dispose);
        }
        else p.Reader.Dispose();
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

    /// <summary>Proves de les pistes d'emergència (fase 5, pas 4): els fitxers d'aquestes rutes «tarden» a obrir-se.</summary>
    internal Func<string, bool>? HoldOpeningForTests { get; set; }

    /// <summary>Proves de les pistes d'emergència: la pista següent, que «tardava», ja s'obre.</summary>
    internal void ReleaseNextForTests()
    {
        lock (_sync) _next?.Reader.ResumeReading();
    }

    /// <summary>Proves de la pista encallada: el fitxer de la que sona deixa de llegir-se (o hi torna).</summary>
    internal void SetCurrentReadingForTests(bool reading)
    {
        lock (_sync)
        {
            if (reading) _current?.Reader.ResumeReading();
            else _current?.Reader.PauseReading();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_next is not null) Retire(_next);
            if (_current is not null) Retire(_current);
            DropFiller();
            if (_resume is not null) Retire(_resume);
            _resume = null;
            _paused = null;
            if (_cut?.First is { } cutFirst) Retire(cutFirst);
            if (_hold?.Probe is { } probe) Retire(probe);
            _hold = null;
            foreach (var r in _reserved.Values) Retire(r);
            _reserved.Clear();
            _cut = null;
            foreach (var e in _ending) Retire(e);
            _next = _current = null;
            _ending.Clear();
        }
    }

    /// <summary>Una pista amb la seva veu i el seu lector.</summary>
    private sealed class Playing(QueueItem item, MixerVoice voice, IVoiceReader reader)
    {
        public QueueItem Item { get; } = item;
        public MixerVoice Voice { get; } = voice;
        public IVoiceReader Reader { get; } = reader;
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

        /// <summary>Pista encallada (fase 5, pas 2): els fotogrames sonats a l'última comprovació i quan han canviat per última vegada.</summary>
        public long LastPlayed { get; set; } = -1;
        public long LastProgressFrame { get; set; }
        /// <summary>S'ha encallat (o la lectura ha fallat a mitja pista): ja s'ha avisat.</summary>
        public bool Stuck { get; set; }

        /// <summary>El fotograma en què es va començar a obrir (fase 5, pas 4: un fitxer que no s'obre mai es dona per perdut).</summary>
        public long OpenedFrame { get; init; }

        /// <summary>És el stream que torna després d'haver caigut (D-031); <see cref="ReturnNow"/>: de seguida, sense esperar la cançó.</summary>
        public bool Returning { get; set; }
        public bool ReturnNow { get; set; }

        /// <summary>Si no és null, el punt de mescla i el final fixos (el stream que torna acaba a l'hora prevista del principi).</summary>
        public (long Mix, long End)? Until { get; set; }

        /// <summary>D'un stream que torna (D-031), on començava l'esdeveniment (per al temps que es mostra); si no, -1.</summary>
        public long EventStartFrame { get; set; } = -1;
    }

    /// <summary>
    /// Un stream amb reconnexió que ha caigut (D-031, D-180): fins a <see cref="MixFrame"/> (on comença la resta, com si
    /// hagués sonat sencer) sona música de farciment i, en segon pla, es prova de connectar-s'hi (<see cref="Probe"/>).
    /// </summary>
    private sealed class StreamHold(QueueItem stream, long startFrame, long mixFrame, long endFrame)
    {
        public QueueItem Stream { get; } = stream;
        /// <summary>On començava (o havia de començar) el stream: per al temps que es mostra (fase 2, pas 7e).</summary>
        public long StartFrame { get; } = startFrame;
        public long MixFrame { get; } = mixFrame;
        public long EndFrame { get; } = endFrame;
        /// <summary>Les pistes de farciment (per treure les que no han sonat quan el stream torna o s'acaba).</summary>
        public HashSet<Guid> Fill { get; } = [];
        /// <summary>Ara no sona (ha caigut); <see cref="Returning"/>: torna, ja és la següent.</summary>
        public bool Down { get; set; } = true;
        public bool Returning { get; set; }
        public long DownSince { get; set; }
        public long DownFrames { get; set; }
        public int Cuts { get; set; }
        /// <summary>La connexió de prova (una veu que encara no sona), o null.</summary>
        public Playing? Probe { get; set; }
        public long RetryAt { get; set; }
        /// <summary>Des de quan la connexió de prova rep àudio seguit (-1 = encara no).</summary>
        public long StableSince { get; set; } = -1;
        /// <summary>Des de quan cal una pista de farciment i encara no n'hi ha cap (-1 = no cal o ja n'hi ha).</summary>
        public long NoFillSince { get; set; } = -1;
        /// <summary>L'últim que s'ha anotat al registre de la connexió de prova (per no repetir-ho).</summary>
        public string? ProbeStatus { get; set; }
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
