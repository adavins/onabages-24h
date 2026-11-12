using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Cua de l'AutoDJ: uns 30 minuts per davant i es torna a triar quan canvia el que tria (fase 3, pas 8c; D-097).</summary>
public sealed class FillAheadTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Mixer _mixer = new(48000);
    private readonly QueuePlayer _queue;
    private readonly AutoDj _autoDj;
    private readonly long _a, _b;
    private int _next;

    public FillAheadTests()
    {
        _queue = new QueuePlayer(_mixer);
        _db = Database.Open(_temp.File("cua.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        long music = _library.EnsureCategory("Music");
        _a = _library.EnsureSubcategory(music, "Adult 5");
        _b = _library.EnsureSubcategory(music, "Adult 6");
        for (int i = 0; i < 30; i++) { Track(_a); Track(_b); }
        _autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { FillAhead = TimeSpan.FromMinutes(30), MaxUpcoming = 20 },
            random: new Random(5));
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    /// <summary>Una pista de 200 s (3:20).</summary>
    private long Track(long sub) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), SubcategoryId = sub, Artist = $"Artista {_next}", Title = $"Cançó {_next}",
        DurationMs = 200_000, StartMs = 0, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
    });

    private void Fill()
    {
        for (int i = 0; i < 40; i++) _autoDj.Tick();
    }

    private IEnumerable<long?> Subs() => _queue.Upcoming.Select(i => _library.GetTrack(i.TrackId!.Value)!.SubcategoryId);

    [Fact]
    public void Omple_la_cua_fins_a_uns_30_minuts()
    {
        _autoDj.Configure(true, _rotations.Create("A", [new RotationStep { SubcategoryId = _a }]), "a mà");
        Fill();
        Assert.Equal(9, _queue.Upcoming.Count); // 9 × 3:20 = 30 minuts
    }

    [Fact]
    public void Canviar_de_rotacio_torna_a_triar_les_de_l_AutoDJ_pero_no_les_manuals()
    {
        _autoDj.Configure(true, _rotations.Create("A", [new RotationStep { SubcategoryId = _a }]), "a mà");
        Fill();
        var manual = QueueItem.FromTrack(_library.TracksInSubcategory(_a)[^1], QueueOrigin.Manual);
        _queue.Enqueue(manual);

        _autoDj.Configure(true, _rotations.Create("B", [new RotationStep { SubcategoryId = _b }]), "a mà");
        Fill();

        // Aquí no sona res: cap no s'està preparant per sonar, i totes les de l'AutoDJ són de la rotació nova
        Assert.Contains(_queue.Upcoming, i => i.Id == manual.Id); // la manual es queda
        Assert.All(_queue.Upcoming.Where(i => i.Origin == QueueOrigin.AutoDj),
            i => Assert.Equal(_b, _library.GetTrack(i.TrackId!.Value)!.SubcategoryId));
    }

    [Fact]
    public void Canviar_una_bossa_es_nota_de_seguida_a_la_cua()
    {
        long bag = _rotations.CreateBag("Generalista A", _a, "a mà", DateTimeOffset.UtcNow);
        _autoDj.Configure(true, _rotations.Create("R", [new RotationStep { BagId = bag }]), "a mà");
        Fill();
        Assert.All(Subs(), s => Assert.Equal(_a, s));

        _autoDj.SetBag(bag, _b, "a mà");
        Fill();

        Assert.All(Subs(), s => Assert.Equal(_b, s)); // cap no s'estava preparant per sonar: totes, de la bossa nova
    }

    [Fact]
    public void La_que_ja_s_esta_preparant_per_sonar_no_es_canvia()
    {
        // Fitxers de debò (3 s): la primera sona i la següent es comença a preparar de seguida
        long Real(long sub)
        {
            string path = _temp.File($"real{++_next}.wav");
            using (var writer = new NAudio.Wave.WaveFileWriter(path, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = new float[3 * 48000 * 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = 0.2f;
                writer.WriteSamples(samples, 0, samples.Length);
            }
            return _library.AddTrack(new Track { Path = path, SubcategoryId = sub, Artist = $"Real {_next}", Title = $"Real {_next}",
                DurationMs = 3000, StartMs = 0, MixMs = 2900, EndMs = 3000, CueSource = CueSource.Analyzer });
        }
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Reals");
        long other = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Altres reals");
        for (int i = 0; i < 4; i++) { Real(sub); Real(other); }
        long bag = _rotations.CreateBag("B", sub, "a mà", DateTimeOffset.UtcNow);
        var autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = 3 });
        autoDj.Configure(true, _rotations.Create("R", [new RotationStep { BagId = bag }]), "a mà");
        for (int i = 0; i < 5; i++) autoDj.Tick();

        // Fa sonar la primera fins que la següent es comença a preparar
        var bytes = new byte[480 * 2 * sizeof(float)];
        for (int i = 0; i < 400 && _queue.NextInPreparation is null; i++)
        {
            _queue.Tick();
            _mixer.Read(bytes);
            Thread.Sleep(1); // el fitxer s'obre en un altre fil
        }
        var preparing = _queue.NextInPreparation;
        Assert.NotNull(preparing);

        autoDj.SetBag(bag, other, "a mà");

        Assert.Equal(preparing, _queue.Upcoming[0].Id); // la que es prepara es queda
        Assert.All(_queue.Upcoming.Skip(1).Where(i => i.Origin == QueueOrigin.AutoDj),
            i => Assert.Equal(other, _library.GetTrack(i.TrackId!.Value)!.SubcategoryId));
    }

    [Fact]
    public void Regenera_la_cua_continua_pel_pas_on_era_encara_que_se_n_hagin_saltat()
    {
        // Un pas sense pistes (com uns jingles buits) se salta: 9 pistes no són 9 passos
        long empty = _library.EnsureSubcategory(_library.EnsureCategory("Jingles"), "Buits");
        _autoDj.Configure(true, _rotations.Create("R", [
            new RotationStep { SubcategoryId = _a },
            new RotationStep { SubcategoryId = _b },
            new RotationStep { SubcategoryId = empty },
        ]), "a mà");
        Fill();
        var before = Subs().ToList();
        Assert.Equal(_a, before[0]);

        _autoDj.Regenerate();
        Fill();

        Assert.Equal(before, Subs()); // la primera torna a ser del primer pas, i l'ordre de passos és el mateix
    }

    [Fact]
    public void L_avis_del_pas_buit_es_mante_encara_que_l_ultima_tria_no_l_hagi_saltat()
    {
        long empty = _library.EnsureSubcategory(_library.EnsureCategory("Jingles"), "Buits");
        _autoDj.Configure(true, _rotations.Create("R", [
            new RotationStep { SubcategoryId = _a },
            new RotationStep { SubcategoryId = empty },
            new RotationStep { SubcategoryId = _b },
            new RotationStep { SubcategoryId = _a },
        ]), "a mà");

        // Es fa «Regenera la cua» diverses vegades: s'acabi on s'acabi la tria, l'avís hi ha de ser
        for (int round = 0; round < 6; round++)
        {
            _autoDj.Regenerate();
            Fill();
            Assert.Contains("Buits", _autoDj.Problem);
            Assert.Contains("sense pistes disponibles", _autoDj.Problem);
        }

        // Quan el pas torna a tenir pistes, l'avís marxa
        Track(empty);
        _autoDj.Regenerate();
        Fill();
        Assert.Null(_autoDj.Problem);
    }

    [Fact]
    public void Desar_una_rotacio_que_no_es_l_activa_no_toca_la_cua()
    {
        _autoDj.Configure(true, _rotations.Create("A", [new RotationStep { SubcategoryId = _a }]), "a mà");
        long other = _rotations.Create("Altra", [new RotationStep { SubcategoryId = _a }]);
        Fill();
        var before = _queue.Upcoming.Select(i => i.Id).ToList();

        _rotations.Update(other, "Altra", [new RotationStep { SubcategoryId = _b }]);
        _autoDj.RotationsEdited();

        Assert.Equal(before, _queue.Upcoming.Select(i => i.Id));
    }
}
