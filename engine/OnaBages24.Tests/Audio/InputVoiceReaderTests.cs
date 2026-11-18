using System.Diagnostics;
using OnaBages.Audio;

namespace OnaBages24.Tests.Audio;

/// <summary>
/// Una entrada de línia que sona en una veu del mesclador (D-185): retard petit i correcció de la deriva dels rellotges (la
/// targeta d'entrada i la de sortida no van mai a la mateixa velocitat).
/// </summary>
[Collection(SoloRealTime.Name)]
public sealed class InputVoiceReaderTests : IDisposable
{
    private const int Rate = 48000;
    private readonly Mixer _mixer = new(Rate);
    private readonly CancellationTokenSource _stop = new();

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <summary>Una entrada falsa que envia so constant pel seu compte, com una targeta que va <paramref name="speed"/> vegades més de pressa.</summary>
    private FakeInput Feeding(double speed, float value = 0.4f)
    {
        var input = new FakeInput(Rate, 2);
        _ = Task.Run(() =>
        {
            int packet = Rate / 100;
            var buffer = Enumerable.Repeat(value, packet * 2).ToArray();
            var watch = Stopwatch.StartNew();
            long sent = 0;
            while (!_stop.IsCancellationRequested)
            {
                if (!input.IsRunning) { Thread.Sleep(5); watch.Restart(); sent = 0; continue; }
                while (sent <= watch.Elapsed.TotalSeconds * Rate * speed)
                {
                    input.Push(buffer);
                    sent += packet;
                }
                Thread.Sleep(2);
            }
        });
        return input;
    }

    /// <summary>Fa sonar el mesclador en temps real (com la targeta de sortida) durant <paramref name="seconds"/>.</summary>
    private void Play(double seconds, Action? every100ms = null)
    {
        const int block = Rate / 100;
        var bytes = new byte[block * 2 * sizeof(float)];
        var watch = Stopwatch.StartNew();
        long frames = (long)(seconds * Rate);
        for (long done = 0; done < frames; done += block)
        {
            _mixer.Read(bytes);
            if ((done / block) % 10 == 9) every100ms?.Invoke();
            while (watch.Elapsed.TotalSeconds < (double)(done + block) / Rate) Thread.Sleep(1);
        }
    }

    private InputVoiceReader Start(FakeInput input, TimeSpan tolerance)
    {
        var voice = _mixer.AcquireVoice()!;
        var reader = new InputVoiceReader(voice, () => input, Rate,
            new InputOptions { Duration = TimeSpan.FromMinutes(1), DriftTolerance = tolerance }, () => _mixer.FramesRendered);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (reader.State != ReaderState.Ready && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.Equal(ReaderState.Ready, reader.State);
        voice.Start(_mixer.FramesRendered + Rate / 100);
        return reader;
    }

    [Fact]
    public void Amb_una_entrada_que_va_mes_de_pressa_es_treuen_mostres_i_el_retard_no_creix()
    {
        // Un 0,3 %: molt més que una targeta de debò (i més que el màxim de correcció, 0,2 %), perquè es vegi en pocs segons
        var input = Feeding(1.003);
        using var reader = Start(input, TimeSpan.FromMilliseconds(10));
        int underrunsAtStart = reader.Voice.Underruns;

        Play(10);

        var (dropped, repeated) = reader.DriftCorrections;
        // En net (si l'ordinador va carregat, els paquets arriben a batzegades i alguna correcció pot anar a l'inrevés)
        Assert.True(dropped - repeated > 200, $"tretes {dropped}, repetides {repeated}");
        // Sense corregir, n'hi hauria 30 ms de més; amb el màxim de correcció, com a molt uns 10
        Assert.InRange(reader.Buffered.TotalMilliseconds, 80, 260);
        Assert.Equal(underrunsAtStart, reader.Voice.Underruns);
    }

    [Fact]
    public void Amb_una_entrada_que_va_mes_a_poc_a_poc_es_repeteixen_mostres()
    {
        // Un 0,3 % més a poc a poc: més que el màxim de correcció (0,2 %), o sigui que el so guardat baixa una mica igualment
        // (aquí no es mira si hi ha talls). Que el corrector va com cal amb derives de debò, ho comproven DriftControlTests.
        var input = Feeding(0.997);
        using var reader = Start(input, TimeSpan.FromMilliseconds(10));

        // 20 s: en començar, el so guardat pot ser uns mil·lisegons de més (i se'n treuen); després la deriva (60 ms en 20 s)
        // el buida i s'han de repetir mostres
        Play(20);

        var (dropped, repeated) = reader.DriftCorrections;
        Assert.True(repeated > 200, $"tretes {dropped}, repetides {repeated}");
        Assert.InRange(reader.Buffered.TotalMilliseconds, 60, 240); // no s'ha quedat sense so guardat
    }

    [Fact]
    public void Si_les_dues_targetes_van_igual_no_es_toca_res()
    {
        var input = Feeding(1.0);
        using var reader = Start(input, TimeSpan.FromMilliseconds(40)); // la tolerància de debò

        Play(5);

        var (dropped, repeated) = reader.DriftCorrections;
        Assert.True(dropped + repeated < 50, $"tretes {dropped}, repetides {repeated}");
        Assert.InRange(reader.Buffered.TotalMilliseconds, 80, 240);
    }

    [Fact]
    public void Si_l_entrada_es_desconnecta_avisa_i_es_torna_a_obrir()
    {
        var first = Feeding(1.0);
        var second = Feeding(1.0, 0.2f);
        int opened = 0;
        var voice = _mixer.AcquireVoice()!;
        using var reader = new InputVoiceReader(voice, () => Interlocked.Increment(ref opened) == 1 ? first : second, Rate,
            new InputOptions { Duration = TimeSpan.FromMinutes(1), RetryEvery = TimeSpan.FromMilliseconds(100) }, () => _mixer.FramesRendered);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (reader.State != ReaderState.Ready && DateTime.UtcNow < until) Thread.Sleep(10);
        voice.Start(_mixer.FramesRendered + Rate / 100);
        Play(0.5);
        Assert.True(reader.Connected);

        first.Fail("dispositiu desconnectat");
        Play(1);

        Assert.True(reader.Connected); // ja és la segona
        var notices = reader.TakeNotices();
        Assert.Contains(notices, n => n.Cut && n.Message.StartsWith("L'entrada s'ha desconnectat"));
        Assert.Contains(notices, n => !n.Cut);
        Assert.Equal(2, opened);
    }
}

/// <summary>
/// Proves en temps real que mesuren quant so queda guardat: han de córrer soles, sense cap altra prova en paral·lel. Si
/// l'ordinador va carregat, el bucle que fa de targeta de sortida s'endarrereix i el so guardat creix de debò.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SoloRealTime
{
    public const string Name = "Temps real, soles";
}
