using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Separació real i previsualització de les rotacions (fase 3, pas 8b; D-095).</summary>
public sealed class SeparationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly DateTimeOffset _now = new(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);
    private readonly long _sub;
    private int _next;

    public SeparationTests()
    {
        _db = Database.Open(_temp.File("separacio.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _sub = _library.EnsureSubcategory(_library.EnsureCategory("Music"), "Adult 5");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Track(string artist, TimeSpan? playedAgo = null) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), SubcategoryId = _sub, Artist = artist, Title = $"Cançó {_next}",
        DurationMs = 200_000, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
        LastPlayed = playedAgo is { } ago ? _now - ago : null,
    });

    private AutoDj NewAutoDj(int minUpcoming = 1)
    {
        var autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = minUpcoming }, clock: () => _now, random: new Random(3));
        autoDj.Configure(true, _rotations.Save("R", [new RotationStep { SubcategoryId = _sub }]), "a mà");
        return autoDj;
    }

    /// <summary>Anota a l'historial que una pista ha sonat fa <paramref name="ago"/> (per a la separació d'artista).</summary>
    private void Played(long trackId, TimeSpan ago) => _library.AddHistory(_library.GetTrack(trackId)!, _now - ago, QueueOrigin.AutoDj);

    [Fact]
    public void Una_canco_que_ha_sonat_fa_menys_de_6_hores_no_es_tria_si_n_hi_ha_d_altres()
    {
        Track("A", TimeSpan.FromHours(2));             // fa 2 h: no
        Track("B", TimeSpan.FromHours(1));             // fa 1 h: no
        long rested = Track("C", TimeSpan.FromHours(7)); // fa 7 h: sí
        var autoDj = NewAutoDj();
        autoDj.Separation = new SeparationRules(TimeSpan.FromHours(6), TimeSpan.Zero);

        autoDj.Tick();

        Assert.Equal(rested, _queue.Upcoming[0].TrackId);
    }

    [Fact]
    public void Un_artista_que_ha_sonat_fa_menys_d_una_hora_no_es_tria_si_n_hi_ha_d_altres()
    {
        Track("Txarango", TimeSpan.FromDays(10)); // la que fa més temps que no sona, però l'artista ha sonat fa 20 min
        long other = Track("Manel", TimeSpan.FromDays(9));
        long played = Track("Txarango", TimeSpan.FromDays(8));
        Played(played, TimeSpan.FromMinutes(20));
        var autoDj = NewAutoDj();

        autoDj.Tick();

        Assert.Equal(other, _queue.Upcoming[0].TrackId);
    }

    [Fact]
    public void Si_no_n_hi_ha_prou_s_afluixa_i_mai_no_es_queda_sense_res()
    {
        // Totes han sonat fa poc i són del mateix artista, que acaba de sonar
        long a = Track("Txarango", TimeSpan.FromMinutes(30));
        long b = Track("Txarango", TimeSpan.FromMinutes(90));
        Played(a, TimeSpan.FromMinutes(30));
        var autoDj = NewAutoDj();

        var preview = autoDj.Preview(autoDj.Rotation!, 1);
        Assert.Equal(b, preview[0].Track!.Id); // la que fa més temps
        Assert.Equal("separació de cançó", preview[0].Relaxed); // s'han afluixat les dues separacions
        autoDj.Tick();
        Assert.Single(_queue.Upcoming);
    }

    [Fact]
    public void Sense_separacio_de_temps_nomes_compta_la_cua()
    {
        Track("A", TimeSpan.FromMinutes(5));
        Track("B", TimeSpan.FromHours(1));
        var autoDj = NewAutoDj();
        autoDj.Separation = new SeparationRules(TimeSpan.Zero, TimeSpan.Zero);

        var preview = autoDj.Preview(autoDj.Rotation!, 2);

        Assert.Null(preview[0].Relaxed); // sense separació, les dues es poden triar sense afluixar res
        Assert.Null(preview[1].Relaxed);
    }

    [Fact]
    public void La_previsualitzacio_no_canvia_ni_la_cua_ni_el_pas()
    {
        for (int i = 0; i < 5; i++) Track($"Artista {i}", TimeSpan.FromDays(i + 1));
        var autoDj = NewAutoDj();
        int stepBefore = autoDj.NextStep;

        var preview = autoDj.Preview(autoDj.Rotation!, 5);

        Assert.Equal(5, preview.Count);
        Assert.Equal(5, preview.Select(p => p.Track!.Id).Distinct().Count()); // cap repetida dins la previsualització
        Assert.Empty(_queue.Upcoming);
        Assert.Equal(stepBefore, autoDj.NextStep);
        Assert.Equal(0, preview[0].StepIndex);
    }

    [Fact]
    public void Ordres_separation_i_rotations_preview()
    {
        for (int i = 0; i < 3; i++) Track($"Artista {i}", TimeSpan.FromDays(i + 1));
        var autoDj = NewAutoDj();
        var router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: autoDj, rotations: _rotations);
        JsonObject Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }

        var got = Send(new() { ["cmd"] = "separation.get" })["result"]!;
        Assert.Equal((360, 60), (got["trackMinutes"]!.GetValue<int>(), got["artistMinutes"]!.GetValue<int>()));

        Send(new() { ["cmd"] = "separation.set", ["trackMinutes"] = 180 });
        Assert.Equal(TimeSpan.FromHours(3), autoDj.Separation.Track);
        Assert.Equal(TimeSpan.FromHours(1), autoDj.Separation.Artist);
        Assert.Equal(TimeSpan.FromHours(3), CommandRouter.ReadSeparation(_library).Track); // desada
        Assert.Equal("bad_params", Send(new() { ["cmd"] = "separation.set", ["artistMinutes"] = -1 })["error"]!["code"]!.GetValue<string>());

        var preview = Send(new() { ["cmd"] = "rotations.preview", ["rotationId"] = autoDj.Rotation!.Id, ["count"] = 3 })["result"]!;
        var items = preview["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal("Music / Adult 5", items[0]!["step"]!.GetValue<string>());
        Assert.NotNull(items[0]!["track"]!["title"]);
    }
}
