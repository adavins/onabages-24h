using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Supervision;

namespace OnaBages24.Tests.Engine;

/// <summary>Unitats de la música que no responen (D-197): l'arrel d'una ruta, la comprovació amb temps màxim i la tornada.</summary>
public sealed class DriveWatchTests
{
    [Theory]
    [InlineData(@"O:\musica\BOSSA 1\cançó.mp3", @"O:\")]
    [InlineData(@"o:\musica\cançó.mp3", @"o:\")]
    [InlineData(@"\\nas\musica\BOSSA 1\cançó.mp3", @"\\nas\musica")]
    [InlineData("http://servidor:8000/directe", null)]
    [InlineData("entrada://Línia 2", null)]
    [InlineData("", null)]
    public void TheRootIsTheDriveOrTheNetworkShare(string path, string? root)
    {
        Assert.Equal(root, DriveWatch.RootOf(path));
    }

    /// <summary>Espera (com a molt 5 s) que es compleixi una condició: la comprovació es fa en un altre fil.</summary>
    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) Thread.Sleep(10);
        Assert.True(condition());
    }

    [Fact]
    public void ADriveThatDoesNotRespondIsDownUntilItComesBack()
    {
        bool responds = false;
        var watch = new DriveWatch(_ => responds, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
        var events = new List<string>();
        watch.WentDown += (root, _) => { lock (events) events.Add($"down {root}"); };
        watch.CameBack += (root, _) => { lock (events) events.Add($"back {root}"); };

        Assert.False(watch.IsDown(@"O:\musica\a.mp3")); // abans de comprovar-ho, no se'n sap res
        watch.Suspect(@"O:\musica\a.mp3");
        WaitFor(() => watch.IsDown(@"O:\musica\b.mp3"));
        Assert.False(watch.IsDown(@"D:\local\c.mp3")); // les altres unitats, no
        Assert.Equal(@"O:\", Assert.Single(watch.Down).Root);

        responds = true;
        WaitFor(() => !watch.IsDown(@"O:\musica\b.mp3"));
        WaitFor(() => { lock (events) return events.Count == 2; });
        Assert.Equal([@"down O:\", @"back O:\"], events);
        watch.Stop();
    }

    [Fact]
    public void AMissingTrackOnADriveThatRespondsChangesNothing()
    {
        var watch = new DriveWatch(_ => true, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50));
        int down = 0;
        watch.WentDown += (_, _) => Interlocked.Increment(ref down);
        watch.Suspect(@"O:\musica\no-hi-és.mp3");
        Thread.Sleep(300);
        Assert.False(watch.IsDown(@"O:\musica\a.mp3"));
        Assert.Equal(0, down);
    }

    [Fact]
    public void AProbeThatHangsCountsAsNotResponding()
    {
        // Una unitat de xarxa que no respon pot tardar 20 o 30 s a dir-ho: no s'espera tant
        using var release = new ManualResetEventSlim();
        var watch = new DriveWatch(_ => { release.Wait(); return true; }, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10));
        watch.Suspect(@"\\nas\musica\a.mp3");
        WaitFor(() => watch.IsDown(@"\\nas\musica\b.mp3"));
        watch.Stop();
        release.Set();
    }

    [Fact]
    public void TheIncidentTextsSayWhatHappens()
    {
        var since = new DateTimeOffset(2026, 11, 20, 3, 38, 9, TimeSpan.FromHours(1));
        Assert.StartsWith(@"La unitat O:\ no respon (des de les 03:38:09)", IncidentRules.DriveDownText(@"O:\", since));
        Assert.StartsWith(@"La unitat O:\ no ha respost entre les 03:38:09 i les 03:40:33 (2 min 24 s)",
            IncidentRules.DriveBackText(@"O:\", since, since.AddSeconds(144)));
        Assert.Equal(["45 s", "2 min", "1 h 5 min", "2 h"],
            new[] { 45, 120, 3900, 7200 }.Select(s => IncidentRules.Span(TimeSpan.FromSeconds(s))));
    }
}
