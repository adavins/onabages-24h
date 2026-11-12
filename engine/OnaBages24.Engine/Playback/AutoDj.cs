using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>Ajustos de l'AutoDJ.</summary>
public sealed record AutoDjSettings
{
    /// <summary>Quantes pistes, com a mínim, ha d'haver-hi a la cua per sonar (comptant les manuals).</summary>
    public int MinUpcoming { get; init; } = 2;

    /// <summary>
    /// Quanta música, com a mínim, ha d'haver-hi a la cua per davant (D-097): el motor hi posa 30 minuts, perquè es vegi què
    /// vindrà i la previsió fins al pròxim esdeveniment sigui útil. Zero (per defecte, i a les proves) = només <see cref="MinUpcoming"/>.
    /// </summary>
    public TimeSpan FillAhead { get; init; } = TimeSpan.Zero;

    /// <summary>Com a molt, tantes pistes per davant (per si n'hi hagués de molt curtes).</summary>
    public int MaxUpcoming { get; init; } = 20;

    /// <summary>Si no ha trobat cap pista, quant espera a tornar-ho a provar (per no consultar la base de dades cada 10 ms).</summary>
    public TimeSpan RetryAfterNothing { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Quant temps es queda aturat l'AutoDJ si no es diu res (D-073): després es torna a activar sol.</summary>
    public TimeSpan DefaultPause { get; init; } = TimeSpan.FromHours(2);

    /// <summary>El màxim que es pot aturar d'una vegada (mai indefinidament, D-073).</summary>
    public TimeSpan MaxPause { get; init; } = TimeSpan.FromHours(12);
}

/// <summary>
/// Reserva de l'AutoDJ (fase 3, pas 8d; D-099): si cap pas de la rotació no troba res, la rotació de reserva
/// (<see cref="RotationId"/>, opcional) i, si tampoc, música de tota la biblioteca sense les subcategories «no de reserva»
/// (<see cref="ExcludedSubcategoryIds"/>, p. ex. Nadal).
/// </summary>
public sealed record ReserveSettings(long? RotationId, IReadOnlySet<long> ExcludedSubcategoryIds)
{
    /// <summary>La de la primera vegada: sense rotació de reserva i sense subcategories excloses (com fins al pas 8c).</summary>
    public static ReserveSettings None { get; } = new(null, new HashSet<long>());
}

/// <summary>
/// Separació real (fase 3, pas 8b; D-095): quant ha de passar, com a mínim, perquè torni a sonar la mateixa cançó o el
/// mateix artista (només als passos amb criteris de repetició). Zero = sense límit de temps (només el que és a la cua).
/// </summary>
public sealed record SeparationRules(TimeSpan Track, TimeSpan Artist)
{
    /// <summary>La de la primera vegada (decisió del desenvolupador): cançó 6 hores, artista 1 hora.</summary>
    public static SeparationRules Default { get; } = new(TimeSpan.FromHours(6), TimeSpan.FromHours(1));

    /// <summary>Com a molt, una setmana (més no té sentit i podria deixar una bossa sense res).</summary>
    public static readonly TimeSpan Max = TimeSpan.FromDays(7);
}

/// <summary>
/// AutoDJ (fase 1, pas 8; D-014): quan a la cua hi ha menys de <see cref="AutoDjSettings.MinUpcoming"/> pistes, hi afegeix
/// la del pas següent de la rotació. Cada pas tria una pista de la seva subcategoria (o categoria), amb l'etiqueta si en té:
/// - «la menys reproduïda recentment»: la que fa més temps que no sona (si n'hi ha diverses que no han sonat mai, a l'atzar);
/// - «la menys reproduïda»: la que ha sonat menys vegades (si n'hi ha diverses, a l'atzar);
/// - «aleatòria»: qualsevol.
/// Criteris de repetició (fase 3, pas 8b; D-095): mai una pista que ja és a la cua o sona; si el pas ho demana, tampoc una
/// cançó que ha sonat fa menys de <see cref="Separation"/>.Track ni un artista que és a la cua o ha sonat fa menys de
/// <see cref="Separation"/>.Artist. Si així no en queda cap, s'afluixen per ordre, com RadioDJ, que «reinicia» la subcategoria.
/// Si un pas no troba res, se salta (amb un avís); si cap pas no troba res, sona música de tota la biblioteca. Mai silenci.
///
/// Treballa al fil de control (<see cref="Tick"/>), com el reproductor de la cua.
/// </summary>
public sealed class AutoDj
{
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue;
    private readonly AutoDjSettings _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Random _random;
    private readonly object _sync = new();
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Passos de la rotació activa que l'última vegada no han trobat cap pista (s'han saltat). L'avís es manté mentre n'hi
    /// hagi, no només quan l'última tria ha saltat un pas; un pas en surt quan torna a trobar una pista.
    /// </summary>
    private readonly SortedSet<int> _emptySteps = [];

