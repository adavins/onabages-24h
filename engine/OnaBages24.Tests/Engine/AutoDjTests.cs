using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>AutoDJ amb rotacions (fase 1, pas 8). Les pistes no cal que existeixin, excepte a la prova que sona de veritat.</summary>
public class AutoDjTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Mixer _mixer = new(48000);
    private readonly QueuePlayer _queue;
    private readonly long _music;
    private int _next;

    public AutoDjTests()
    {
        _db = Database.Open(_temp.File("autodj.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _queue = new QueuePlayer(_mixer);
        _music = _library.EnsureCategory("Music");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Sub(string name, long? category = null) => _library.EnsureSubcategory(category ?? _music, name);

    private long AddTrack(long subcategory, string artist, string? title = null, Func<Track, Track>? change = null)
    {
        var track = new Track
        {
            Path = _temp.File($"pista{++_next}.mp3"),
            SubcategoryId = subcategory,
            Artist = artist,
            Title = title ?? $"Cançó {_next}",
            DurationMs = 200_000, StartMs = 0, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
        };
        if (change is not null) track = change(track);
        return _library.AddTrack(track);
    }

    private static RotationStep Step(long subcategory, RotationSelection selection = RotationSelection.LeastRecent, bool repeatRule = true, long? tag = null) =>
        new() { SubcategoryId = subcategory, Selection = selection, RepeatRule = repeatRule, TagId = tag };

    private AutoDj NewAutoDj(int seed = 1, int minUpcoming = 2) =>
        new(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = minUpcoming }, random: new Random(seed));

    /// <summary>Fa «sonar» la primera pista de la cua (la treu i n'anota l'historial) i deixa que l'AutoDJ torni a omplir-la.</summary>
    private QueueItem PlayFirst(AutoDj autoDj, DateTimeOffset when)
    {
        autoDj.Tick();
        autoDj.Tick();
        var first = _queue.Upcoming[0];
        _queue.Remove(first.Id);
        if (first.TrackId is { } id) _library.AddHistory(_library.GetTrack(id)!, when, QueueOrigin.AutoDj);
        return first;
    }

    [Fact]
    public void Segueix_els_passos_de_la_rotacio_en_cicle()
    {
        long a = Sub("Adult 5"), b = Sub("Adult 9"), c = Sub("Català");
        for (int i = 0; i < 4; i++) { AddTrack(a, $"A{i}"); AddTrack(b, $"B{i}"); AddTrack(c, $"C{i}"); }
        long rotation = _rotations.Save("OnaBages", [Step(a), Step(b), Step(c)]);
        var autoDj = NewAutoDj();
        Assert.True(autoDj.Configure(true, rotation));

        var subs = new List<long?>();
        var time = DateTimeOffset.UtcNow;
        for (int i = 0; i < 7; i++)
            subs.Add(_library.GetTrack(PlayFirst(autoDj, time.AddMinutes(i)).TrackId!.Value)!.SubcategoryId);

        Assert.Equal([a, b, c, a, b, c, a], subs);
        Assert.All(_queue.Upcoming, i => Assert.Equal(QueueOrigin.AutoDj, i.Origin));
        Assert.Null(autoDj.Problem);
    }

    [Fact]
    public void Omple_la_cua_fins_al_minim_i_prou()
    {
        long a = Sub("Adult 5");
        for (int i = 0; i < 5; i++) AddTrack(a, $"A{i}");
        var autoDj = NewAutoDj(minUpcoming: 2);
        autoDj.Configure(true, _rotations.Save("R", [Step(a)]));

        for (int i = 0; i < 10; i++) autoDj.Tick();

        Assert.Equal(2, _queue.Upcoming.Count);
    }

    [Fact]
    public void Desactivat_no_afegeix_res()
    {
        long a = Sub("Adult 5");
        AddTrack(a, "A");
        var autoDj = NewAutoDj();
        autoDj.Configure(false, _rotations.Save("R", [Step(a)]));

        autoDj.Tick();

        Assert.Empty(_queue.Upcoming);
    }

    [Fact]
    public void La_menys_reproduida_recentment_primer_les_que_no_han_sonat_mai()
    {
        long a = Sub("Adult 5");
        long old = AddTrack(a, "Vella"), recent = AddTrack(a, "Recent"), never = AddTrack(a, "Mai");
        var now = DateTimeOffset.UtcNow;
        _library.AddHistory(_library.GetTrack(old)!, now.AddHours(-5), QueueOrigin.AutoDj);
        _library.AddHistory(_library.GetTrack(recent)!, now.AddHours(-1), QueueOrigin.AutoDj);
        var autoDj = NewAutoDj(minUpcoming: 3);
        autoDj.Configure(true, _rotations.Save("R", [Step(a)]));

        for (int i = 0; i < 3; i++) autoDj.Tick();

        Assert.Equal([never, old, recent], _queue.Upcoming.Select(i => i.TrackId!.Value));
    }

    [Fact]
    public void La_menys_reproduida_tria_la_que_ha_sonat_menys_vegades()
    {
        long a = Sub("Dance90");
        long many = AddTrack(a, "Molt", change: t => t with { PlayCount = 30 });
        long few = AddTrack(a, "Poc", change: t => t with { PlayCount = 2, LastPlayed = DateTimeOffset.UtcNow });
        long some = AddTrack(a, "Força", change: t => t with { PlayCount = 10, LastPlayed = DateTimeOffset.UtcNow.AddDays(-30) });
        var autoDj = NewAutoDj(minUpcoming: 3);
        autoDj.Configure(true, _rotations.Save("R", [Step(a, RotationSelection.LeastPlayed)]));

        for (int i = 0; i < 3; i++) autoDj.Tick();

        // Per nombre de reproduccions, encara que «Poc» acabi de sonar
        Assert.Equal([few, some, many], _queue.Upcoming.Select(i => i.TrackId!.Value));
    }

    [Fact]
    public void No_repeteix_pista_ni_artista_de_la_cua_si_pot()
    {
        long a = Sub("Adult 5");
        AddTrack(a, "Txarango"); AddTrack(a, "Txarango"); AddTrack(a, "Txarango");
        AddTrack(a, "Manel");
        long rotation = _rotations.Save("R", [Step(a, RotationSelection.Random)]);

        // Amb diverses llavors: les dues de la cua sempre són d'artistes diferents
        for (int seed = 0; seed < 20; seed++)
        {
            _queue.RemoveWhere(_ => true);
            var autoDj = NewAutoDj(seed);
            autoDj.Configure(true, rotation);
            autoDj.Tick();
            autoDj.Tick();
            var artists = _queue.Upcoming.Select(i => i.Artist).ToList();
            Assert.Equal(2, artists.Count);
            Assert.NotEqual(artists[0], artists[1]);
        }
    }

    [Fact]
    public void Si_nomes_hi_ha_un_artista_afluixa_la_regla_pero_no_repeteix_pista()
    {
        long a = Sub("Adult 5");
        AddTrack(a, "Txarango"); AddTrack(a, "Txarango");
        var autoDj = NewAutoDj();
        autoDj.Configure(true, _rotations.Save("R", [Step(a)]));

        autoDj.Tick();
        autoDj.Tick();

        var ids = _queue.Upcoming.Select(i => i.TrackId).ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public void Nomes_tria_pistes_disponibles_avui_i_amb_l_etiqueta_del_pas()
    {
        long sweepers = Sub("Ona Bages", _library.EnsureCategory("Sweepers"));
        var today = DateOnly.FromDateTime(DateTime.Now);
        long good = AddTrack(sweepers, "SW català");
        _library.SetTags(good, ["SW - catala"]);
        foreach (var change in new Func<Track, Track>[]
                 {
                     t => t with { Enabled = false },
                     t => t with { FileState = FileState.Missing },
                     t => t with { ValidUntil = today.AddDays(-1) },
                     t => t with { ValidFrom = today.AddDays(1) },
                     t => t with { Type = TrackType.Stream },
                 })
        {
            long bad = AddTrack(sweepers, "No", change: change);
            _library.SetTags(bad, ["SW - catala"]);
        }
        AddTrack(sweepers, "SW general"); // sense l'etiqueta
        long tag = _library.EnsureTag("SW - catala");
        var autoDj = NewAutoDj(minUpcoming: 1);
        autoDj.Configure(true, _rotations.Save("R", [Step(sweepers, RotationSelection.Random, repeatRule: false, tag: tag)]));

        for (int i = 0; i < 5; i++)
        {
            autoDj.Tick();
            Assert.Equal(good, _queue.Upcoming.Single().TrackId);
            _queue.RemoveWhere(_ => true);
        }
    }

    [Fact]
    public void Prefereix_les_pistes_que_ja_tenen_punts()
    {
        long a = Sub("Adult 5");
        AddTrack(a, "Sense punts", change: t => t with { MixMs = null, StartMs = null, EndMs = null, CueSource = null });
        long withCues = AddTrack(a, "Amb punts");
        var autoDj = NewAutoDj(minUpcoming: 1);
        autoDj.Configure(true, _rotations.Save("R", [Step(a, RotationSelection.Random)]));

        autoDj.Tick();

        Assert.Equal(withCues, _queue.Upcoming.Single().TrackId);
    }

    [Fact]
    public void Un_pas_sense_pistes_se_salta_amb_un_avis()
    {
        long empty = Sub("Buida"), a = Sub("Adult 5");
        AddTrack(a, "A");
        var autoDj = NewAutoDj(minUpcoming: 1);
        autoDj.Configure(true, _rotations.Save("R", [Step(empty), Step(a)]));

        autoDj.Tick();

        Assert.Single(_queue.Upcoming);
        Assert.Contains("Buida", autoDj.Problem);
    }

    [Fact]
    public void Sense_res_a_la_rotacio_sona_musica_de_tota_la_biblioteca()
    {
        long empty = Sub("Buida"), other = Sub("Una altra bossa");
        long song = AddTrack(other, "A");
        var autoDj = NewAutoDj(minUpcoming: 1);
        autoDj.Configure(true, _rotations.Save("R", [Step(empty)]));

        autoDj.Tick();

        Assert.Equal(song, _queue.Upcoming.Single().TrackId);
        Assert.Contains("tota la biblioteca", autoDj.Problem);
    }

    [Fact]
    public void Sense_cap_rotacio_tambe_sona_musica()
    {
        long song = AddTrack(Sub("Adult 5"), "A");
        var autoDj = NewAutoDj(minUpcoming: 1);
        Assert.True(autoDj.Configure(true, null));

        autoDj.Tick();

        Assert.Null(autoDj.Rotation);
        Assert.Equal(song, _queue.Upcoming.Single().TrackId);
        Assert.Contains("No hi ha cap rotació", autoDj.Problem);
    }

    [Fact]
    public void Biblioteca_buida_avisa_i_no_afegeix_res()
    {
        var autoDj = NewAutoDj();
        autoDj.Configure(true, null);

        autoDj.Tick();

        Assert.Empty(_queue.Upcoming);
        Assert.Contains("no pot afegir res", autoDj.Problem);
    }

    [Fact]
    public void Una_rotacio_que_no_existeix_no_canvia_res()
    {
        long a = Sub("Adult 5");
        long rotation = _rotations.Save("R", [Step(a)]);
        var autoDj = NewAutoDj();
        autoDj.Configure(true, rotation);

        Assert.False(autoDj.Configure(true, 999));
        Assert.Equal(rotation, autoDj.Rotation!.Id);
    }

    /// <summary>
    /// De cap a cap, sense targeta: AutoDJ + cua + historial amb fitxers WAV reals d'1 s. La cua comença buida i l'AutoDJ
    /// l'omple sol (arrencada automàtica); cada pista que sona queda a l'historial.
    /// </summary>
    [Fact]
    public void Arrenca_sol_i_sona_seguit_anotant_l_historial()
    {
        long a = Sub("Adult 5");
        for (int i = 0; i < 4; i++)
        {
            string path = _temp.File($"to{i}.wav");
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                var samples = Enumerable.Repeat(0.1f * (i + 1), 48000 * 2).ToArray();
                writer.WriteSamples(samples, 0, samples.Length);
            }
            _library.AddTrack(new Track { Path = path, SubcategoryId = a, Artist = $"Artista {i}", Title = $"To {i}", StartMs = 0, MixMs = 1000, EndMs = 1000 });
        }
        var autoDj = NewAutoDj();
        autoDj.Configure(true, _rotations.Save("R", [Step(a)]));
        _ = new PlayHistory(_queue, _library, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var started = new List<QueueStarted>();
        _queue.Started += started.Add;

        // 5,5 s de so, en blocs de 10 ms: a cada bloc, el fil de control (AutoDJ i cua) i després el mesclador
        var bytes = new byte[480 * 2 * sizeof(float)];
        int silentBlocks = 0;
        bool sounding = false;
        for (int block = 0; block < 550; block++)
        {
            autoDj.Tick();
            _queue.Tick();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (_queue.IsPreparing && DateTime.UtcNow < until) { Thread.Sleep(1); _queue.Tick(); }
            _mixer.Read(bytes);
            // Un cop ha començat a sonar, cap bloc no pot ser silenci (els primers ho són: el marge d'inici)
            bool silent = MaxLevel(bytes) < 0.05f;
            if (!silent) sounding = true;
            else if (sounding) silentBlocks++;
        }
        _queue.Tick();

        Assert.True(started.Count >= 5, $"Han sonat {started.Count} pistes");
        Assert.Equal(0, silentBlocks);
        var history = _library.History(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(started.Count, history.Count);
        Assert.All(history, h => Assert.Equal(QueueOrigin.AutoDj, h.Origin));
        Assert.True(history.Count(h => h.Complete == true) >= 4);
        // Les 4 pistes han sonat abans que se'n repetís cap (la menys reproduïda recentment)
        Assert.Equal(4, history.Take(4).Select(h => h.TrackId).Distinct().Count());
    }

    private static float MaxLevel(byte[] bytes)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        float max = 0;
        foreach (float s in samples) max = Math.Max(max, Math.Abs(s));
        return max;
    }
}

/// <summary>Ordres de l'AutoDJ i de les rotacions (docs/protocol.md, 4.5).</summary>
public class AutoDjCommandsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly AutoDj _autoDj;
    private readonly CommandRouter _router;
    private readonly long _first, _second;

    public AutoDjCommandsTests()
    {
        _db = Database.Open(_temp.File("ordres.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Adult 5");
        long tag = _library.EnsureTag("SW - catala");
        _first = _rotations.Save("OnaBages", [new RotationStep { SubcategoryId = sub }, new RotationStep { SubcategoryId = sub, TagId = tag, Selection = RotationSelection.Random, RepeatRule = false }]);
        _second = _rotations.Save("Dance90", [new RotationStep { SubcategoryId = sub }]);
        _autoDj = new AutoDj(_library, _rotations, _queue);
        _autoDj.Configure(true, null);
        _router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: _autoDj, rotations: _rotations);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(string json) => _router.Handle(json);

    [Fact]
    public void Autodj_get_diu_l_estat_i_la_rotacio()
    {
        var response = Send("""{ "v": 1, "id": 1, "cmd": "autodj.get" }""");

        Assert.True(response["ok"]!.GetValue<bool>());
        var autodj = response["result"]!["autodj"]!;
        Assert.True(autodj["enabled"]!.GetValue<bool>());
        Assert.Equal("OnaBages", autodj["rotation"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Autodj_set_canvia_la_rotacio_i_ho_desa()
    {
        var response = Send($$"""{ "v": 1, "id": 1, "cmd": "autodj.set", "rotationId": {{_second}} }""");

        Assert.True(response["ok"]!.GetValue<bool>());
        Assert.Equal("Dance90", _autoDj.Rotation!.Name);
        Assert.True(_autoDj.Enabled); // no s'ha enviat «enabled»: es queda com estava
        Assert.Equal($$"""{"enabled":true,"rotationId":{{_second}}}""", _library.GetSetting(CommandRouter.AutoDjSettingKey));

        Send("""{ "v": 1, "id": 2, "cmd": "autodj.set", "enabled": false }""");
        Assert.False(_autoDj.Enabled);
        Assert.Equal("Dance90", _autoDj.Rotation!.Name);
    }

    [Theory]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "autodj.set", "rotationId": 999 }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "autodj.set", "enabled": "sí" }""")]
    [InlineData("""{ "v": 1, "id": 1, "cmd": "autodj.set", "rotationId": "OnaBages" }""")]
    public void Autodj_set_amb_parametres_dolents_dona_bad_params(string json)
    {
        var response = Send(json);

        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal("bad_params", response["error"]!["code"]!.GetValue<string>());
        Assert.Equal(_first, _autoDj.Rotation!.Id);
        Assert.True(_autoDj.Enabled);
    }

    [Fact]
    public void Rotations_list_dona_els_passos()
    {
        var response = Send("""{ "v": 1, "id": 1, "cmd": "rotations.list" }""");

        var rotations = response["result"]!["rotations"]!.AsArray();
        Assert.Equal(2, rotations.Count);
        var steps = rotations[0]!["steps"]!.AsArray();
        Assert.Equal(2, steps.Count);
        Assert.Equal("Music", steps[0]!["category"]!.GetValue<string>());
        Assert.Equal("Adult 5", steps[0]!["subcategory"]!.GetValue<string>());
        Assert.Equal("leastRecent", steps[0]!["selection"]!.GetValue<string>());
        Assert.Equal("SW - catala", steps[1]!["tag"]!.GetValue<string>());
        Assert.Equal("random", steps[1]!["selection"]!.GetValue<string>());
        Assert.False(steps[1]!["repeatRule"]!.GetValue<bool>());
    }
}
