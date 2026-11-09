using OnaBages.Audio;

namespace OnaBages24.Tests;

/// <summary>
/// Mesclador amb veus (fase 1, pas 7a; docs/disseny-motor.md, secció 8). Sense targeta de so: la prova fa el paper
/// del fil d'àudio de Windows i demana blocs al mesclador. Els senyals són constants (0,5), de manera que es pot
/// comprovar a la mostra exacta on comença i on acaba cada veu.
/// </summary>
public class MixerTests
{
    private const int Rate = 48000;

    /// <summary>Demana <paramref name="frames"/> fotogrames en blocs de <paramref name="block"/>, com faria la targeta. Retorna l'estèreo.</summary>
    private static float[] Pump(Mixer mixer, int frames, int block = 480)
    {
        var all = new float[frames * 2];
        var bytes = new byte[block * 2 * sizeof(float)];
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(block, frames - done);
            mixer.Read(bytes.AsSpan(0, n * 2 * sizeof(float)));
            Buffer.BlockCopy(bytes, 0, all, done * 2 * sizeof(float), n * 2 * sizeof(float));
            done += n;
        }
        return all;
    }

    /// <summary>Omple la veu amb <paramref name="frames"/> fotogrames d'un valor constant.</summary>
    private static void Fill(MixerVoice voice, int frames, float value = 0.5f, bool finished = true)
    {
        var samples = new float[frames * 2];
        Array.Fill(samples, value);
        Assert.Equal(samples.Length, voice.Buffer.Write(samples));
        if (finished) voice.MarkSourceFinished();
    }

    private static int FirstNonZero(float[] stereo) => Array.FindIndex(stereo, s => s != 0) / 2;

    private static int LastNonZero(float[] stereo) => Array.FindLastIndex(stereo, s => s != 0) / 2;

    [Fact]
    public void Sense_veus_dona_silenci_i_compta_els_fotogrames()
    {
        var mixer = new Mixer(Rate);
        var output = Pump(mixer, 4800);

        Assert.All(output, s => Assert.Equal(0f, s));
        Assert.Equal(4800, mixer.FramesRendered);
        Assert.True(mixer.SinceLastRender < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(1000, 480)]
    [InlineData(1000, 441)]   // l'inici cau a mig bloc
    [InlineData(0, 512)]
    [InlineData(4799, 4800)]  // l'últim fotograma d'un bloc gran
    public void Una_veu_comenca_a_la_mostra_exacta(int startFrame, int block)
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 2000);
        voice.Start(startFrame);

        var output = Pump(mixer, startFrame + 3000, block);

        Assert.Equal(startFrame, FirstNonZero(output));
        Assert.Equal(startFrame + 1999, LastNonZero(output)); // sona sencera: 2000 fotogrames
        Assert.Equal(startFrame, voice.ActualStartFrame);
        Assert.Equal(VoiceState.Finished, voice.State);
        Assert.Equal(0, voice.Underruns); // s'ha acabat perquè no hi ha més dades, no és un tall
    }

    [Fact]
    public void Dues_veus_que_coincideixen_se_sumen()
    {
        var mixer = new Mixer(Rate);
        var a = mixer.AcquireVoice()!;
        var b = mixer.AcquireVoice()!;
        Fill(a, 1000, 0.25f);
        Fill(b, 1000, 0.5f);
        a.Start(0);
        b.Start(500);

        var output = Pump(mixer, 2000);

        Assert.Equal(0.25f, output[100 * 2]);   // només A
        Assert.Equal(0.75f, output[700 * 2]);   // A i B (el solapament)
        Assert.Equal(0.5f, output[1200 * 2]);   // només B
        Assert.Equal(0f, output[1600 * 2]);
    }

    [Fact]
    public void Una_aturada_sense_fos_talla_a_la_mostra_exacta()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 10_000, finished: false);
        voice.Start(100);
        voice.Stop(3333);

        var output = Pump(mixer, 6000, 441);

        Assert.Equal(3332, LastNonZero(output));
        Assert.Equal(VoiceState.Finished, voice.State);
        Assert.Equal(0, voice.Underruns);
    }

    [Fact]
    public void El_fos_de_sortida_baixa_constant_en_dB_i_acaba_la_veu()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 20_000, 1f, finished: false);
        voice.Start(0);
        voice.Stop(atFrame: 4800, fadeFrames: 4800); // fos de 100 ms

        var output = Pump(mixer, 12_000);

        Assert.Equal(1f, output[4799 * 2]);                       // abans del fos, sencer
        double midDb = 20 * Math.Log10(output[(4800 + 2400) * 2]);
        Assert.InRange(midDb, -31, -29);                           // a la meitat, −30 dB
        Assert.Equal(9599, LastNonZero(output));                   // s'acaba just en acabar el fos
        Assert.Equal(VoiceState.Finished, voice.State);
    }

    [Fact]
    public void El_fos_d_entrada_puja_des_de_menys_60_dB()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 10_000, 1f);
        voice.Start(1000, fadeInFrames: 2000);

        var output = Pump(mixer, 12_000);

        Assert.Equal(1000, FirstNonZero(output));
        Assert.InRange(20 * Math.Log10(output[1000 * 2]), -61, -59);
        Assert.InRange(20 * Math.Log10(output[2000 * 2]), -31, -29);
        Assert.Equal(1f, output[3000 * 2]);
    }

    [Fact]
    public void Un_fos_programat_tard_comenca_ara_i_dura_sencer()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 20_000, 1f, finished: false);
        voice.Start(0);
        Pump(mixer, 4800);

        voice.Stop(atFrame: 1000, fadeFrames: 2400); // el fotograma 1000 ja ha passat
        var output = Pump(mixer, 9600);

        Assert.Equal(2399, LastNonZero(output)); // 2400 fotogrames de fos a partir d'ara
        Assert.Equal(VoiceState.Finished, voice.State);
    }

    [Fact]
    public void Una_veu_programada_tard_comenca_al_bloc_seguent()
    {
        var mixer = new Mixer(Rate);
        Pump(mixer, 4800);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 1000);

        voice.Start(1000); // ja ha passat: el fil de control ha arribat tard
        var output = Pump(mixer, 2000);

        Assert.Equal(0, FirstNonZero(output));
        Assert.Equal(4800, voice.ActualStartFrame); // se sap quan ha començat de debò
    }

    [Fact]
    public void Si_la_memoria_es_buida_abans_d_hora_compta_un_tall_i_continua()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 1000, finished: false); // el fil de lectura «va lent»
        voice.Start(0);

        var output = Pump(mixer, 2400);
        Assert.True(voice.Underruns > 0);
        Assert.Equal(VoiceState.Playing, voice.State); // no s'acaba: espera més dades
        Assert.Equal(999, LastNonZero(output));

        Fill(voice, 1000); // arriben les dades
        output = Pump(mixer, 2400);
        Assert.Equal(0, FirstNonZero(output));
        Assert.Equal(VoiceState.Finished, voice.State);
        Assert.Equal(2000, voice.FramesPlayed);
    }

    [Fact]
    public void El_guany_s_aplica_amb_una_rampa()
    {
        var mixer = new Mixer(Rate);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 10_000, 1f);
        voice.Start(0);
        Pump(mixer, 480);

        voice.Gain = 0.5f;
        var output = Pump(mixer, 960, block: 480);

        Assert.InRange(output[0], 0.5f, 1f);       // dins del primer bloc, la rampa
        Assert.Equal(0.5f, output[479 * 2], 3);    // al final del bloc, el guany nou
        Assert.Equal(0.5f, output[700 * 2], 3);
    }

    [Fact]
    public void Les_veus_es_reserven_i_s_alliberen()
    {
        var mixer = new Mixer(Rate, voiceCount: 2);
        var a = mixer.AcquireVoice()!;
        var b = mixer.AcquireVoice()!;
        Assert.Null(mixer.AcquireVoice()); // no n'hi ha més

        Fill(a, 100);
        a.Start(0);
        Pump(mixer, 50);
        Assert.False(mixer.ReleaseVoice(a)); // encara sona

        Pump(mixer, 100);
        Assert.True(mixer.ReleaseVoice(a));  // ja ha acabat
        Assert.True(mixer.ReleaseVoice(b));  // reservada sense fer servir

        var again = mixer.AcquireVoice()!;
        Assert.Equal(0, again.Buffer.Count);  // es reprèn buida
        Assert.Equal(-1, again.ActualStartFrame);
        Assert.Throws<InvalidOperationException>(() => mixer.Voices.First(v => v.State == VoiceState.Idle).Start(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Funciona_amb_sortides_d_un_o_de_mes_de_dos_canals(int channels)
    {
        var mixer = new Mixer(Rate, outChannels: channels);
        var voice = mixer.AcquireVoice()!;
        Fill(voice, 1000);
        voice.Start(0);

        var bytes = new byte[480 * channels * sizeof(float)];
        mixer.Read(bytes);
        var samples = new float[480 * channels];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        Assert.Equal(0.5f, samples[0]);
        if (channels == 4)
        {
            Assert.Equal(0.5f, samples[1]);
            Assert.Equal(0f, samples[2]);
            Assert.Equal(0f, samples[3]);
        }
    }

    [Fact]
    public void Mesclar_no_crea_objectes()
    {
        var mixer = new Mixer(Rate);
        var bytes = new byte[480 * 2 * sizeof(float)];
        var allocatedPerRound = new List<long>();

        // L'escenari sencer (fos d'entrada, so, canvi de guany, fos de sortida i final) es repeteix 6 vegades.
        // - La primera no compta: la primera vegada que s'executa un tros de codi, .NET el compila i pot crear objectes.
        // - El comptador de .NET (GetAllocatedBytesForCurrentThread) dona de tant en tant un valor fals de pocs KB amb
        //   l'ordinador molt carregat, també en un bucle que només copia memòria (comprovat a la fase 1, pas 7a).
        // Un codi que creés objectes de debò ho faria a totes les voltes: n'hi ha prou que alguna mesuri exactament 0.
        for (int round = 0; round < 6; round++)
        {
            var voices = Enumerable.Range(0, 4).Select(_ => mixer.AcquireVoice()!).ToArray();
            long start = mixer.FramesRendered;
            foreach (var v in voices)
            {
                Fill(v, Rate * 4, 0.1f, finished: false); // crea objectes, però fora de la mesura
                v.Start(start, fadeInFrames: 4800);
                v.Stop(start + Rate * 2, fadeFrames: 4800);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++) // 3 s: fos d'entrada, so, fos de sortida i final
            {
                if (i == 50) voices[1].Gain = 0.5f;
                mixer.Read(bytes);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (round > 0) allocatedPerRound.Add(allocated);

            Assert.All(voices, v => Assert.Equal(VoiceState.Finished, v.State));
            foreach (var v in voices) Assert.True(mixer.ReleaseVoice(v));
        }

        Assert.True(allocatedPerRound.Min() == 0, $"Bytes creats a cada volta: {string.Join(", ", allocatedPerRound)}");
    }
}
