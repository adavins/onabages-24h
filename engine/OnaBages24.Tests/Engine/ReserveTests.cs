using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Reserva de l'AutoDJ (fase 3, pas 8d; D-099): si cap pas de la rotació no troba res, la rotació de reserva i, si tampoc,
/// tota la biblioteca sense les subcategories «no de reserva». Sempre amb avís; mai silenci.
/// </summary>
public sealed class ReserveTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly long _empty, _adult, _dance, _nadal;
    private int _next;

    public ReserveTests()
    {
        _db = Database.Open(_temp.File("reserva.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        long music = _library.EnsureCategory("Music");
        _empty = _library.EnsureSubcategory(music, "Dance90");  // buida, com a la prova de la 10c
        _adult = _library.EnsureSubcategory(music, "Adult 5");
        _dance = _library.EnsureSubcategory(music, "Dance");
        _nadal = _library.EnsureSubcategory(music, "Nadal");
        for (int i = 0; i < 5; i++) { Track(_adult); Track(_dance); Track(_nadal); }
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Track(long sub) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), SubcategoryId = sub, Artist = $"Artista {_next}", Title = $"Cançó {_next}",
        DurationMs = 200_000, MixMs = 195_000, EndMs = 200_000, CueSource = CueSource.Analyzer,
    });

    /// <summary>Un AutoDJ amb una rotació que només té el pas buit, i omple la cua amb 5 pistes.</summary>
    private AutoDj FillWithEmptyRotation(ReserveSettings reserve)
    {
        var autoDj = new AutoDj(_library, _rotations, _queue, new AutoDjSettings { MinUpcoming = 5 }, random: new Random(7));
        autoDj.Reserve = reserve;
        autoDj.Configure(true, _rotations.Create("Dance90", [new RotationStep { SubcategoryId = _empty }]), "a mà");
        for (int i = 0; i < 10; i++) autoDj.Tick();
        return autoDj;
    }

    private long? Sub(QueueItem item) => _library.GetTrack(item.TrackId!.Value)!.SubcategoryId;

    [Fact]
    public void Amb_rotacio_de_reserva_sona_la_reserva_i_es_diu()
    {
        long reserve = _rotations.Create("Reserva", [new RotationStep { SubcategoryId = _dance }]);
        var autoDj = FillWithEmptyRotation(new ReserveSettings(reserve, new HashSet<long>()));

        Assert.Equal(5, _queue.Upcoming.Count);
        Assert.All(_queue.Upcoming, i =>
        {
            Assert.Equal(_dance, Sub(i));
            Assert.Null(i.RotationStep); // no és un pas de la rotació activa
            Assert.StartsWith("Reserva: rotació «Reserva»", i.RotationStepName);
        });
        Assert.Contains("sona la rotació de reserva «Reserva»", autoDj.Problem);
    }

    [Fact]
    public void Sense_rotacio_de_reserva_tota_la_biblioteca_sense_les_no_de_reserva()
    {
        var autoDj = FillWithEmptyRotation(new ReserveSettings(null, new HashSet<long> { _nadal }));

        Assert.Equal(5, _queue.Upcoming.Count);
        Assert.All(_queue.Upcoming, i =>
        {
            Assert.NotEqual(_nadal, Sub(i));
            Assert.Equal("Reserva: tota la biblioteca", i.RotationStepName);
        });
        Assert.Contains("sense les subcategories «no de reserva»", autoDj.Problem);
    }

    [Fact]
    public void Si_la_rotacio_de_reserva_tampoc_te_res_tota_la_biblioteca()
    {
        long reserve = _rotations.Create("Reserva buida", [new RotationStep { SubcategoryId = _empty }]);
        var autoDj = FillWithEmptyRotation(new ReserveSettings(reserve, new HashSet<long> { _nadal }));

        Assert.All(_queue.Upcoming, i => Assert.NotEqual(_nadal, Sub(i)));
        Assert.Contains("ni la rotació de reserva «Reserva buida»", autoDj.Problem);
        Assert.Contains("tota la biblioteca", autoDj.Problem);
    }

    [Fact]
    public void Mai_silenci_si_nomes_queden_les_no_de_reserva_sonen_aquestes()
    {
        var autoDj = FillWithEmptyRotation(new ReserveSettings(null, new HashSet<long> { _adult, _dance, _nadal }));

        Assert.Equal(5, _queue.Upcoming.Count);
        Assert.Contains("no n'hi ha cap altra", autoDj.Problem);
    }

    [Fact]
    public void Canviar_la_reserva_torna_a_triar_les_que_en_venien()
    {
        var autoDj = FillWithEmptyRotation(ReserveSettings.None);
        long reserve = _rotations.Create("Reserva", [new RotationStep { SubcategoryId = _dance }]);

        autoDj.Reserve = new ReserveSettings(reserve, new HashSet<long>());
        for (int i = 0; i < 10; i++) autoDj.Tick();

        Assert.All(_queue.Upcoming, i => Assert.Equal(_dance, Sub(i)));
    }

    [Fact]
    public void Ordres_reserve_es_desa_i_no_es_pot_esborrar_la_rotacio_de_reserva()
    {
        var autoDj = new AutoDj(_library, _rotations, _queue);
        autoDj.Configure(true, _rotations.Create("Activa", [new RotationStep { SubcategoryId = _adult }]), "a mà");
        long reserve = _rotations.Create("Reserva", [new RotationStep { SubcategoryId = _dance }]);
        var router = new CommandRouter(new EngineState(), () => { }, _library, queue: _queue, autoDj: autoDj, rotations: _rotations);
        JsonObject Send(JsonObject msg) { msg["v"] = 1; msg["id"] = 1; return router.Handle(msg.ToJsonString()); }

        var empty = Send(new() { ["cmd"] = "reserve.get" })["result"]!;
        Assert.Null(empty["rotationId"]);
        Assert.Empty(empty["excludedSubcategories"]!.AsArray());

        var set = Send(new() { ["cmd"] = "reserve.set", ["rotationId"] = reserve, ["excludedSubcategoryIds"] = new JsonArray(_nadal) })["result"]!;
        Assert.Equal("Reserva", set["rotationName"]!.GetValue<string>());
        Assert.Equal("Nadal", set["excludedSubcategories"]![0]!["name"]!.GetValue<string>());

        // Es desa (es manté si el motor es reinicia)
        var saved = CommandRouter.ReadReserve(_library);
        Assert.Equal(reserve, saved.RotationId);
        Assert.Equal([_nadal], saved.ExcludedSubcategoryIds);

        // Canviar només la rotació no toca les excloses
        Send(new() { ["cmd"] = "reserve.set", ["rotationId"] = null });
        Assert.Null(autoDj.Reserve.RotationId);
        Assert.Contains(_nadal, autoDj.Reserve.ExcludedSubcategoryIds);

        Send(new() { ["cmd"] = "reserve.set", ["rotationId"] = reserve });
        var refused = Send(new() { ["cmd"] = "rotations.delete", ["rotationId"] = reserve });
        Assert.Equal("bad_params", refused["error"]!["code"]!.GetValue<string>());
        Assert.Contains("rotació de reserva", refused["error"]!["message"]!.GetValue<string>());

        Assert.Equal("bad_params", Send(new() { ["cmd"] = "reserve.set", ["rotationId"] = 9999 })["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad_params", Send(new() { ["cmd"] = "reserve.set", ["excludedSubcategoryIds"] = new JsonArray(9999) })["error"]!["code"]!.GetValue<string>());
    }
}