    /// <summary>El pas següent de la rotació de reserva (continua on era cada vegada que s'hi ha de recórrer).</summary>
    private int _reserveNext;

    private ReserveSettings _reserve = ReserveSettings.None;

    /// <summary>
    /// La reserva (fase 3, pas 8d; D-099): què sona si cap pas de la rotació no troba res. Si es canvia i a la cua hi ha
    /// pistes que en surten, es tornen a triar (D-097).
    /// </summary>
    public ReserveSettings Reserve
    {
        get => _reserve;
        set
        {
            if (value.RotationId == _reserve.RotationId && value.ExcludedSubcategoryIds.SetEquals(_reserve.ExcludedSubcategoryIds)) return;
            if (value.RotationId != _reserve.RotationId) _reserveNext = 0;
            _reserve = value;
            if (_queue.Upcoming.Any(i => i.Origin == QueueOrigin.AutoDj && i.RotationStep is null)) RefreshQueue();
        }
    }

    /// <param name="clock">Rellotge (les proves en fan servir un de fals). Per defecte, l'hora de l'ordinador.</param>
    /// <param name="random">Generador d'atzar (les proves hi posen una llavor fixa).</param>
    public AutoDj(Library library, Rotations rotations, QueuePlayer queue, AutoDjSettings? settings = null,
        Func<DateTimeOffset>? clock = null, Random? random = null)
    {
        _library = library;
        _rotations = rotations;
        _queue = queue;
        _settings = settings ?? new AutoDjSettings();
        _clock = clock ?? (() => DateTimeOffset.Now);
        _random = random ?? Random.Shared;
    }

    /// <summary>L'AutoDJ afegeix pistes a la cua.</summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Separació real (D-095). Es pot canviar en qualsevol moment: les pistes de l'AutoDJ que encara no sonen es tornen a
    /// triar amb la nova (D-097).
    /// </summary>
    public SeparationRules Separation
    {
        get => _separation;
        set
        {
            if (value == _separation) return;
            _separation = value;
            RefreshQueue();
        }
    }

    private SeparationRules _separation = SeparationRules.Default;

    /// <summary>La rotació activa (null si no n'hi ha cap: llavors sona música de tota la biblioteca).</summary>
    public Rotation? Rotation { get; private set; }

    /// <summary>Índex del pas que es farà servir la pròxima vegada.</summary>
    public int NextStep { get; private set; }

    /// <summary>L'últim avís (en català), o null si tot va bé.</summary>
    public string? Problem { get; private set; }

    /// <summary>Fins quan està aturat (D-073): llavors es torna a activar sol. Null si està actiu.</summary>
    public DateTimeOffset? DisabledUntil { get; private set; }

    /// <summary>Avís de l'última vegada que s'ha tornat a activar sol (es manté fins al pròxim canvi a mà), o null.</summary>
    public string? Notice { get; private set; }

    /// <summary>Qui ha posat la rotació activa («a mà», «esdeveniment «Converses»», «en arrencar el motor») i quan.</summary>
    public string? RotationChangedBy { get; private set; }

    public DateTimeOffset? RotationChangedAt { get; private set; }

    /// <summary>
    /// Categoria de sweepers activa (fase 3, pas 5): la subcategoria d'on surten els sweepers dels passos amb trepitjador
    /// (com el desplegable de RadioDJ que canvia l'acció «Set Sweeper Category»). Null si no n'hi ha cap.
    /// </summary>
    public long? SweepersSubcategoryId { get; private set; }

    /// <summary>«Categoria / Subcategoria» de la categoria de sweepers activa (per mostrar-la).</summary>
    public string? SweepersName { get; private set; }

    /// <summary>Qui ha posat la categoria de sweepers activa i quan (com <see cref="RotationChangedBy"/>).</summary>
    public string? SweepersChangedBy { get; private set; }

    public DateTimeOffset? SweepersChangedAt { get; private set; }

