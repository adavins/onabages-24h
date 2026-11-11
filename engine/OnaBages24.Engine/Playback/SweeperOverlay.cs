namespace OnaBages24.Engine.Playback;

/// <summary>
/// Trepitjador (fase 3, pas 5): fa sonar el sweeper d'una pista de la cua (<see cref="QueueItem.Sweeper"/>) a sobre del seu
/// inici, com «A l'inici» de RadioDJ. La música baixa (<see cref="DuckingDb"/>, D-078) mentre sona el sweeper.
///
/// Fa servir el reproductor intern <see cref="AuxPlayers.SweeperPlayer"/>:
/// - quan la pista següent ja està programada (uns 20 s abans), se n'obre el sweeper per endavant;
/// - quan falten menys de <see cref="Lead"/> perquè comenci, es programa el sweeper al mateix fotograma que la pista;
/// - si la pista comença abans del previst (p. ex. «Següent»), el sweeper entra tan aviat com es pot, si encara és a
///   l'inici (<see cref="LateLimit"/>);
/// - si la pista surt de la cua sense sonar, el sweeper preparat es deixa anar.
/// Si el sweeper no es pot obrir, la pista sona igual (mai silenci) i ho diu <see cref="AuxPlayers.Failed"/>.
///
/// Treballa al fil de control (<see cref="Tick"/>), abans dels auxiliars.
/// </summary>
public sealed class SweeperOverlay
{
    /// <summary>
    /// Quant abans de començar la pista es programa el sweeper. No es fa abans perquè, si l'operador salta a la
    /// següent, la pista comença en un altre fotograma i un sweeper ja programat no es podria moure.
    /// </summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(300);

    /// <summary>Si la pista ja fa més d'això que sona quan se'n pot programar el sweeper, ja no sona: no seria a l'inici.</summary>
    public static readonly TimeSpan LateLimit = TimeSpan.FromSeconds(2);

    /// <summary>Atenuació estàndard de la música mentre sona un sweeper (D-078; provisional, es decidirà escoltant-ho).</summary>
    public const double StandardDuckingDb = -8;

    private readonly QueuePlayer _queue;
    private readonly AuxPlayers _aux;

    // El sweeper preparat ara: per a quina pista de la cua, l'id a AuxPlayers i si ja s'ha programat
    private Guid? _itemId;
    private Guid? _auxId;
    private bool _triggered;

    public SweeperOverlay(QueuePlayer queue, AuxPlayers aux)
    {
        _queue = queue;
        _aux = aux;
    }

    /// <summary>Atenuació de la música mentre sona un sweeper, en dB (la de <c>ducking.set</c>). 0 = sense atenuació.</summary>
    public double DuckingDb { get; set; } = StandardDuckingDb;

    /// <summary>Fil de control: prepara, programa o deixa anar el sweeper de la pista següent. Barata quan no cal fer res.</summary>
    public void Tick()
    {
        var next = _queue.ScheduledNext;
        long now = _queue.CurrentFrame;

        // 1. El sweeper preparat era per a una pista que ja no és la següent programada
        if (_itemId is { } itemId && next?.Item.Id != itemId)
        {
            if (!_triggered && _auxId is { } auxId)
            {
                // Ha començat abans del previst: tan aviat com es pugui, si encara és a l'inici. Si no, ja no sona.
                if (_queue.CurrentStart is { } current && current.Item.Id == itemId && now - current.StartFrame < Frames(LateLimit))
                    _aux.StartAt(auxId, current.StartFrame);
                else
                    _aux.Release(auxId);
            }
            _itemId = null;
            _auxId = null;
            _triggered = false;
        }

        // 2. Una pista següent nova amb sweeper: s'obre per endavant
        if (_itemId is null && next is { Item.Sweeper: { } sweeper } upcoming)
        {
            _itemId = upcoming.Item.Id;
            _auxId = _aux.Prepare(AuxPlayers.SweeperPlayer, sweeper, DuckingDb < 0 ? DuckingDb : null);
            _triggered = _auxId is null; // sense veus lliures: la pista sonarà sense sweeper
        }

        // 3. Falta poc perquè comenci: es programa al mateix fotograma
        if (!_triggered && _auxId is { } id && next is { } n && n.Item.Id == _itemId && n.StartFrame - now <= Frames(Lead))
        {
            _aux.StartAt(id, n.StartFrame);
            _triggered = true;
        }
    }

    private long Frames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * _queue.SampleRate);
}
