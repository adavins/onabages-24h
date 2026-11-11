using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>Ajustos de l'AutoDJ.</summary>
public sealed record AutoDjSettings
{
    /// <summary>Quantes pistes, com a mínim, ha d'haver-hi a la cua per sonar (comptant les manuals).</summary>
    public int MinUpcoming { get; init; } = 2;

    /// <summary>Si no ha trobat cap pista, quant espera a tornar-ho a provar (per no consultar la base de dades cada 10 ms).</summary>
    public TimeSpan RetryAfterNothing { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Quant temps es queda aturat l'AutoDJ si no es diu res (D-073): després es torna a activar sol.</summary>
    public TimeSpan DefaultPause { get; init; } = TimeSpan.FromHours(2);

    /// <summary>El màxim que es pot aturar d'una vegada (mai indefinidament, D-073).</summary>
    public TimeSpan MaxPause { get; init; } = TimeSpan.FromHours(12);
}

/// <summary>
/// AutoDJ (fase 1, pas 8; D-014): quan a la cua hi ha menys de <see cref="AutoDjSettings.MinUpcoming"/> pistes, hi afegeix
/// la del pas següent de la rotació. Cada pas tria una pista de la seva subcategoria (o categoria), amb l'etiqueta si en té:
/// - «la menys reproduïda recentment»: la que fa més temps que no sona (si n'hi ha diverses que no han sonat mai, a l'atzar);
/// - «la menys reproduïda»: la que ha sonat menys vegades (si n'hi ha diverses, a l'atzar);
/// - «aleatòria»: qualsevol.
/// Criteris de repetició (de moment, els de RadioDJ a l'emissora; la separació en minuts arriba a la fase 3):
/// mai una pista que ja és a la cua o sona; si el pas ho demana, tampoc un artista que ja hi és. Si així no en queda cap,
/// s'afluixen (primer l'artista i després la pista), com RadioDJ, que «reinicia» la subcategoria.
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
        lock (_sync)
        {
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
                RotationChangedBy = by;
                RotationChangedAt = now;
            }
            Rotation = rotation;
            Problem = null;
            Notice = null;
            _retryAt = DateTimeOffset.MinValue;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// «Regenera la cua» (D-034): treu de la cua les pistes que hi ha posat l'AutoDJ (no la que sona, ni les manuals, ni
    /// les dels esdeveniments) i la torna a omplir amb la rotació actual. El pas de la rotació torna enrere tants passos
    /// com pistes s'han tret, perquè la rotació continuï on era. Retorna quantes s'han tret.
    /// </summary>
    public int Regenerate()
    {
        int removed = _queue.RemoveWhere(i => i.Origin == QueueOrigin.AutoDj);
        lock (_sync)
        {
            if (Rotation is { Steps.Count: > 0 } rotation && removed > 0)
                NextStep = (((NextStep - removed) % rotation.Steps.Count) + rotation.Steps.Count) % rotation.Steps.Count;
            _retryAt = DateTimeOffset.MinValue;
        }
        Changed?.Invoke();
        return removed;
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

            // Si a la cua hi falten pistes, n'afegeix una
            if (_queue.Upcoming.Count < _settings.MinUpcoming && now >= _retryAt)
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

    /// <summary>Tria la pista següent i avança el pas de la rotació. Retorna null si no hi ha res que pugui sonar.</summary>
    private QueueItem? PickNext(DateOnly today, out string? problem)
    {
        problem = null;
        var inQueue = new List<QueueItem>(_queue.Upcoming);
        if (_queue.Current is { } current) inQueue.Add(current);
        var queuedIds = inQueue.Where(i => i.TrackId is not null).Select(i => i.TrackId!.Value).ToHashSet();
        var queuedArtists = inQueue.Select(i => i.Artist.Trim()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var skipped = new List<string>();
        if (Rotation is { Steps.Count: > 0 } rotation)
        {
            for (int attempt = 0; attempt < rotation.Steps.Count; attempt++)
            {
                var step = rotation.Steps[NextStep % rotation.Steps.Count];
                NextStep = (NextStep + 1) % rotation.Steps.Count;
                var track = Choose(_library.TracksForRotationStep(step, today), step.Selection, step.RepeatRule, queuedIds, queuedArtists);
                if (track is not null)
                {
                    if (skipped.Count > 0) problem = $"Rotació «{rotation.Name}»: sense pistes disponibles a {string.Join(", ", skipped)}; s'ha saltat";
                    var item = QueueItem.FromTrack(track, QueueOrigin.AutoDj);
                    if (step.Sweeper)
                    {
                        // Trepitjador (fase 3, pas 5): si no n'hi ha cap, la pista sona igual, sense sweeper, i es diu
                        item = item with { Sweeper = PickSweeper(today, inQueue, out string? sweeperProblem) };
                        if (sweeperProblem is not null) problem = problem is null ? sweeperProblem : $"{problem}. {sweeperProblem}";
                    }
                    return item;
                }
                skipped.Add($"«{step}»");
            }
        }

        // Reserva: música de tota la biblioteca (mai silenci)
        var fallback = Choose(_library.AvailableMusic(today), RotationSelection.LeastRecent, true, queuedIds, queuedArtists);
        string reason = Rotation is null ? "No hi ha cap rotació" : $"Cap pas de la rotació «{Rotation.Name}» no té pistes disponibles";
        if (fallback is null)
        {
            problem = $"{reason} i no hi ha cap música disponible a la biblioteca: l'AutoDJ no pot afegir res.";
            return null;
        }
        problem = $"{reason}: sona música de tota la biblioteca.";
        return QueueItem.FromTrack(fallback, QueueOrigin.AutoDj);
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
        var alreadyUsed = inQueue.Select(i => i.Sweeper?.TrackId).OfType<long>().ToHashSet();
        var track = Choose(_library.TracksForRotationStep(step, today), RotationSelection.LeastRecent, false, alreadyUsed, []);
        if (track is null)
        {
            problem = $"No hi ha cap sweeper disponible a «{SweepersName}»: la pista sona sense";
            return null;
        }
        return QueueItem.FromTrack(track, QueueOrigin.AutoDj);
    }

    /// <summary>Tria una pista d'entre les candidates, amb els criteris de repetició i, si n'hi ha, amb punts d'encadenament.</summary>
    private Track? Choose(IReadOnlyList<Track> candidates, RotationSelection selection, bool repeatRule,
        HashSet<long> queuedIds, HashSet<string> queuedArtists)
    {
        if (candidates.Count == 0) return null;

        List<Track> pool = candidates.Where(t => !queuedIds.Contains(t.Id)).ToList();
        if (repeatRule)
        {
            var otherArtists = pool.Where(t => !queuedArtists.Contains(t.Artist.Trim())).ToList();
            if (otherArtists.Count > 0) pool = otherArtists; // si no, s'afluixa la regla de l'artista
        }
        if (pool.Count == 0) pool = candidates.ToList(); // totes ja són a la cua: s'afluixa també la de la pista

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
        return pool[_random.Next(pool.Count)];
    }
}
