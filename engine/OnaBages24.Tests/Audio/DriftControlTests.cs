using OnaBages.Audio;

namespace OnaBages24.Tests.Audio;

/// <summary>
/// Corrector de la deriva dels rellotges (entrades de línia, D-185), simulat sense esperar: cada pas són 10 ms, l'entrada
/// envia so a la seva velocitat i la sortida se'l va emportant a la seva. Es mira quant so queda guardat.
/// </summary>
public class DriftControlTests
{
    private const int Rate = 48000;
    private const double Target = 0.150 * Rate;     // el marge: 150 ms
    private const double Tolerance = 0.040 * Rate;  // 40 ms

    private sealed record Result(double Min, double Max, double End, long Net);

    /// <param name="ppm">Quant més de pressa va l'entrada que la sortida (parts per milió).</param>
    /// <param name="jitterMs">Com a molt, quant varia el que es veu guardat a cada pas (els paquets arriben a batzegades).</param>
    private static Result Simulate(double ppm, double jitterMs, TimeSpan duration)
    {
        var control = new DriftControl(Target, Tolerance, Rate);
        var random = new Random(7);
        double buffered = Target, incoming = 0;
        double min = double.MaxValue, max = double.MinValue;
        long net = 0;
        long steps = (long)(duration.TotalSeconds * 100);
        for (long step = 0; step < steps; step++)
        {
            incoming += Rate / 100.0 * (1 + ppm / 1e6);
            int arrived = (int)incoming;
            incoming -= arrived;
            buffered += arrived;
            double seen = buffered + (random.NextDouble() * 2 - 1) * jitterMs / 1000 * Rate;
            int correction = control.Update(seen, arrived);
            buffered -= correction;
            net += correction;
            buffered -= Rate / 100; // la sortida
            if (step > 6000) // després del primer minut
            {
                min = Math.Min(min, buffered);
                max = Math.Max(max, buffered);
            }
        }
        return new Result(min, max, buffered, net);
    }

    private static double Ms(double frames) => frames * 1000 / Rate;

    [Fact]
    public void Si_les_dues_targetes_van_igual_no_toca_res_encara_que_els_paquets_arribin_a_batzegades()
    {
        var r = Simulate(0, jitterMs: 15, TimeSpan.FromHours(1));

        Assert.Equal(0, r.Net);
        Assert.Equal(Target, r.End, 1);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(-50)]
    [InlineData(200)]
    [InlineData(-200)]
    public void Amb_una_deriva_normal_el_so_guardat_es_queda_dins_de_la_tolerancia(double ppm)
    {
        var r = Simulate(ppm, jitterMs: 15, TimeSpan.FromHours(4));

        // Sense corregir, en 4 hores s'haurien acumulat (o faltat) 0,7 s amb 50 ppm i 2,9 s amb 200 ppm
        double drift = ppm / 1e6 * 4 * 3600 * Rate;
        // S'ha corregit gairebé tot (el que queda és com a molt la tolerància i una mica)
        Assert.InRange(r.Net, drift - Tolerance * 1.5, drift + Tolerance * 1.5);
        // (es mira just després que la sortida se n'emporti el seu tros de 10 ms: per això el marge de sota és més ample)
        Assert.InRange(Ms(r.Min), 150 - 40 - 20, 150 + 40 + 10);
        Assert.InRange(Ms(r.Max), 150 - 40 - 20, 150 + 40 + 10);
    }

    [Fact]
    public void Amb_una_deriva_molt_gran_tambe_ho_aguanta_sense_passar_del_maxim()
    {
        // 1.500 ppm: molt més que qualsevol targeta, però per sota del màxim de correcció (0,2 %)
        var r = Simulate(1500, jitterMs: 15, TimeSpan.FromMinutes(30));

        Assert.InRange(Ms(r.Max), 0, 150 + 40 + 30);
        Assert.InRange(Ms(r.Min), 150, 1000);
        // Mai més del 0,2 % de les mostres
        Assert.True(r.Net <= DriftControl.MaxRate * 30 * 60 * Rate + 1);
    }
}