    /// <summary>Ha canviat l'estat, la rotació o l'avís (no cada vegada que s'afegeix una pista: per això hi ha <c>queue.changed</c>).</summary>
    public event Action? Changed;

    /// <summary>Ha canviat la categoria de sweepers (per desar-la: es manté si el motor es reinicia).</summary>
    public event Action<long?>? SweepersChanged;

    /// <summary>
    /// Canvia la categoria de sweepers activa (fase 3, pas 5): a mà, amb l'acció SWEEPERS d'un esdeveniment o en arrencar.
    /// Les pistes que ja són a la cua es queden amb el sweeper que tenien. Retorna false, sense canviar res, si la
    /// subcategoria no existeix.
    /// </summary>
    /// <param name="subcategoryId">La subcategoria, o null perquè no n'hi hagi cap (els passos amb trepitjador sonen sense).</param>
    /// <param name="by">Qui fa el canvi («a mà», «esdeveniment «Dance90»», «en arrencar el motor»).</param>
    public bool SetSweepers(long? subcategoryId, string? by)
    {
        string? name = null;
        if (subcategoryId is { } id && (name = _library.SubcategoryName(id)) is null) return false;
        lock (_sync)
        {
            if (subcategoryId != SweepersSubcategoryId)
            {
                SweepersChangedBy = by;
                SweepersChangedAt = _clock();
            }
            SweepersSubcategoryId = subcategoryId;
            SweepersName = name;
        }
        SweepersChanged?.Invoke(subcategoryId);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Ha canviat alguna bossa amb nom (es crida des de <see cref="BagsEdited"/>): per avisar les interfícies.</summary>
    public event Action? BagsChanged;

    /// <summary>
    /// Canvia la subcategoria d'una bossa amb nom (fase 3, pas 8c; D-096): a mà o amb l'acció BOSSA del planificador. Els
    /// passos que hi apunten triaran d'aquesta subcategoria a partir de la pròxima cançó (el que ja és a la cua no canvia).
    /// Retorna false, sense canviar res, si la bossa o la subcategoria no existeixen.
    /// </summary>
    /// <param name="subcategoryId">La subcategoria, o null perquè la bossa no en tingui cap (els seus passos se salten).</param>
    public bool SetBag(long bagId, long? subcategoryId, string? by)
    {
        if (subcategoryId is { } sub && _library.SubcategoryName(sub) is null) return false;
        if (_rotations.GetBag(bagId) is null) return false;
        _rotations.SetBagSubcategory(bagId, subcategoryId, by, _clock());
        BagsEdited();
        return true;
    }

    /// <summary>S'han canviat les bosses (o els seus noms): es torna a llegir la rotació activa i s'avisa.</summary>
    public void BagsEdited()
    {
        RotationsEdited();
        BagsChanged?.Invoke();
    }

    /// <summary>
    /// S'han editat les rotacions (editor, fase 3, pas 8a): si la rotació activa ha canviat, es torna a llegir (amb els passos
    /// nous). El pas següent es manté si encara existeix; si no, torna al primer. Les pistes que ja són a la cua no canvien.
    /// </summary>
    public void RotationsEdited()
    {
        if (Rotation is not { } current) return;
        var fresh = _rotations.Get(current.Id);
        bool stepsChanged;
        lock (_sync)
        {
            if (fresh is null) return; // l'activa no es pot esborrar (ho comprova qui l'esborra)
            stepsChanged = StepsKey(current) != StepsKey(fresh);
            if (stepsChanged) _emptySteps.Clear(); // els passos poden haver canviat de lloc: es tornen a comprovar
            Rotation = fresh;
            if (NextStep >= fresh.Steps.Count) NextStep = 0;
        }
        // Han canviat els passos de l'activa (o la subcategoria d'una de les seves bosses): la cua es torna a triar (D-097)
        if (stepsChanged) RefreshQueue();
        Changed?.Invoke();
    }

    /// <summary>
    /// Activa o desactiva l'AutoDJ i tria la rotació. Sense <paramref name="rotationId"/>, es fa servir la primera rotació
    /// (la de l'id més baix). Retorna false, sense canviar res, si la rotació demanada no existeix.
    /// </summary>
    /// <param name="by">Qui fa el canvi, per mostrar d'on ve la rotació (p. ex. «a mà» o «esdeveniment «Converses»»).</param>
    /// <param name="pause">Si s'atura: quant temps (per defecte <see cref="AutoDjSettings.DefaultPause"/>, com a molt
    /// <see cref="AutoDjSettings.MaxPause"/>). Després es torna a activar sol (D-073).</param>
    public bool Configure(bool enabled, long? rotationId, string? by = null, TimeSpan? pause = null)
    {
        var all = _rotations.List();
        Rotation? rotation = rotationId is { } id ? all.FirstOrDefault(r => r.Id == id) : all.FirstOrDefault();
        if (rotationId is not null && rotation is null) return false;
        bool rotationChanged;
        lock (_sync)
        {
            rotationChanged = rotation?.Id != Rotation?.Id;
            var now = _clock();
            if (enabled) DisabledUntil = null;
            else if (Enabled || pause is not null || DisabledUntil is null)
            {
                var length = pause ?? _settings.DefaultPause;
                if (length > _settings.MaxPause) length = _settings.MaxPause;
                if (length < TimeSpan.FromMinutes(1)) length = TimeSpan.FromMinutes(1);
                DisabledUntil = now + length;
            }
            Enabled = enabled;
            if (rotation?.Id != Rotation?.Id)
            {
                NextStep = 0;
                _emptySteps.Clear();
                RotationChangedBy = by;
                RotationChangedAt = now;
            }
            Rotation = rotation;
            Problem = null;
            Notice = null;
            _retryAt = DateTimeOffset.MinValue;
        }
        // Rotació nova: les pistes de l'anterior que encara no sonen es tornen a triar amb aquesta (D-097)
        if (rotationChanged) RefreshQueue(rewind: false);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// «Regenera la cua» (D-034): treu de la cua les pistes que hi ha posat l'AutoDJ (no la que sona, ni les manuals, ni
    /// les dels esdeveniments) i la torna a omplir amb la rotació actual. La rotació continua on era: pel pas de la primera
    /// pista que s'ha tret. Retorna quantes s'han tret.
    /// </summary>
    public int Regenerate()
    {
        var removed = _queue.TakeWhere(i => i.Origin == QueueOrigin.AutoDj);
        lock (_sync)
        {
            RewindTo(removed);
            _retryAt = DateTimeOffset.MinValue;
        }
        Changed?.Invoke();
        return removed.Count;
    }

    /// <summary>
    /// Torna el pas de la rotació al de la primera pista treta que en venia (de la rotació activa), perquè es torni a triar
    /// des d'allà. No es compta quantes s'han tret: un pas que s'ha saltat (sense pistes) també fa avançar la rotació.
    /// </summary>
    private void RewindTo(IReadOnlyList<QueueItem> removed)
    {
        if (Rotation is not { Steps.Count: > 0 } rotation) return;
        var first = removed.FirstOrDefault(i => i.RotationId == rotation.Id && i.RotationStep is not null);
        if (first?.RotationStep is { } step) NextStep = step < rotation.Steps.Count ? step : 0;
    }

    /// <summary>Fil de control: si a la cua hi falten pistes, n'hi afegeix una. Barata quan no cal fer res.</summary>
    public void Tick()
    {
        bool changed = false;
        lock (_sync)
        {
            var now = _clock();

            // Aturat (D-073): es torna a activar sol quan s'acaba el temps, o abans si a la cua ja no queda res (mai silenci)
            if (!Enabled)
            {
                if (DisabledUntil is { } until && now >= until)
                    Notice = $"L'AutoDJ s'ha tornat a activar sol: s'havia aturat fins a les {until.LocalDateTime:HH:mm}.";
                else if (_queue.Upcoming.Count == 0)
                    Notice = "L'AutoDJ estava aturat i a la cua ja no hi quedava res: s'ha tornat a activar sol perquè no hi hagi silenci.";
                else
                    return;
                Enabled = true;
                DisabledUntil = null;
                changed = true;
            }

            // Si a la cua hi falten pistes (o música per davant, D-097), n'afegeix una
            if (NeedsMore() && now >= _retryAt)
            {
                var item = PickNext(DateOnly.FromDateTime(now.LocalDateTime), out string? problem);
                if (item is null) _retryAt = now + _settings.RetryAfterNothing;
                else _queue.Enqueue(item);
                if (problem != Problem)
                {
                    Problem = problem;
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// A la cua hi falten pistes: menys de <see cref="AutoDjSettings.MinUpcoming"/>, o menys de <see cref="AutoDjSettings.FillAhead"/>
    /// de música per davant (sense passar de <see cref="AutoDjSettings.MaxUpcoming"/>). Una pista sense durada coneguda compta 3 minuts.
    /// </summary>
    private bool NeedsMore()
    {
        var upcoming = _queue.Upcoming;
        if (upcoming.Count < _settings.MinUpcoming) return true;
        if (_settings.FillAhead <= TimeSpan.Zero || upcoming.Count >= _settings.MaxUpcoming) return false;
        long aheadMs = upcoming.Sum(i => i.PlannedMs ?? 180_000);
        return aheadMs < _settings.FillAhead.TotalMilliseconds;
    }

    /// <summary>
    /// Ha canviat el que tria l'AutoDJ (la rotació, els seus passos, una bossa o la separació; D-097): les pistes que hi ha
    /// posat i encara no sonen es tornen a triar, com «Regenera la cua», perquè el canvi es noti de seguida. No es toca la que
    /// ja s'està preparant per sonar (uns 20 s abans de la mescla: obrir-ne una altra en aquell moment podria no ser a temps),
    /// ni les manuals ni les dels esdeveniments.
    /// </summary>
    /// <param name="rewind">La rotació torna al pas de la primera pista treta (la mateixa rotació continua on era).
    /// Amb una rotació nova, no: ja comença pel primer pas.</param>
    private void RefreshQueue(bool rewind = true)
    {
        if (_queue.Upcoming.Count == 0) return;
        var preparing = _queue.NextInPreparation;
        var removed = _queue.TakeWhere(i => i.Origin == QueueOrigin.AutoDj && i.Id != preparing);
        lock (_sync)
        {
            if (rewind) RewindTo(removed);
            _retryAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>Una «empremta» dels passos d'una rotació, per saber si han canviat de debò (també la subcategoria de les bosses).</summary>
    private static string StepsKey(Rotation? rotation) => rotation is null ? "" :
        string.Join("|", rotation.Steps.Select(s => $"{s.CategoryId}/{s.SubcategoryId}/{s.TagId}/{s.BagId}/{s.Selection}/{s.RepeatRule}/{s.Sweeper}"));

    /// <summary>Tria la pista següent i avança el pas de la rotació. Retorna null si no hi ha res que pugui sonar.</summary>
    private QueueItem? PickNext(DateOnly today, out string? problem)
    {
        var inQueue = new List<QueueItem>(_queue.Upcoming);
        if (_queue.Current is { } current) inQueue.Add(current);
        var context = NewContext(inQueue, _clock());
        int next = NextStep;
        var pick = Pick(Rotation, ref next, ref _reserveNext, today, context, _random);
        NextStep = next;

        // Passos buits: els que s'han saltat ara s'hi afegeixen i el que ha trobat pista en surt (l'avís no va i ve)
        if (pick.StepIndex >= 0) _emptySteps.Remove(pick.StepIndex);
        foreach (int index in pick.SkippedSteps) _emptySteps.Add(index);
        problem = pick.Problem;
        if (!pick.Fallback && Rotation is { } active && _emptySteps.Count > 0)
            problem = $"Rotació «{active.Name}»: sense pistes disponibles a " +
                string.Join(", ", _emptySteps.Where(i => i < active.Steps.Count).Select(i => $"«{active.Steps[i]}»").Distinct()) +
                "; se salten";
        if (pick.Track is null) return null;

        var item = QueueItem.FromTrack(pick.Track, QueueOrigin.AutoDj);
        if (pick.StepIndex >= 0 && Rotation is { } rotation)
            item = item with { RotationId = rotation.Id, RotationStep = pick.StepIndex, RotationStepName = pick.Step?.ToString() };
        else if (pick.Source is not null)
            item = item with { RotationStepName = pick.Source }; // de la reserva: sense pas de la rotació activa
        if (pick.Step?.Sweeper == true)
        {
            // Trepitjador (fase 3, pas 5): si no n'hi ha cap, la pista sona igual, sense sweeper, i es diu
            item = item with { Sweeper = PickSweeper(today, inQueue, out string? sweeperProblem) };
            if (sweeperProblem is not null) problem = problem is null ? sweeperProblem : $"{problem}. {sweeperProblem}";
        }
        return item;
    }

    /// <summary>Una tria: la pista (o null), el pas d'on surt, la regla de repetició que s'ha hagut d'afluixar, l'avís i si és de la reserva.</summary>
    private sealed record PickResult(Track? Track, RotationStep? Step, int StepIndex, string? Relaxed, string? Problem, bool Fallback)
    {
        /// <summary>Els passos (índexs) de la rotació activa que s'han saltat perquè no tenien pistes.</summary>
        public IReadOnlyList<int> SkippedSteps { get; init; } = [];

        /// <summary>D'on surt, si és de la reserva (per mostrar-ho a la cua), p. ex. «Reserva: tota la biblioteca».</summary>
        public string? Source { get; init; }
    }

    /// <summary>El que ja és a la cua (o ja s'ha triat, a la previsualització) i el que ha sonat fa poc: per als criteris de repetició.</summary>
    private sealed class PickContext
    {
        public required HashSet<long> QueuedIds { get; init; }
        public required HashSet<string> QueuedArtists { get; init; }
        public required HashSet<string> RecentArtists { get; init; }
        public required DateTimeOffset Now { get; init; }

        /// <summary>Una pista triada passa a ser «a la cua» (per a la tria següent).</summary>
        public void Add(Track track)
        {
            QueuedIds.Add(track.Id);
            if (ArtistKey(track) is { Length: > 0 } a) QueuedArtists.Add(a);
        }
    }

    private static string ArtistKey(Track track) => track.Artist.Trim();

    private PickContext NewContext(IEnumerable<QueueItem> inQueue, DateTimeOffset now)
    {
        var items = inQueue.ToList();
        // Els artistes que han sonat dins de la separació d'artista (historial)
        var recent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Separation.Artist > TimeSpan.Zero)
        {
            try
            {
                foreach (var entry in _library.History(now - Separation.Artist, now + TimeSpan.FromMinutes(1)))
                    if (entry.Artist.Trim() is { Length: > 0 } a) recent.Add(a);
            }
            catch (DatabaseException) { /* sense historial, només la cua */ }
        }
        return new PickContext
        {
            QueuedIds = items.Where(i => i.TrackId is not null).Select(i => i.TrackId!.Value).ToHashSet(),
            QueuedArtists = items.Select(i => i.Artist.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
            RecentArtists = recent,
            Now = now,
        };
    }

    /// <summary>
    /// Tria una pista a partir del pas <paramref name="next"/> de la rotació (que avança). Si un pas no troba res, se salta.
    /// Si cap pas no troba res (fase 3, pas 8d; D-099): la rotació de reserva (a partir del seu pas
    /// <paramref name="reserveNext"/>, que també avança) i, si tampoc, música de tota la biblioteca sense les subcategories
    /// «no de reserva»; sempre amb avís. Mai silenci. La fan servir l'AutoDJ i la previsualització.
    /// </summary>
    private PickResult Pick(Rotation? rotation, ref int next, ref int reserveNext, DateOnly today, PickContext context, Random random)
    {
        // 1. La rotació activa
        var skippedSteps = new List<int>();
        if (TryRotation(rotation, ref next, today, context, random, skippedSteps) is { } found)
        {
            string? problem = skippedSteps.Count > 0
                ? $"Rotació «{rotation!.Name}»: sense pistes disponibles a {string.Join(", ", skippedSteps.Select(i => $"«{rotation.Steps[i]}»"))}; s'ha saltat"
                : null;
            return new PickResult(found.Track, found.Step, found.Index, found.Relaxed, problem, false) { SkippedSteps = skippedSteps };
        }
        string reason = rotation is null ? "No hi ha cap rotació"
            : $"Cap pas de la rotació «{rotation.Name}» no té pistes disponibles " +
              $"({string.Join(", ", skippedSteps.Select(i => $"«{rotation.Steps[i]}»").Distinct())})";

        // 2. La rotació de reserva (si n'hi ha una i no és la mateixa)
        var settings = Reserve;
        if (settings.RotationId is { } reserveId && reserveId != rotation?.Id && _rotations.Get(reserveId) is { } reserve)
        {
            if (TryRotation(reserve, ref reserveNext, today, context, random, []) is { } r)
                return new PickResult(r.Track, r.Step, -1, r.Relaxed, $"{reason}: sona la rotació de reserva «{reserve.Name}».", true)
                {
                    SkippedSteps = skippedSteps,
                    Source = $"Reserva: rotació «{reserve.Name}», pas {r.Index + 1} ({r.Step})",
                };
            reason += $", ni la rotació de reserva «{reserve.Name}»";
        }

        // 3. Música de tota la biblioteca, sense les subcategories «no de reserva» (p. ex. Nadal)
        var all = _library.AvailableMusic(today);
        var allowed = settings.ExcludedSubcategoryIds.Count == 0 ? all
            : all.Where(t => t.SubcategoryId is not { } s || !settings.ExcludedSubcategoryIds.Contains(s)).ToList();
        string what = allowed.Count < all.Count ? "música de tota la biblioteca (sense les subcategories «no de reserva»)" : "música de tota la biblioteca";
        if (allowed.Count == 0 && all.Count > 0)
        {
            allowed = all; // mai silenci: si només en queda de les «no de reserva», també
            what = "música de les subcategories «no de reserva» (no n'hi ha cap altra)";
        }
        var (fallback, fallbackRelaxed) = Choose(allowed, RotationSelection.LeastRecent, true, context, random);
        if (fallback is null)
            return new PickResult(null, null, -1, null, $"{reason} i no hi ha cap música disponible a la biblioteca: l'AutoDJ no pot afegir res.", true)
                { SkippedSteps = skippedSteps };
        return new PickResult(fallback, null, -1, fallbackRelaxed, $"{reason}: sona {what}.", true)
            { SkippedSteps = skippedSteps, Source = "Reserva: tota la biblioteca" };
    }

    /// <summary>Una pista d'un pas d'una rotació: la pista, el pas, el seu índex i la regla afluixada.</summary>
    private sealed record StepPick(Track Track, RotationStep Step, int Index, string? Relaxed);

    /// <summary>
    /// Prova els passos d'una rotació a partir de <paramref name="next"/> (que avança) fins que un troba pista. Els que no en
    /// troben s'anoten a <paramref name="skipped"/>. Null si no en troba cap (o no hi ha rotació).
    /// </summary>
    private StepPick? TryRotation(Rotation? rotation, ref int next, DateOnly today, PickContext context, Random random, List<int> skipped)
    {
        if (rotation is not { Steps.Count: > 0 }) return null;
        for (int attempt = 0; attempt < rotation.Steps.Count; attempt++)
        {
            int index = next % rotation.Steps.Count;
            var step = rotation.Steps[index];
            next = (index + 1) % rotation.Steps.Count;
            var (track, relaxed) = Choose(_library.TracksForRotationStep(step, today), step.Selection, step.RepeatRule, context, random);
            if (track is not null) return new StepPick(track, step, index, relaxed);
            skipped.Add(index);
        }
        return null;
    }

    /// <summary>
    /// El sweeper per a un pas amb trepitjador: de la categoria de sweepers activa, el que fa més temps que no sona, i
    /// que no sigui el d'una altra pista de la cua. Null (amb l'avís a <paramref name="problem"/>) si no n'hi ha cap.
    /// </summary>
    private QueueItem? PickSweeper(DateOnly today, List<QueueItem> inQueue, out string? problem)
    {
        problem = null;
        if (SweepersSubcategoryId is not { } id)
        {
            problem = "Un pas de la rotació demana un sweeper a l'inici de la pista, però no hi ha cap categoria de sweepers triada: sona sense";
            return null;
        }
        var step = new RotationStep { SubcategoryId = id, Selection = RotationSelection.LeastRecent, RepeatRule = false };
        var context = new PickContext
        {
            QueuedIds = inQueue.Select(i => i.Sweeper?.TrackId).OfType<long>().ToHashSet(),
            QueuedArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            RecentArtists = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Now = _clock(),
        };
        var (track, _) = Choose(_library.TracksForRotationStep(step, today), RotationSelection.LeastRecent, false, context, _random);
        if (track is null)
        {
            problem = $"No hi ha cap sweeper disponible a «{SweepersName}»: la pista sona sense";
            return null;
        }
        return QueueItem.FromTrack(track, QueueOrigin.AutoDj);
    }

    /// <summary>
    /// Tria una pista d'entre les candidates. Amb els criteris de repetició (fase 3, pas 8b; D-095): no una pista que ja és a
    /// la cua; no una que hagi sonat fa menys de <see cref="SeparationRules.Track"/>; no un artista que és a la cua o que ha
    /// sonat fa menys de <see cref="SeparationRules.Artist"/>. Si així no en queda cap, s'afluixen per ordre (la separació
    /// d'artista, la de cançó, l'artista de la cua i, al final, la pista de la cua): mai silenci. Retorna quina s'ha afluixat.
    /// Sense criteris de repetició (sweepers, jingles), només s'evita el que ja és a la cua.
    /// </summary>
    private (Track? Track, string? Relaxed) Choose(IReadOnlyList<Track> candidates, RotationSelection selection, bool repeatRule,
        PickContext context, Random random)
    {
        if (candidates.Count == 0) return (null, null);

        var notQueued = candidates.Where(t => !context.QueuedIds.Contains(t.Id)).ToList();
        List<Track> pool;
        string? relaxed = null;
        if (!repeatRule)
        {
            pool = notQueued.Count > 0 ? notQueued : candidates.ToList();
        }
        else
        {
            var separation = Separation;
            var trackCut = context.Now - separation.Track;
            bool TrackRested(Track t) => separation.Track <= TimeSpan.Zero || t.LastPlayed is not { } last || last <= trackCut;
            bool ArtistNotQueued(Track t) => ArtistKey(t) is not { Length: > 0 } a || !context.QueuedArtists.Contains(a);
            bool ArtistRested(Track t) => ArtistNotQueued(t) && (ArtistKey(t) is not { Length: > 0 } a || !context.RecentArtists.Contains(a));

            (Func<Track, bool> Test, string? Relaxed)[] stages =
            [
                (t => TrackRested(t) && ArtistRested(t), null),
                (t => TrackRested(t) && ArtistNotQueued(t), "separació d'artista"),
                (ArtistNotQueued, "separació de cançó"),
                (_ => true, "artista que ja és a la cua"),
            ];
            pool = [];
            foreach (var (test, stageRelaxed) in stages)
            {
                pool = notQueued.Where(test).ToList();
                if (pool.Count > 0)
                {
                    relaxed = stageRelaxed;
                    break;
                }
            }
            if (pool.Count == 0)
            {
                pool = candidates.ToList(); // totes ja són a la cua
                relaxed = "cançó que ja és a la cua";
            }
        }

        // Les que encara no tenen punts d'encadenament (s'estan calculant) només si no n'hi ha d'altres (D-026)
        var withCues = pool.Where(t => t.MixMs is not null).ToList();
        if (withCues.Count > 0) pool = withCues;

        if (selection == RotationSelection.LeastRecent)
        {
            var oldest = pool.Min(t => t.LastPlayed ?? DateTimeOffset.MinValue);
            pool = pool.Where(t => (t.LastPlayed ?? DateTimeOffset.MinValue) == oldest).ToList();
        }
        else if (selection == RotationSelection.LeastPlayed)
        {
            long fewest = pool.Min(t => t.PlayCount);
            pool = pool.Where(t => t.PlayCount == fewest).ToList();
        }
        return (pool[random.Next(pool.Count)], relaxed);
    }

    // --- Previsualització (fase 3, pas 8b; D-095) ---

    /// <summary>Una pista de la previsualització: el pas (null si és de la reserva), la pista i què s'ha hagut d'afluixar.</summary>
    public sealed record PreviewItem(int StepIndex, RotationStep? Step, Track? Track, string? Relaxed, string? Problem, bool Fallback)
    {
        /// <summary>Si és de la reserva, d'on surt (p. ex. «Reserva: rotació «Dance», pas 2 (…)»).</summary>
        public string? Source { get; init; }
    }

    /// <summary>
    /// Les properes <paramref name="count"/> pistes que triaria una rotació, <b>sense canviar res</b> (ni la cua ni el pas de
    /// la rotació): amb la mateixa tria que l'AutoDJ, comptant el que ja és a la cua i el que ha sonat fa poc. Si és la
    /// rotació activa, comença pel pas següent; si no, pel primer. Amb «a l'atzar», és una de les possibilitats.
    /// </summary>
    public IReadOnlyList<PreviewItem> Preview(Rotation rotation, int count)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        List<QueueItem> inQueue;
        int next, reserveNext;
        lock (_sync)
        {
            inQueue = new List<QueueItem>(_queue.Upcoming);
            if (_queue.Current is { } current) inQueue.Add(current);
            next = Rotation?.Id == rotation.Id ? NextStep : 0;
            reserveNext = _reserveNext;
        }
        var context = NewContext(inQueue, now);
        var random = new Random();
        var list = new List<PreviewItem>();
        for (int i = 0; i < count; i++)
        {
            var pick = Pick(rotation, ref next, ref reserveNext, today, context, random);
            list.Add(new PreviewItem(pick.StepIndex, pick.Step, pick.Track, pick.Relaxed, pick.Problem, pick.Fallback) { Source = pick.Source });
            if (pick.Track is null) break;
            context.Add(pick.Track);
        }
        return list;
    }
}
