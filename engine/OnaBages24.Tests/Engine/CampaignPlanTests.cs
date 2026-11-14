using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Pla de passis de les campanyes i acció CAMPANYA (fase 4, pas 3b; D-029, D-123).</summary>
public sealed class CampaignPlanTests : IDisposable
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Events _events;
    private readonly Campaigns _campaigns;
    private readonly QueuePlayer _queue = new(new Mixer(48000));
    private readonly long _a, _b, _c;
    private DateTimeOffset _now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private int _next;

    public CampaignPlanTests()
    {
        _db = Database.Open(_temp.File("pla.db"));
        _library = new Library(_db);
        _events = new Events(_db);
        _campaigns = new Campaigns(_db);
        _a = Spot("Falca A");
        _b = Spot("Falca B");
        _c = Spot("Falca C");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _db.Dispose();
        _temp.Dispose();
    }

    private long Spot(string title)
    {
        string path = _temp.File($"f{++_next}.mp3");
        File.WriteAllBytes(path, [0]);
        return _library.AddTrack(new Track
        {
            Path = path, Type = TrackType.Commercial, Artist = "Ferreteria", Title = title,
            DurationMs = 20_000, MixMs = 19_500, EndMs = 20_000, CueSource = CueSource.Analyzer,
        });
    }

    private CampaignPlans Plans() => new(_campaigns, _events, _library, Utc, () => _now);

    /// <summary>Una campanya de novembre amb aquestes falques.</summary>
    private Campaign Campaign(CampaignSelection selection, params CampaignSpot[] spots)
    {
        var c = new Campaign
        {
            Name = "Ferreteria", StartDate = new DateOnly(2026, 11, 1), EndDate = new DateOnly(2026, 11, 30),
            Selection = selection, State = CampaignState.Active, Spots = spots,
        };
        return _campaigns.Get(_campaigns.Save(c, _now))!;
    }

    /// <summary>Un esdeveniment ENCUA amb l'acció CAMPANYA (i, si cal, altres accions abans i després).</summary>
    private ScheduledEvent Event(long campaignId, TimeOnly time, int days = Weekdays.All, int? actionDays = null, params EventAction[] around)
    {
        var actions = new List<EventAction>();
        if (around.Length > 0) actions.Add(around[0]);
        actions.Add(new EventAction { Type = EventActionType.Campaign, Params = $$"""{"campanyaId":{{campaignId}}}""", DaysMask = actionDays });
        actions.AddRange(around.Skip(1));
        long id = _events.Save(new ScheduledEvent { Name = $"Falques {time:HH:mm}", Mode = EventMode.Queue, StartTime = time, DaysMask = days, Actions = actions });
        return _events.Get(id)!;
    }

    private static EventAction TrackAction(long id) => new() { Type = EventActionType.Track, Params = $$"""{"songId":{{id}}}""" };

    private IReadOnlyList<CampaignPass> Compute(Campaign c, IReadOnlyList<CampaignPass>? past = null, DateTimeOffset? from = null) =>
        CampaignPlanner.Compute(c, _events.List(), _library.GetTrack, past ?? [], from ?? _now, Utc);

    [Fact]
    public void Alterna_reparteix_a_parts_iguals_i_també_per_hora()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        foreach (int h in new[] { 8, 13, 19 }) Event(c.Id, new TimeOnly(h, 30));

        var plan = Compute(c);
        Assert.Equal(90, plan.Count); // 30 dies × 3
        Assert.Equal(45, plan.Count(p => p.TrackId == _a));
        foreach (int h in new[] { 8, 13, 19 })
        {
            int a = plan.Count(p => p.At.Hour == h && p.TrackId == _a);
            Assert.InRange(a, 14, 16); // cap falca no surt sempre a la mateixa hora
        }
        Assert.Equal(_a, plan[0].TrackId);
        Assert.Equal(_b, plan[1].TrackId);
    }

    [Fact]
    public void Pes_dona_mes_passis_a_la_falca_de_mes_pes()
    {
        var c = Campaign(CampaignSelection.Weight, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _c, Weight = 2 });
        Event(c.Id, new TimeOnly(8, 30));
        Event(c.Id, new TimeOnly(19, 30));

        var plan = Compute(c);
        Assert.Equal(60, plan.Count);
        Assert.Equal(20, plan.Count(p => p.TrackId == _a));
        Assert.Equal(40, plan.Count(p => p.TrackId == _c));
    }

    [Fact]
    public void Sequencia_va_en_ordre_i_salta_la_que_aquell_dia_no_es_valida()
    {
        // B només els dilluns (2/11, 9/11…): els altres dies la seqüència és A, C, A, C…
        var c = Campaign(CampaignSelection.Sequence, new CampaignSpot { TrackId = _a },
            new CampaignSpot { TrackId = _b, DaysMask = 1 }, new CampaignSpot { TrackId = _c });
        Event(c.Id, new TimeOnly(8, 0));
        Event(c.Id, new TimeOnly(12, 0));
        Event(c.Id, new TimeOnly(16, 0));

        var plan = Compute(c);
        var sunday = plan.Where(p => p.At.Date == new DateTime(2026, 11, 1)).Select(p => p.TrackId).ToList();
        Assert.Equal([_a, _c, _a], sunday);
        var monday = plan.Where(p => p.At.Date == new DateTime(2026, 11, 2)).Select(p => p.TrackId).ToList();
        Assert.Equal([_b, _c, _a], monday); // després d'A, la següent és B (dilluns sí que és vàlida)
    }

    [Fact]
    public void L_exemple_de_la_ferreteria_C_nomes_del_24_al_28()
    {
        var c = Campaign(CampaignSelection.Weight, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b },
            new CampaignSpot { TrackId = _c, StartDate = new DateOnly(2026, 11, 24), EndDate = new DateOnly(2026, 11, 28), Weight = 2 });
        foreach (int h in new[] { 8, 13, 19 }) Event(c.Id, new TimeOnly(h, 30), days: 0b0011111); // de dilluns a divendres

        var plan = Compute(c);
        Assert.Equal(21 * 3, plan.Count); // 21 dies feiners al novembre del 2026
        Assert.All(plan.Where(p => p.TrackId == _c), p => Assert.InRange(p.At.Day, 24, 28));
        Assert.Contains(plan, p => p.TrackId == _c);
        Assert.DoesNotContain(plan, p => p.At.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void Compten_les_dates_de_la_pista_els_dies_propis_de_l_accio_i_les_excepcions()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a });
        _library.UpdateTrack(_library.GetTrack(_a)! with { ValidUntil = new DateOnly(2026, 11, 10) });
        var ev = Event(c.Id, new TimeOnly(9, 0), actionDays: 0b0000001); // l'acció, només els dilluns
        _events.Save(ev with { ExcludedDates = [new DateOnly(2026, 11, 2)] });

        var plan = Compute(c);
        // Dilluns de novembre: 2 (excepció), 9, 16, 23 i 30; a partir de l'11 la pista és fora de dates: passi buit
        Assert.Equal([9, 16, 23, 30], plan.Select(p => p.At.Day));
        Assert.Equal(_a, plan[0].TrackId);
        Assert.All(plan.Skip(1), p => Assert.Null(p.TrackId));
    }

    [Fact]
    public void En_recalcular_els_passis_passats_no_canvien_i_compten_per_a_l_equilibri()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        Event(c.Id, new TimeOnly(10, 0));
        var plans = Plans();
        Assert.Equal(30, plans.RecalculateAll());
        var before = _campaigns.Plan(c.Id);

        // Al cap de 5 dies i mig s'afegeix una falca: els 6 passis fets (han sonat: historial) es mantenen; la nova recupera passis
        _now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        foreach (var p in before.Take(6)) _library.AddHistory(_library.GetTrack(p.TrackId!.Value)!, p.At, QueueOrigin.Event, c.Id);
        _campaigns.Save(c with { Spots = [.. c.Spots, new CampaignSpot { TrackId = _c }] }, _now);
        Assert.Equal(24, plans.RecalculateAll());
        var after = _campaigns.Plan(c.Id);

        Assert.Equal(before.Take(6), after.Take(6));
        Assert.Equal(_c, after[6].TrackId);
        Assert.Equal(10, after.Count(p => p.TrackId == _a));
        Assert.Equal(10, after.Count(p => p.TrackId == _b));
        Assert.Equal(10, after.Count(p => p.TrackId == _c));
    }

    [Fact]
    public void Un_passi_d_abans_que_no_va_sonar_no_compta_per_a_l_equilibri()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        Event(c.Id, new TimeOnly(10, 0));
        var plans = Plans();
        plans.RecalculateAll(); // A, B, A, B…

        // Han passat tres dies (A, B, A) sense que soni res (p. ex. el motor estava aturat): torna a començar per A
        _now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        plans.RecalculateAll();
        Assert.Equal(_a, _campaigns.Plan(c.Id, from: _now)[0].TrackId);

        // Si sí que van sonar, la següent és B
        foreach (var p in _campaigns.Plan(c.Id, to: _now)) _library.AddHistory(_library.GetTrack(p.TrackId!.Value)!, p.At, QueueOrigin.Event, c.Id);
        plans.RecalculateAll();
        Assert.Equal(_b, _campaigns.Plan(c.Id, from: _now)[0].TrackId);
    }

    [Fact]
    public void Una_campanya_que_no_es_activa_no_te_passis_futurs()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a });
        Event(c.Id, new TimeOnly(10, 0));
        var plans = Plans();
        plans.RecalculateAll();
        Assert.NotEmpty(_campaigns.Plan(c.Id));

        _campaigns.Save(c with { State = CampaignState.Draft }, _now);
        Assert.Equal(0, plans.RecalculateAll());
        Assert.Empty(_campaigns.Plan(c.Id));
    }

    [Fact]
    public void L_accio_CAMPANYA_fa_sonar_la_falca_del_pla_al_seu_lloc()
    {
        long before = Spot("Abans"), after = Spot("Després");
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        var ev = Event(c.Id, new TimeOnly(10, 0), around: [TrackAction(before), TrackAction(after)]);
        var plans = Plans();
        plans.RecalculateAll();
        var executor = new EventExecutor(_library, _queue, null, campaigns: plans);

        var day1 = executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 1), new TimeOnly(10, 0), Utc));
        Assert.Equal([before, _a, after], day1.Items.Select(i => i.TrackId!.Value));
        Assert.Equal(c.Id, day1.Items[1].CampaignId);
        Assert.Null(day1.Items[0].CampaignId);
        Assert.Empty(day1.Warnings);

        var day2 = executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 2), new TimeOnly(10, 0), Utc));
        Assert.Equal(_b, day2.Items[1].TrackId);
    }

    [Fact]
    public void Si_la_falca_prevista_no_pot_sonar_en_sona_una_altra_amb_avis()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        var ev = Event(c.Id, new TimeOnly(10, 0));
        var plans = Plans();
        plans.RecalculateAll();
        _library.UpdateTrack(_library.GetTrack(_a)! with { Enabled = false }); // sense recalcular el pla

        var executor = new EventExecutor(_library, _queue, null, campaigns: plans);
        var prepared = executor.Prepare(Recurrences.At(ev, new DateOnly(2026, 11, 1), new TimeOnly(10, 0), Utc));
        Assert.Equal(_b, Assert.Single(prepared.Items).TrackId);
        Assert.Contains("«Falca A», desactivada", Assert.Single(prepared.Warnings));
    }

    [Fact]
    public void Un_esborrany_o_una_campanya_repetida_no_fan_sonar_res_i_ho_diuen()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a });
        var ev = Event(c.Id, new TimeOnly(10, 0));
        _events.Save(ev with { Actions = [.. ev.Actions, ev.Actions[0] with { Id = 0 }] }); // la mateixa campanya dues vegades
        ev = _events.Get(ev.Id)!;
        var plans = Plans();
        plans.RecalculateAll();
        var executor = new EventExecutor(_library, _queue, null, campaigns: plans);
        var at = Recurrences.At(ev, new DateOnly(2026, 11, 1), new TimeOnly(10, 0), Utc);

        var prepared = executor.Prepare(at);
        Assert.Single(prepared.Items);
        Assert.Contains("dues vegades", Assert.Single(prepared.Warnings));

        _campaigns.Save(c with { State = CampaignState.Draft }, _now);
        prepared = executor.Prepare(at);
        Assert.Empty(prepared.Items);
        Assert.Contains(prepared.Warnings, w => w.Contains("és un esborrany"));
    }

    [Fact]
    public void L_historial_desa_la_campanya_que_ha_fet_sonar_la_falca()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a });
        _library.AddHistory(_library.GetTrack(_a)!, _now, QueueOrigin.Event, c.Id);
        _library.AddHistory(_library.GetTrack(_a)!, _now, QueueOrigin.Event);
        Assert.Equal(1, _campaigns.PlayCount(c.Id));
    }

    [Fact]
    public void L_ordre_campaigns_plan_dona_els_passis_i_el_recompte_per_falca()
    {
        var c = Campaign(CampaignSelection.Alternate, new CampaignSpot { TrackId = _a }, new CampaignSpot { TrackId = _b });
        Event(c.Id, new TimeOnly(10, 0));
        Plans().RecalculateAll();
        var router = new CommandRouter(new EngineState(), () => { }, _library, events: _events, campaigns: _campaigns) { CampaignClock = () => _now };

        var msg = new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = "campaigns.plan", ["campaignId"] = c.Id };
        var result = router.Handle(msg.ToJsonString())["result"]!;
        var passes = result["passes"]!.AsArray();
        Assert.Equal(30, passes.Count);
        Assert.Equal("Falques 10:00", passes[0]!["eventName"]!.GetValue<string>());
        Assert.Equal("Falca A", passes[0]!["title"]!.GetValue<string>());
        Assert.Equal([15, 15], result["bySpot"]!.AsArray().Select(s => s!["count"]!.GetValue<int>()));
        Assert.Equal(0, result["empty"]!.GetValue<int>());
    }
}
