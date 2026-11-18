namespace OnaBages.Audio;

/// <summary>
/// Corregeix la deriva entre dos rellotges (entrades de línia, D-185): la targeta d'entrada i la de sortida no van mai a la
/// mateixa velocitat (unes desenes de ppm: uns quants segons al dia), i el so guardat entre totes dues aniria creixent o
/// buidant-se. Cada vegada que es passa so a la sortida, es diu quant n'hi ha de guardat i quant se'n passa, i retorna
/// quantes mostres cal treure (positiu) o repetir (negatiu) per tornar a prop de l'objectiu.
///
/// - Mira el so guardat amb una mitjana suau: els paquets arriben a batzegades (de 10 en 10 ms o més), i això no és deriva.
/// - Dins de la tolerància no fa res. Fora, corregeix en proporció a la diferència (la tornaria a l'objectiu en uns 5 s) i
///   com a molt un 0,2 % (una mostra de cada 500): d'una en una, no se sent. Amb una deriva normal, gairebé no actua.
/// </summary>
public sealed class DriftControl(double targetFrames, double toleranceFrames, int sampleRate)
{
    /// <summary>Quant es mou la mitjana a cada crida (amb una crida cada 10–15 ms, mig segon llarg).</summary>
    private const double Smoothing = 0.02;
    /// <summary>La correcció màxima: un 0,2 % de les mostres.</summary>
    public const double MaxRate = 0.002;
    /// <summary>En quants segons es tornaria a l'objectiu (si no passa del màxim).</summary>
    private const double CorrectOverSeconds = 5;

    private double _smoothed = double.NaN;
    private double _pending;

    /// <summary>El so guardat, en fotogrames, amb la mitjana suau (NaN abans de la primera crida).</summary>
    public double Smoothed => _smoothed;

    /// <param name="bufferedFrames">Quant so hi ha guardat ara (fotogrames).</param>
    /// <param name="movedFrames">Quant so es passa a la sortida en aquesta volta (fotogrames).</param>
    /// <returns>Quants fotogrames cal treure (positiu) o repetir (negatiu) ara.</returns>
    public int Update(double bufferedFrames, int movedFrames)
    {
        _smoothed = double.IsNaN(_smoothed) ? bufferedFrames : _smoothed + (bufferedFrames - _smoothed) * Smoothing;
        double error = _smoothed - targetFrames;
        double excess = Math.Abs(error) - toleranceFrames;
        if (excess <= 0)
        {
            _pending = 0;
            return 0;
        }
        double rate = Math.Min(MaxRate, excess / (CorrectOverSeconds * sampleRate)) * Math.Sign(error);
        _pending += rate * movedFrames;
        int frames = (int)_pending; // cap a zero: el que no arriba a una mostra sencera, la volta següent
        _pending -= frames;
        return frames;
    }

    /// <summary>Torna a començar (el so guardat ha canviat de cop, p. ex. s'ha tornat al marge).</summary>
    public void Reset()
    {
        _smoothed = double.NaN;
        _pending = 0;
    }
}
