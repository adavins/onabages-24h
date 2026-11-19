using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Tests.Engine;

/// <summary>Manteniment de la programació (D-194): què es detecta i què es proposa. Només calcula; no toca res.</summary>
public sealed class ProgrammingMaintenanceTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    /// <summary>Dijous 19 de novembre de 2026, 12:00 (hora local).</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(1));
    private static readonly DateOnly Today = new(2026, 11, 19);

    private static ScheduledEvent Event(long id, string name, Action<List<EventAction>>? actions = null, int hour = 10) => new()
    {
        Id = id, Name = name, Mode = EventMode.Cut, StartTime = new TimeOnly(hour, 0), Recurrence = EventRecurrence.Weekly,
        DaysMask = Weekdays.All, Enabled = true, Modified = Now.AddDays(-1),
        Actions = Build(actions ?? (a => a.Add(Track(1, 100)))),
    };

    private static List<EventAction> Build(Action<List<EventAction>> fill)
    {
        var list = new List<EventAction>();
        fill(list);
        return list;
    }

    private static EventAction Track(long actionId, long trackId) => new() { Id = actionId, Type = EventActionType.Track, Params = $$"""{"songId":{{trackId}}}""" };

    private static Track TrackWith(long id, DateOnly? until = null, bool enabled = true) =>
        new() { Id = id, Path = $@"M:\musica\{id}.mp3", Title = $"Pista {id}", ValidUntil = until, Enabled = enabled };

    private static IReadOnlyList<MaintenanceFinding> Scan(
        IEnumerable<ScheduledEvent> events, IEnumerable<Track>? tracks = null, IEnumerable<Campaign>? campaigns = null,
        ISet<long>? played = null, IEnumerable<long>? rotations = null, IEnumerable<(long, string)>? emptySubcategories = null)
    {
        var all = (tracks ?? [TrackWith(100)]).ToDictionary(t => t.Id);
        return ProgrammingMaintenance.Scan(new MaintenanceInputs
        {
            Events = events.ToList(),
            GetTrack = id => all.GetValueOrDefault(id),
            Campaigns = (campaigns ?? []).ToList(),
            RotationIds = (rotations ?? [1]).ToHashSet(),
            BagIds = new HashSet<long> { 1 },
            SubcategoryIds = new HashSet<long> { 1 },
            ExpiredTracks = all.Values.Where(t => t.ValidUntil is { } u && u < Today).ToList(),
            PlayedRecently = id => played?.Contains(id) == true,
            UnusedEmptySubcategories = (emptySubcategories ?? []).ToList(),
            Now = Now,
            Zone = Zone,
        });
    }

    [Fact]
    public void NothingToDoWithAHealthyProgramme()
    {
        Assert.Empty(Scan([Event(1, "Magazín"), Event(2, "Informatiu", hour: 13)]));
    }

    [Fact]
    public void AnEventWhoseEndDateHasPassedIsProposedForDeletion()
    {
        var finding = Assert.Single(Scan([Event(1, "Estiu") with { EndDate = new DateOnly(2026, 9, 30) }]));
        Assert.Equal((MaintenanceCase.EndedEvent, MaintenanceAction.DeleteEvent, "event:1"), (finding.Case, finding.Action, finding.Key));
        Assert.Equal("Es va acabar el 30/9/2026.", finding.Reason);
    }

    [Fact]
    public void AnEventWhoseDatesHaveAllPassedHasEnded()
    {
        var ev = Event(1, "Festa major") with
        {
            Recurrence = EventRecurrence.Dates, IncludedDates = [new DateOnly(2026, 8, 15), new DateOnly(2026, 8, 16)],
        };
        Assert.Equal(MaintenanceCase.EndedEvent, Assert.Single(Scan([ev])).Case);
        // Si encara en queda una d'avui en endavant, no
        Assert.Empty(Scan([ev with { IncludedDates = [new DateOnly(2026, 8, 15), Today] }]));
    }

    [Fact]
    public void ADisabledEventIsProposedOnlyAfterNinetyDaysWithoutChanges()
    {
        var old = Event(1, "Vell") with { Enabled = false, Modified = Now.AddDays(-91) };
        var recent = Event(2, "Vacances", hour: 11) with { Enabled = false, Modified = Now.AddDays(-30) };
        var finding = Assert.Single(Scan([old, recent]));
        Assert.Equal((MaintenanceCase.DisabledEvent, 1L), (finding.Case, finding.Id));
    }

    [Fact]
    public void OfTwoIdenticalEventsTheSecondIsADuplicate()
    {
        // El mateix contingut amb el JSON escrit diferent també és el mateix
        var first = Event(1, "Magazín");
        var copy = Event(2, "Magazín (còpia)", a => a.Add(new EventAction { Id = 9, Type = EventActionType.Track, Params = """{ "songId": 100 }""" }));
        var finding = Assert.Single(Scan([first, copy]));
        Assert.Equal((MaintenanceCase.DuplicateEvent, 2L), (finding.Case, finding.Id));
        Assert.Contains("«Magazín»", finding.Reason);
    }

    [Fact]
    public void AnEventThatAlwaysLosesToAHigherPriorityOneNeverWins()
    {
        var winner = Event(1, "Informatiu") with { Priority = 60 };
        var loser = Event(2, "Promo", a => a.Add(Track(2, 100))) with { Mode = EventMode.Queue, DaysMask = Weekdays.MondayToFriday };
        var finding = Assert.Single(Scan([winner, loser]));
        Assert.Equal((MaintenanceCase.NeverWins, 2L), (finding.Case, finding.Id));
        Assert.Contains("«Informatiu», de prioritat més alta (60)", finding.Reason);
    }

    [Fact]
    public void AnEventThatWinsSomeDaysIsKept()
    {
        // L'altre només sona dilluns: la resta de dies guanya aquest
        var monday = Event(1, "Especial de dilluns") with { Priority = 60, DaysMask = 1 };
        var daily = Event(2, "Magazín", a => a.Add(Track(2, 100)));
        Assert.Empty(Scan([monday, daily]));
    }

    [Fact]
    public void WithTheSamePriorityTheLowerIdWinsAsInTheScheduler()
    {
        var first = Event(1, "A");
        var second = Event(2, "B", a => a.Add(Track(2, 100)));
        Assert.Equal(2L, Assert.Single(Scan([first, second])).Id);
    }

    [Fact]
    public void OverlayAndSystemEventsNeverConflict()
    {
        var cut = Event(1, "Magazín") with { Priority = 90 };
        var signals = Event(2, "Senyals", a => a.Add(new EventAction { Id = 2, Type = EventActionType.Auxiliary, Params = """{"songId":100}""" })) with { Mode = EventMode.Overlay };
        Assert.Empty(Scan([cut, signals]));
    }

    [Fact]
    public void AnEventWhoseTracksHaveAllExpiredDoesNothing()
    {
        var finding = Assert.Single(Scan([Event(1, "Falca vella")], [TrackWith(100, until: new DateOnly(2026, 10, 1))]),
            f => f.Subject == MaintenanceSubject.Event);
        Assert.Equal(MaintenanceCase.DeadEvent, finding.Case);
        Assert.Contains("«Pista 100» es va acabar el 1/10/2026", finding.Reason);
    }

    [Fact]
    public void AnEventWithATrackThatCanStillPlayIsKept()
    {
        // Una pista que falta o està desactivada es pot arreglar: l'esdeveniment no es proposa
        Assert.Empty(Scan([Event(1, "Magazín")], [TrackWith(100, enabled: false)]));
    }

    [Fact]
    public void AnEventWithAFinishedCampaignAndNothingElseDoesNothing()
    {
        var campaign = new Campaign { Id = 5, Name = "Rebaixes", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 7, 31), State = CampaignState.Active };
        var ev = Event(1, "Bloc de falques", a => a.Add(new EventAction { Id = 1, Type = EventActionType.Campaign, Params = """{"campanyaId":5}""" }));
        var finding = Assert.Single(Scan([ev], campaigns: [campaign]));
        Assert.Equal(MaintenanceCase.DeadEvent, finding.Case);
        Assert.Contains("la campanya «Rebaixes» s'ha acabat", finding.Reason);
    }

    [Fact]
    public void BrokenActionsAreRemovedAndTheRestOfTheEventIsKept()
    {
        var ev = Event(1, "Canvi de rotació", a =>
        {
            a.Add(Track(1, 100));
            a.Add(new EventAction { Id = 2, Type = EventActionType.Rotation, Params = """{"rotacioId":7}""" });
        });
        var finding = Assert.Single(Scan([ev]));
        Assert.Equal((MaintenanceCase.BrokenActions, MaintenanceAction.RemoveActions, "actions:1"), (finding.Case, finding.Action, finding.Key));
        Assert.Equal([2L], finding.ActionIds);
        Assert.Contains("la rotació 7", finding.Reason);
    }

    [Fact]
    public void AnEventWithOnlyBrokenActionsIsDeleted()
    {
        var ev = Event(1, "Canvi de rotació", a => a.Add(new EventAction { Id = 2, Type = EventActionType.Rotation, Params = """{"rotacioId":7}""" })) with { Mode = EventMode.System };
        var finding = Assert.Single(Scan([ev]));
        Assert.Equal((MaintenanceCase.BrokenActions, MaintenanceAction.DeleteEvent), (finding.Case, finding.Action));
    }

    [Fact]
    public void PastExceptionsAreRemoved()
    {
        var ev = Event(1, "Magazín") with { ExcludedDates = [new DateOnly(2026, 1, 6), new DateOnly(2026, 4, 6), new DateOnly(2026, 12, 25)] };
        var finding = Assert.Single(Scan([ev]));
        Assert.Equal((MaintenanceCase.PastExceptions, MaintenanceAction.RemovePastExceptions), (finding.Case, finding.Action));
        Assert.Equal("2 dies que no s'emet ja han passat (del 6/1/2026 al 6/4/2026).", finding.Reason);
    }

    [Fact]
    public void AFinishedCampaignInAnEventThatStillPlaysIsRemovedFromTheEvents()
    {
        var campaign = new Campaign { Id = 5, Name = "Rebaixes", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 7, 31), State = CampaignState.Active };
        var ev = Event(1, "Magazín", a =>
        {
            a.Add(Track(1, 100));
            a.Add(new EventAction { Id = 2, Type = EventActionType.Campaign, Params = """{"campanyaId":5}""" });
        });
        var finding = Assert.Single(Scan([ev], campaigns: [campaign]));
        Assert.Equal((MaintenanceCase.ExpiredInEvents, MaintenanceSubject.Campaign, MaintenanceAction.RemoveFromEvents, "campaign-events:5"),
            (finding.Case, finding.Subject, finding.Action, finding.Key));
    }

    [Fact]
    public void AnExpiredTrackNobodyUsesIsDeletedIfItHasNotPlayed()
    {
        var finding = Assert.Single(Scan([], [TrackWith(200, until: new DateOnly(2026, 10, 1))]));
        Assert.Equal((MaintenanceCase.ExpiredTrack, MaintenanceAction.DeleteTrack, "track:200"), (finding.Case, finding.Action, finding.Key));
    }

    [Fact]
    public void AnExpiredTrackThatHasPlayedIsDisabledNotDeleted()
    {
        // Els certificats la necessiten (D-011)
        var finding = Assert.Single(Scan([], [TrackWith(200, until: new DateOnly(2026, 10, 1))], played: new HashSet<long> { 200 }));
        Assert.Equal(MaintenanceAction.DisableTrack, finding.Action);
        Assert.Contains("ha sonat els últims 2 anys", finding.Reason);
        // Si ja està desactivada, no hi ha res a fer
        Assert.Empty(Scan([], [TrackWith(200, until: new DateOnly(2026, 10, 1), enabled: false)], played: new HashSet<long> { 200 }));
    }

    [Fact]
    public void AnExpiredTrackOfACampaignIsDisabledNotDeleted()
    {
        var campaign = new Campaign
        {
            Id = 5, Name = "Rebaixes", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2026, 7, 31), State = CampaignState.Finished,
            Spots = [new CampaignSpot { TrackId = 200 }],
        };
        var finding = Assert.Single(Scan([], [TrackWith(200, until: new DateOnly(2026, 7, 31))], campaigns: [campaign]));
        Assert.Equal(MaintenanceAction.DisableTrack, finding.Action);
        Assert.Contains("és d'una campanya", finding.Reason);
    }

    [Fact]
    public void AnExpiredTrackUsedByAnEventIsNotProposedOnItsOwn()
    {
        // L'esdeveniment ja surt com a «no farà res»; la pista, no
        var findings = Scan([Event(1, "Falca vella")], [TrackWith(100, until: new DateOnly(2026, 10, 1))]);
        Assert.DoesNotContain(findings, f => f.Subject == MaintenanceSubject.Track);
    }

    [Fact]
    public void EmptySubcategoriesAreProposedForDeletion()
    {
        var finding = Assert.Single(Scan([], emptySubcategories: [(8, "Música / Nadal 2019")]));
        Assert.Equal((MaintenanceCase.EmptySubcategory, MaintenanceAction.DeleteSubcategory, "subcategory:8", "Música / Nadal 2019"),
            (finding.Case, finding.Action, finding.Key, finding.Name));
    }

    [Fact]
    public void AnEventAppearsOnlyOnceWithTheFirstReason()
    {
        // Acabat i, a més, duplicat: només surt com a acabat
        var ended = new DateOnly(2026, 9, 30);
        var findings = Scan([Event(1, "A") with { EndDate = ended }, Event(2, "B") with { EndDate = ended }]);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(MaintenanceCase.EndedEvent, f.Case));
    }

    [Fact]
    public void TemplatesAreNeverProposed()
    {
        Assert.Empty(Scan([Event(1, "Plantilla") with { IsTemplate = true, Enabled = false, Modified = Now.AddYears(-1) }]));
    }
}
