using Microsoft.Extensions.Logging.Abstractions;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Planificador bàsic: ENCUA i SISTEMA amb un rellotge fals (fase 2, pas 3).</summary>
public sealed class SchedulerTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Events _events;
    private readonly Mixer _mixer = new(48000);
    private readonly QueuePlayer _queue;
    private readonly AutoDj _autoDj;
    private readonly long _sub;
    private readonly List<long?> _remembered = [];
    private readonly List<ExecutionRecord> _fired = [];
    private DateTimeOffset _now = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private int _next;

    public SchedulerTests()
    {
        _db = Database.Open(_temp.File("planificador.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _events = new Events(_db);
        _queue = new QueuePlayer(_mixer);
        _autoDj = new AutoDj(_library, _rotations, _queue);
        _sub = _library.EnsureSubcategory(_library.EnsureCategory("Programes"), "Diaris");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Track(string title) => _library.AddTrack(new Track
    {
        Path = _temp.File($"p{++_next}.mp3"), SubcategoryId = _sub, Title = title, Type = TrackType.Program, DurationMs = 60_000,
    });

    private static EventAction Pista(long id) => new() { Type = EventActionType.Track, Params = $$"""{"songId":{{id}}}""" };

    private long Save(string name, int hour, int minute, EventMode mode = EventMode.Queue, Func<ScheduledEvent, ScheduledEvent>? change = null, params EventAction[] actions)
    {
        var ev = new ScheduledEvent { Name = name, Mode = mode, StartTime = new TimeOnly(hour, minute), Actions = actions };
        return _events.Save(change is null ? ev : change(ev));
    }

    private Scheduler NewScheduler(TimeZoneInfo? zone = null)
    {
        var executor = new EventExecutor(_library, _queue, _autoDj, id => _remembered.Add(id));
        var scheduler = new Scheduler(_events, executor, NullLogger.Instance, clock: () => _now, zone: zone ?? TimeZoneInfo.Utc);
        scheduler.Fired += r => _fired.Add(r);
        return scheduler;
    }

    /// <summary>Avança el rellotge fals de <paramref name="step"/> en <paramref name="step"/> fins a <paramref name="until"/>, amb una volta a cada pas.</summary>
    private void RunUntil(Scheduler scheduler, DateTimeOffset until, TimeSpan? step = null)
    {
        while (_now < until)
        {
            _now = _now + (step ?? TimeSpan.FromSeconds(1)) > until ? until : _now + (step ?? TimeSpan.FromSeconds(1));
            scheduler.Tick();
        }
    }

    private DateTimeOffset At(int hour, int minute, int second = 0) => new(2026, 11, 20, hour, minute, second, TimeSpan.Zero);

    private IReadOnlyList<ExecutionRecord> Log() => _events.Executions(At(0, 0), At(23, 59, 59));

    [Fact]
    public void Un_ENCUA_posa_el_contingut_al_davant_de_la_cua_a_l_hora_i_en_ordre()
    {
        var existing = QueueItem.FromTrack(_library.GetTrack(Track("Ja hi era"))!, QueueOrigin.AutoDj);
        _queue.Enqueue(existing);
        long promo = Track("Promo"), falca = Track("Falca");
        long id = Save("Mitja hora", 9, 30, actions: [Pista(promo), Pista(falca)]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 29, 59));
        Assert.Single(_queue.Upcoming); // encara no
        Assert.Empty(Log());

        RunUntil(scheduler, At(9, 30, 0));

        Assert.Equal(["Promo", "Falca", "Ja hi era"], _queue.Upcoming.Select(i => i.Title));
        Assert.All(_queue.Upcoming.Take(2), i => Assert.Equal(QueueOrigin.Event, i.Origin));
        Assert.Equal("Mitja hora", _queue.Upcoming[0].EventName);
        var record = Assert.Single(Log());
        Assert.Equal(id, record.EventId);
        Assert.Equal(ExecutionResult.Ok, record.Result);
        Assert.Equal(At(9, 30), record.ScheduledFor);
        Assert.Equal(At(9, 30), record.ExecutedAt);
        Assert.Contains("2 pistes a la cua", record.Detail);
        Assert.Single(_fired);
    }

    [Fact]
    public void Es_prepara_uns_30_segons_abans()
    {
        Save("Mitja hora", 9, 30, actions: [Pista(Track("Promo"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 29, 29));
        var pending = Assert.Single(scheduler.Pending);
        Assert.Equal(At(9, 30), pending.At);

        // Si la pista desapareix de la biblioteca després de preparar-lo, l'esdeveniment ja la tenia: sona igualment
        RunUntil(scheduler, At(9, 29, 31));
        _db.Command("DELETE FROM pista;").ExecuteNonQuery();
        RunUntil(scheduler, At(9, 30, 1));

        Assert.Equal(["Promo"], _queue.Upcoming.Select(i => i.Title));
        Assert.Empty(scheduler.Pending);
    }

    [Fact]
    public void No_es_dispara_dues_vegades_ni_despres_de_reiniciar_el_motor()
    {
        Save("Mitja hora", 9, 30, actions: [Pista(Track("Promo"))]);
        var scheduler = NewScheduler();
        RunUntil(scheduler, At(9, 30, 10));
        Assert.Single(_queue.Upcoming);

        // El motor es reinicia 20 s després: dins de la tolerància, però ja es va fer
        var restarted = NewScheduler();
        RunUntil(restarted, At(9, 30, 40));

        Assert.Single(_queue.Upcoming);
        Assert.Single(Log());
    }

    [Fact]
    public void Esborra_la_cua_si_ho_demana()
    {
        _queue.Enqueue(QueueItem.FromTrack(_library.GetTrack(Track("Vella 1"))!, QueueOrigin.AutoDj),
            QueueItem.FromTrack(_library.GetTrack(Track("Vella 2"))!, QueueOrigin.AutoDj));
        Save("Programa", 10, 0, change: e => e with { ClearQueue = true }, actions: [Pista(Track("Programa"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(10, 0));

        Assert.Equal(["Programa"], _queue.Upcoming.Select(i => i.Title));
        Assert.Contains("2 pistes tretes", Log()[0].Detail);
    }

    [Fact]
    public void Si_el_motor_arrenca_tard_dins_de_la_tolerancia_es_dispara_i_si_no_s_omet()
    {
        Save("A temps", 9, 0, actions: [Pista(Track("A"))]);                                         // tolerància 60 s
        Save("Massa tard", 8, 58, change: e => e with { ToleranceSeconds = 30 }, actions: [Pista(Track("B"))]);
        _now = At(9, 0, 45); // el motor arrenca ara
        var scheduler = NewScheduler();

        scheduler.Tick();

        Assert.Equal(["A"], _queue.Upcoming.Select(i => i.Title));
        var log = Log();
        Assert.Equal(ExecutionResult.SkippedLate, log.Single(r => r.Name == "Massa tard").Result);
        Assert.Contains("2 min 45 s tard", log.Single(r => r.Name == "Massa tard").Detail);
        var late = log.Single(r => r.Name == "A temps");
        Assert.Equal(ExecutionResult.Ok, late.Result);
        Assert.Equal(At(9, 0, 45), late.ExecutedAt);
    }

    [Fact]
    public void Si_dos_ENCUA_coincideixen_guanya_la_prioritat_mes_alta()
    {
        Save("Normal", 11, 0, actions: [Pista(Track("Normal"))]);
        Save("Important", 11, 0, change: e => e with { Priority = 70 }, actions: [Pista(Track("Important"))]);
        Save("Canvi de rotació", 11, 0, EventMode.System, actions: []);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(11, 0));

        Assert.Equal(["Important"], _queue.Upcoming.Select(i => i.Title));
        var log = Log();
        Assert.Equal(ExecutionResult.SkippedConflict, log.Single(r => r.Name == "Normal").Result);
        Assert.Contains("«Important»", log.Single(r => r.Name == "Normal").Detail);
        Assert.Equal(ExecutionResult.Ok, log.Single(r => r.Name == "Canvi de rotació").Result); // SISTEMA no entra en conflicte
    }

    [Fact]
    public void Un_SISTEMA_canvia_la_rotacio_i_atura_o_activa_l_AutoDJ()
    {
        long main = _rotations.Save("OnaBages", []);
        long night = _rotations.Save("Nit", []);
        _autoDj.Configure(true, main);
        Save("Rotació de nit", 9, 10, EventMode.System, actions:
            [new EventAction { Type = EventActionType.Rotation, Params = $$"""{"rotacioId":{{night}}}""" }]);
        Save("Atura l'AutoDJ", 9, 20, EventMode.System, actions:
            [new EventAction { Type = EventActionType.AutoDj, Params = """{"actiu":false}""" }]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 10));
        Assert.Equal("Nit", _autoDj.Rotation!.Name);
        Assert.True(_autoDj.Enabled);
        Assert.Equal([night], _remembered); // es desa, com amb autodj.set

        RunUntil(scheduler, At(9, 20), TimeSpan.FromSeconds(5));
        Assert.False(_autoDj.Enabled);
        Assert.Equal("Nit", _autoDj.Rotation!.Name);
        Assert.Contains("AutoDJ aturat", Log()[1].Detail);
    }

    [Fact]
    public void Una_rotacio_que_no_existeix_no_canvia_res()
    {
        long main = _rotations.Save("OnaBages", []);
        _autoDj.Configure(true, main);
        Save("Rotació", 9, 10, EventMode.System, actions: [new EventAction { Type = EventActionType.Rotation, Params = """{"rotacioId":999}""" }]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 10));

        Assert.Equal("OnaBages", _autoDj.Rotation!.Name);
        Assert.Empty(_remembered);
        Assert.Equal(ExecutionResult.Error, Log()[0].Result);
        Assert.Contains("999 no existeix", Log()[0].Detail);
    }

    [Fact]
    public void Una_pista_que_no_existeix_se_salta_amb_avis()
    {
        Save("Promos", 9, 30, actions: [Pista(12345), Pista(Track("Promo"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 30));

        Assert.Equal(["Promo"], _queue.Upcoming.Select(i => i.Title));
        Assert.Equal(ExecutionResult.Ok, Log()[0].Result);
        Assert.Contains("12345 no existeix", Log()[0].Detail);
    }

    [Fact]
    public void Els_desactivats_i_les_plantilles_no_es_disparen()
    {
        Save("Desactivat", 9, 30, change: e => e with { Enabled = false }, actions: [Pista(Track("A"))]);
        Save("Plantilla", 9, 30, change: e => e with { IsTemplate = true }, actions: [Pista(Track("B"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 31), TimeSpan.FromSeconds(5));

        Assert.Empty(_queue.Upcoming);
        Assert.Empty(Log());
    }

    [Fact]
    public void Un_esdeveniment_desat_mentre_funciona_es_te_en_compte_de_seguida()
    {
        var scheduler = NewScheduler();
        RunUntil(scheduler, At(9, 0, 10));

        // Un de nou per d'aquí a 20 s (abans del pròxim recàlcul de la finestra) i un altre amb una hora que ja ha passat
        Save("Nou", 9, 0, change: e => e with { StartTime = new TimeOnly(9, 0, 30) }, actions: [Pista(Track("Nou"))]);
        Save("Passat", 8, 30, actions: [Pista(Track("Passat"))]);
        scheduler.Reload();
        RunUntil(scheduler, At(9, 0, 31));

        Assert.Equal(["Nou"], _queue.Upcoming.Select(i => i.Title));
        Assert.Equal(["Nou"], Log().Select(r => r.Name)); // el passat no s'anota com a omès
    }

    [Fact]
    public void Una_hora_que_no_existeix_pel_canvi_d_hora_s_anota_i_no_sona()
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        Save("Nit", 2, 30, actions: [Pista(Track("Nit"))]);
        _now = new DateTimeOffset(2027, 3, 28, 0, 59, 0, TimeSpan.Zero); // 01:59 a Madrid
        var scheduler = NewScheduler(madrid);

        RunUntil(scheduler, new DateTimeOffset(2027, 3, 28, 1, 0, 1, TimeSpan.Zero), TimeSpan.FromSeconds(1)); // 03:00:01 d'estiu

        Assert.Empty(_queue.Upcoming);
        var record = Assert.Single(_events.Executions(new DateTimeOffset(2027, 3, 27, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 29, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(ExecutionResult.SkippedClockChange, record.Result);
        Assert.Contains("canvi d'hora", record.Detail);
    }

    [Fact]
    public void Un_TALLA_es_programa_2_s_abans_i_esborra_el_contingut_d_un_ENCUA_que_no_ha_sonat()
    {
        long encua = Save("Promos", 9, 59, actions: [Pista(Track("Promo 1")), Pista(Track("Promo 2"))]);
        long talla = Save("Programa", 10, 0, EventMode.Cut, change: e => e with { ClearQueue = true }, actions: [Pista(Track("Programa"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 59, 57));
        Assert.Equal(["Promo 1", "Promo 2"], _queue.Upcoming.Select(i => i.Title)); // el TALLA encara no
        Assert.Single(Log());

        RunUntil(scheduler, At(9, 59, 58)); // 2 s abans: es programa el tall

        Assert.Equal(["Programa"], _queue.Upcoming.Select(i => i.Title));
        var log = Log();
        var cut = log.Single(r => r.EventId == talla);
        Assert.Equal(ExecutionResult.Ok, cut.Result);
        Assert.Null(cut.ExecutedAt); // s'hi posarà quan comenci la primera pista
        Assert.Contains("tall", cut.Detail);
        Assert.Contains("2 pistes tretes", cut.Detail);
        // El contingut de l'ENCUA no ha sonat: NO_EMES (no compta per als certificats)
        var lost = log.Single(r => r.EventId == encua && r.Result == ExecutionResult.NotPlayed);
        Assert.Equal(At(9, 59), lost.ScheduledFor);
        Assert.Contains("2 pistes esborrades", lost.Detail);
        Assert.Contains("«Programa»", lost.Detail);
    }

    [Fact]
    public void Un_TALLA_que_perd_un_conflicte_deixa_anar_la_veu_que_tenia_reservada()
    {
        Save("Programa", 10, 0, EventMode.Cut, actions: [Pista(Track("Programa"))]);
        Save("Urgent", 10, 0, change: e => e with { Priority = 90 }, actions: [Pista(Track("Avís"))]);
        var scheduler = NewScheduler();

        RunUntil(scheduler, At(9, 59, 40)); // preparat: la primera pista del TALLA ja té una veu
        Assert.Contains(_mixer.Voices, v => v.State != VoiceState.Idle);

        RunUntil(scheduler, At(10, 0));

        Assert.Equal(["Avís"], _queue.Upcoming.Select(i => i.Title));
        Assert.Equal(ExecutionResult.SkippedConflict, Log().Single(r => r.Name == "Programa").Result);
        Assert.All(_mixer.Voices, v => Assert.Equal(VoiceState.Idle, v.State));
    }
}
