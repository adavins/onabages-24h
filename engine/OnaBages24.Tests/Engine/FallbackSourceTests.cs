using OnaBages.Audio;
using OnaBages24.Engine.Streaming;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// La font d'un emissor amb reserva per silenci (fase 5, pas 13b; D-202). L'entrada falsa envia so de 0,2 (o silenci) i
/// la còpia de la mescla, so de 0,5: el que arriba a l'emissor diu quina de les dues envia.
/// </summary>
public sealed class FallbackSourceTests : IDisposable
{
    private const int Rate = 48000;
    private const float InputLevel = 0.2f, MixLevel = 0.5f;
    private SampleRingBuffer? _tap;
    private FakeInput _input = new(Rate);
    private bool _inputMissing;
    private float _last = float.NaN; // el valor de l'últim tros que ha arribat a l'emissor
    private FallbackSource? _source;

    public void Dispose() => _source?.Dispose();

    private FallbackSource Create(bool withInput = true, double silenceSeconds = 1, double returnSeconds = 0.5)
    {
        _source = new FallbackSource(
            withInput ? () => _inputMissing ? throw new AudioDeviceException("no hi és") : _input : null,
            "Línia", Rate, tap => _tap = tap, tap => { if (ReferenceEquals(_tap, tap)) _tap = null; },
            TimeSpan.FromSeconds(silenceSeconds), TimeSpan.FromSeconds(returnSeconds));
        _source.DataAvailable += (samples, _, _) => { if (samples.Length > 0) Volatile.Write(ref _last, samples[^1]); };
        _source.Start();
        return _source;
    }

    /// <summary>
    /// Fa passar el temps (de debò): cada 10 ms, 10 ms de so a l'entrada (<paramref name="input"/>: el nivell, 0 = silenci,
    /// null = res) i a la mescla.
    /// </summary>
    private void Pump(double seconds, float? input)
    {
        const int packet = Rate / 100; // 10 ms
        var inputBlock = new float[packet * 2];
        var mixBlock = new float[packet * 2];
        Array.Fill(mixBlock, MixLevel);
        if (input is { } level) Array.Fill(inputBlock, level);
        // Tant de so com temps de debò ha passat (Thread.Sleep(10) pot dormir 15 ms o més)
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long sent = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * Rate);
            for (; sent + packet <= due; sent += packet)
            {
                if (input is not null) _input.Push(inputBlock);
                _tap?.Write(mixBlock);
            }
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Envia_l_entrada_si_porta_so_i_la_mescla_si_fa_estona_que_arriba_en_silenci()
    {
        var source = Create();
        Pump(0.5, InputLevel);
        Assert.Equal(SourceMode.Input, source.Status.Mode);
        Assert.Equal(InputLevel, _last);

        // 1 s de silenci: passa a la mescla
        Pump(1.5, 0);
        Assert.Equal(SourceMode.Mix, source.Status.Mode);
        Assert.Equal("l'entrada «Línia» fa 1 s que arriba en silenci", source.Status.Reason);
        Assert.NotNull(source.Status.MixSince);
        Pump(0.2, 0);
        Assert.Equal(MixLevel, _last);

        // Torna el so: fins al cap de mig segon seguit, encara la mescla; després, l'entrada
        Pump(0.2, InputLevel);
        Assert.Equal(SourceMode.Mix, source.Status.Mode);
        Pump(0.8, InputLevel);
        Assert.Equal(SourceMode.Input, source.Status.Mode);
        Assert.Null(source.Status.MixSince);
        Pump(0.2, InputLevel);
        Assert.Equal(InputLevel, _last);
    }

    [Fact]
    public void Un_silenci_curt_no_la_fa_saltar()
    {
        var source = Create(silenceSeconds: 2);
        Pump(0.3, InputLevel);
        Pump(1, 0); // una pausa d'un segon
        Pump(0.3, InputLevel);
        Assert.Equal(SourceMode.Input, source.Status.Mode);
    }

    [Fact]
    public void Si_l_entrada_es_desconnecta_passa_a_la_mescla_de_seguida_i_hi_torna_quan_es_pot_tornar_a_obrir()
    {
        var source = Create();
        Pump(0.5, InputLevel);
        _inputMissing = true;
        _input.Fail("USB desconnectat");
        Pump(0.5, null);
        Assert.Equal(SourceMode.Mix, source.Status.Mode);
        Assert.StartsWith("l'entrada «Línia» no envia so", source.Status.Reason);
        Assert.Equal(MixLevel, _last);

        // Es torna a obrir al cap de 10 s (FallbackSource.ReopenDelay): aquí no s'espera tant, només es mira que continua
        // enviant la mescla, sense cap error cap a l'emissor
        Pump(0.5, null);
        Assert.Equal(MixLevel, _last);
    }

    [Fact]
    public void Si_l_entrada_no_envia_res_en_obrir_la_passa_a_la_mescla()
    {
        var source = Create();
        Pump(0.5, null); // s'obre però no arriba res
        Assert.Equal(SourceMode.Input, source.Status.Mode); // encara s'espera (pot tardar una mica)
        Pump(FallbackSource.InputTimeout.TotalSeconds + 0.5, null);
        Assert.Equal(SourceMode.Mix, source.Status.Mode);
        Assert.Equal(MixLevel, _last);
    }

    [Fact]
    public void Sense_entrada_sempre_envia_la_mescla_del_motor()
    {
        var source = Create(withInput: false);
        Pump(0.5, null);
        Assert.Equal(SourceMode.Mix, source.Status.Mode);
        Assert.Null(source.Status.Reason); // no és cap reserva
        Assert.Equal(MixLevel, _last);

        source.Stop();
        Assert.Null(_tap); // en aturar-la, deixa la còpia de la mescla
    }

    [Fact]
    public void El_mesclador_escriu_una_copia_de_la_mescla_a_qui_la_demana()
    {
        var mixer = new Mixer(Rate);
        var tap = new SampleRingBuffer(Rate * 2);
        var bytes = new byte[480 * 2 * sizeof(float)];
        mixer.AddTap(tap);
        mixer.Read(bytes);
        Assert.Equal(480 * 2, tap.Count); // 10 ms estèreo
        mixer.RemoveTap(tap);
        mixer.Read(bytes);
        Assert.Equal(480 * 2, tap.Count); // ja no s'hi escriu
    }
}
