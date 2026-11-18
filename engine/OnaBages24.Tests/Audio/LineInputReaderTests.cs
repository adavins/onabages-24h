using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages24.Tests.Audio;

/// <summary>Lector d'entrades de línia (D-185): conversió a estèreo i a la freqüència de sortida, silenci, errors i rellotge.</summary>
public class LineInputReaderTests
{
    private const int Rate = 48000;

    /// <summary>Espera fins que es compleixi (com a molt 3 s).</summary>
    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.True(condition());
    }

    /// <summary>Envia paquets de 10 ms amb aquests valors per canal, al ritme del rellotge multiplicat per <paramref name="speed"/>.</summary>
    private static void Feed(FakeInput input, double seconds, float[] values, double speed = 1)
    {
        int packet = input.SampleRate / 100;
        var buffer = new float[packet * input.Channels];
        for (int i = 0; i < packet; i++)
            for (int c = 0; c < input.Channels; c++) buffer[i * input.Channels + c] = values[c];
        var watch = Stopwatch.StartNew();
        long frames = (long)(seconds * input.SampleRate);
        for (long sent = 0; sent < frames; sent += packet)
        {
            // Mostres que «hauria» d'haver enviat ara una targeta que va speed vegades més de pressa
            while (sent > watch.Elapsed.TotalSeconds * input.SampleRate * speed) Thread.Sleep(1);
            input.Push(buffer);
        }
    }

    /// <summary>Llegeix el que hi ha a la memòria del lector (estèreo).</summary>
    private static float[] Drain(LineInputReader reader)
    {
        var all = new float[reader.Buffer.Count];
        int n = reader.Buffer.Read(all);
        return all[..n];
    }

    [Fact]
    public void Una_entrada_mono_a_44100_arriba_en_estereo_a_la_freqüència_de_sortida()
    {
        var input = new FakeInput(44100, 1);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);

        Feed(input, 1, [0.5f], speed: 50); // d'un cop: només es mira la conversió
        WaitFor(() => reader.Received >= TimeSpan.FromSeconds(0.95));

        Assert.Equal(LineInputState.Receiving, reader.State);
        Assert.Equal("44.100 Hz, mono", reader.Format);
        Assert.InRange(reader.Received.TotalSeconds, 0.95, 1.01); // 1 s d'àudio, ara a 48.000 Hz
        var samples = Drain(reader);
        // Fora de l'arrencada i del final del convertidor, tot amb el valor de l'entrada, als dos canals: sense cap buit
        var steady = samples[(samples.Length / 10 & ~1)..(samples.Length * 9 / 10 & ~1)];
        Assert.InRange(steady.Min(), 0.49f, 0.51f);
        Assert.InRange(steady.Max(), 0.49f, 0.51f);
        Assert.True(input.IsRunning);
    }

    [Fact]
    public void D_una_entrada_de_mes_de_dos_canals_es_fan_servir_els_dos_primers()
    {
        var input = new FakeInput(Rate, 4);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);

        Feed(input, 0.2, [0.2f, 0.4f, 0.9f, 0.9f], speed: 50);
        WaitFor(() => reader.Received >= TimeSpan.FromSeconds(0.19));

        Assert.Equal("48.000 Hz, 4 canals", reader.Format);
        var samples = Drain(reader);
        Assert.Equal(0.2f, samples[100]);
        Assert.Equal(0.4f, samples[101]);
        Assert.Equal(-8, Math.Round(reader.MaxPeakDb)); // el pic és el dels canals que sonen (0,4: −8 dB), no el dels altres (0,9)
    }

    [Fact]
    public void Mesura_el_silenci_i_tambe_el_que_Windows_marca_com_a_silenci()
    {
        var input = new FakeInput(Rate, 2);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);

        Feed(input, 0.5, [0.3f, 0.3f], speed: 50);
        WaitFor(() => reader.Received >= TimeSpan.FromSeconds(0.49));
        Assert.Equal(TimeSpan.Zero, reader.SilentFor);

        // Un paquet que Windows marca com a silenci: compta com a zeros, encara que porti dades
        var noise = Enumerable.Repeat(0.8f, Rate / 100 * 2).ToArray();
        for (int i = 0; i < 150; i++) input.Push(noise, silent: true);
        WaitFor(() => reader.SilentFor >= TimeSpan.FromSeconds(1.49));
        Drain(reader);
        Assert.InRange(reader.MaxPeakDb, -11, -10); // el 0,8 no ha comptat (el pic és el del 0,3)

        Feed(input, 0.1, [0.3f, 0.3f], speed: 50);
        WaitFor(() => reader.SilentFor == TimeSpan.Zero);
    }

    [Fact]
    public void La_discontinuitat_del_primer_paquet_no_compta_com_a_dades_perdudes()
    {
        var input = new FakeInput(Rate, 2);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);
        var packet = Enumerable.Repeat(0.3f, Rate / 100 * 2).ToArray();

        input.Push(packet, discontinuity: true); // el primer: Windows sovint el marca així
        input.Push(packet);
        Assert.Equal(0, reader.Discontinuities);

        input.Push(packet, discontinuity: true); // a mig: sí que s'han perdut dades
        Assert.Equal(1, reader.Discontinuities);
    }

    [Fact]
    public void Si_no_es_pot_obrir_falla_i_diu_per_que()
    {
        using var reader = new LineInputReader(() => throw new AudioDeviceException("Entrada no disponible: no es troba a Windows."),
            Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));

        WaitFor(() => reader.State == LineInputState.Failed);
        Assert.Equal("Entrada no disponible: no es troba a Windows.", reader.Error);
    }

    [Fact]
    public void Si_es_desconnecta_falla_i_tanca_l_entrada()
    {
        var input = new FakeInput(Rate, 2);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);
        Feed(input, 0.1, [0.3f, 0.3f], speed: 50);
        WaitFor(() => reader.State == LineInputState.Receiving);

        input.Fail("dispositiu desconnectat");

        WaitFor(() => reader.State == LineInputState.Failed);
        Assert.StartsWith("L'entrada s'ha desconnectat", reader.Error);
        WaitFor(() => input.Disposed);
    }

    [Fact]
    public void Si_no_envia_so_falla_al_cap_d_una_estona()
    {
        var input = new FakeInput(Rate, 2);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(500));

        WaitFor(() => reader.State == LineInputState.Failed);
        Assert.StartsWith("L'entrada no envia so", reader.Error);
    }

    [Fact]
    public void Mesura_quant_mes_de_pressa_va_el_rellotge_de_l_entrada()
    {
        var input = new FakeInput(Rate, 2);
        using var reader = new LineInputReader(() => input, Rate, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2));
        WaitFor(() => input.IsRunning);

        // Una targeta que va un 0,2 % més de pressa (2.000 ppm: molt més del normal, perquè es vegi en 6 s)
        var feeding = Task.Run(() => Feed(input, 6.5, [0.3f, 0.3f], speed: 1.002));
        while (!feeding.IsCompleted)
        {
            reader.Buffer.Skip(reader.Buffer.Count); // com si sonés
            Thread.Sleep(20);
        }

        Assert.NotNull(reader.ClockPpm);
        Assert.InRange(reader.ClockPpm.Value, 1000, 3000);
    }
}
