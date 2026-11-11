namespace OnaBages.Audio;

/// <summary>
/// Una atenuació (ducking) de la música mentre sona un auxiliar (fase 2, pas 5), en fotogrames del mesclador:
/// - de <c>Start − DownFrames</c> a <see cref="Start"/>, la música baixa fins a <see cref="Db"/> (rampa constant en dB);
/// - de <see cref="Start"/> a <see cref="End"/>, es queda atenuada;
/// - de <see cref="End"/> a <c>End + UpFrames</c>, torna a pujar.
/// No canvia mai: per moure'n el final se'n fa una de nova (<see cref="WithEnd"/>) i el fil d'àudio la recull sencera.
/// </summary>
/// <param name="Start">Fotograma on la música ja ha de ser del tot atenuada (on comença l'auxiliar).</param>
/// <param name="End">Fotograma on comença a tornar a pujar (on acaba l'auxiliar; <see cref="long.MaxValue"/> = encara no se sap).</param>
/// <param name="Db">Atenuació, en dB (negativa: −12 vol dir 12 dB més fluix).</param>
/// <param name="DownFrames">Durada de la baixada.</param>
/// <param name="UpFrames">Durada de la pujada.</param>
public sealed record DuckEnvelope(long Start, long End, double Db, int DownFrames, int UpFrames)
{
    private readonly float _gain = (float)Math.Pow(10, Db / 20);

    /// <summary>Primer fotograma afectat.</summary>
    public long From => Start - DownFrames;

    /// <summary>Primer fotograma que ja no està afectat (la música torna a ser com era).</summary>
    public long Until => End >= long.MaxValue - UpFrames ? long.MaxValue : End + UpFrames;

    /// <summary>La mateixa atenuació, però acabant a <paramref name="end"/> (p. ex. si l'auxiliar s'atura abans).</summary>
    public DuckEnvelope WithEnd(long end) => this with { End = Math.Max(end, Start) };

    /// <summary>Si toca algun fotograma de l'interval [<paramref name="from"/>, <paramref name="until"/>).</summary>
    public bool Touches(long from, long until) => From < until && Until > from;

    /// <summary>Guany lineal de la música al fotograma <paramref name="frame"/> (1 = sense atenuar).</summary>
    public float GainAt(long frame)
    {
        if (frame < From || frame >= Until) return 1f;
        if (frame < Start)
        {
            double t = (double)(frame - From) / DownFrames; // DownFrames > 0: si fos 0, From == Start
            return (float)Math.Pow(10, Db * t / 20);
        }
        if (frame < End) return _gain;
        double up = (double)(frame - End) / UpFrames;    // igual: aquí UpFrames > 0
        return (float)Math.Pow(10, Db * (1 - up) / 20);
    }
}
