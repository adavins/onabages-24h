using Microsoft.Extensions.Logging.Abstractions;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Supervision;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>
/// Pistes que fallen seguides (incidència del NAS del 20-11-2026): les primeres s'anoten a part, com sempre; a partir de
/// la tercera, una sola incidència greu que es tanca amb el resum quan torna a sonar una pista de la cua.
/// </summary>
public sealed class FailureBurstTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly IncidentStore _store;
    private readonly IncidentTracker _incidents;
    private readonly FailureBurst _burst;
    private DateTimeOffset _now = new(2026, 11, 20, 3, 38, 9, TimeSpan.FromHours(1));

    public FailureBurstTests()
    {
        _db = Database.Open(_temp.File("incidencies.db"));
        _store = new IncidentStore(_db);
        _incidents = new IncidentTracker(_store, NullLogger.Instance, () => _now);
        _burst = new FailureBurst(_incidents, 3, () => _now);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private static QueueItem Track(string title, QueueOrigin origin = QueueOrigin.AutoDj) =>
        new() { Path = $@"O:\musica\{title}.mp3", Title = title, Origin = origin };

    private static QueueFailed Fail(int inRow) => new(Track($"Cançó {inRow}"), "No es troba el fitxer.", InRow: inRow);

    private IReadOnlyList<Incident> All() => _store.List(new IncidentFilter()).Incidents;

    [Fact]
    public void TheFirstFailuresAreLeftForTheUsualIncident()
    {
        Assert.False(_burst.Failed(Fail(1)));
        Assert.False(_burst.Failed(Fail(2)));
        // Els streams tenen la seva reserva i la seva incidència
        Assert.False(_burst.Failed(new QueueFailed(new QueueItem { Path = "http://servidor/directe", Title = "Directe", Type = TrackType.Stream }, "No connecta.", InRow: 5)));
    }

    [Fact]
    public async Task FromTheThirdOnOneSeriousIncidentIsOpenAndClosedWithTheSummary()
    {
        for (int i = 3; i <= 500; i++)
        {
            Assert.True(_burst.Failed(Fail(i)));
            _now += TimeSpan.FromMilliseconds(20);
        }
        await _burst.Drain();

        var open = Assert.Single(_incidents.Open);
        Assert.Equal((IncidentKind.File, FailureBurst.Key, IncidentSeverity.Danger), (open.Kind, open.Key, open.Severity));
        Assert.StartsWith("No es poden fer sonar les pistes: des de les 03:38:09", open.Text);

        // Torna la música (una pista de la cua; les d'emergència no compten)
        _burst.Started(new QueueStarted(Track("Emergència", QueueOrigin.Emergency), 0));
        await _burst.Drain();
        Assert.Single(_incidents.Open);
        _now = new DateTimeOffset(2026, 11, 20, 3, 40, 33, TimeSpan.FromHours(1));
        _burst.Started(new QueueStarted(Track("A Cavall Del Vent"), 0));
        await _burst.Drain();

        Assert.Empty(_incidents.Open);
        var closed = Assert.Single(All());
        Assert.Equal("Entre les 03:38:09 i les 03:40:33 no s'han pogut fer sonar 500 pistes seguides (l'última, «Cançó 500»: No es troba el fitxer.) Ha tornat a sonar «A Cavall Del Vent».",
            closed.Text);
        Assert.NotNull(closed.EndedAt);
    }

    [Fact]
    public async Task ANewSeriesOpensANewIncident()
    {
        _burst.Failed(Fail(3));
        _burst.Started(new QueueStarted(Track("A"), 0));
        _now += TimeSpan.FromMinutes(10);
        _burst.Failed(Fail(3));
        await _burst.Drain();

        Assert.Equal(2, All().Count);
        Assert.Single(_incidents.Open);
    }
}
