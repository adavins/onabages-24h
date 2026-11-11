using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Ordre <c>comprova-emissio</c> (fase 1, pas 11): forats entre pistes a l'historial i avisos del registre.</summary>
public class BroadcastCheckTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 22, 0, 0, TimeSpan.FromHours(1));
    private long _id;

    /// <summary>Una pista que comença <paramref name="startSeconds"/> després de T0 i sona <paramref name="playedSeconds"/>.</summary>
    private HistoryEntry Entry(double startSeconds, double? playedSeconds, bool? complete = true, string title = "") =>
        new(++_id, _id, T0.AddSeconds(startSeconds), playedSeconds is { } p ? (long)(p * 1000) : null, complete,
            QueueOrigin.AutoDj, "Artista", title == "" ? $"Pista {_id}" : title, $@"O:\musica\{_id}.mp3");

    [Fact]
    public void Pistes_encadenades_no_tenen_cap_forat_i_el_temps_no_es_compta_dues_vegades()
    {
        // Cada pista dura 200 s i la següent entra 5 s abans que s'acabi (encadenament)
        var entries = new[] { Entry(0, 200), Entry(195, 200), Entry(390, 200) };

        var report = BroadcastCheck.Analyze(entries, TimeSpan.FromSeconds(1));

        Assert.Empty(report.Gaps);
        Assert.Equal(3, report.Tracks);
        Assert.Equal(TimeSpan.FromSeconds(590), report.Covered);
    }

    [Fact]
    public void Un_silenci_entre_dues_pistes_surt_com_a_forat()
    {
        var entries = new[] { Entry(0, 200), Entry(203.5, 100, title: "Després del silenci"), Entry(303.6, 50) };

        var report = BroadcastCheck.Analyze(entries, TimeSpan.FromSeconds(1));

        var gap = Assert.Single(report.Gaps); // els 0,1 s de la tercera no arriben al llindar
        Assert.Equal(T0.AddSeconds(200), gap.From);
        Assert.Equal(3.5, gap.Length.TotalSeconds, 3);
        Assert.Equal("Després del silenci", gap.After.Title);
    }

    [Fact]
    public void Una_pista_llarga_que_encara_sona_no_fa_veure_un_forat_a_la_seguent()
    {
        // La primera acaba als 300 s; la segona (curta) comença als 100 s i acaba als 110: la tercera, als 290, no fa forat
        var entries = new[] { Entry(0, 300), Entry(100, 10), Entry(290, 60) };

        Assert.Empty(BroadcastCheck.Analyze(entries, TimeSpan.FromSeconds(1)).Gaps);
    }

    [Fact]
    public void Les_aturades_de_cop_i_la_que_encara_sona_es_diuen_a_part()
    {
        // La segona es va tancar en arrencar el motor (durada 0, tallada): el forat fins a la tercera no es pot mesurar
        var entries = new[] { Entry(0, 100), Entry(100, 0, complete: false), Entry(4000, 100), Entry(4100, null, complete: null) };

        var report = BroadcastCheck.Analyze(entries, TimeSpan.FromSeconds(1));

        Assert.Empty(report.Gaps);
        Assert.Equal(2, Assert.Single(report.Unknown).Id);
        Assert.Equal(4, report.Open?.Id);
    }

    [Fact]
    public void Del_registre_surten_les_arrencades_i_els_avisos_agrupats()
    {
        using var temp = new TempFolder();
        string logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllLines(Path.Combine(logs, "motor-2026-11-11.log"),
        [
            "2026-11-11 21:59:00.000 INFO  Ona Bages 24h — motor 0.1.0 arrencant (port 47824, PID 1, dades a D:\\x)",
            "2026-11-11 22:00:00.000 INFO  Ona Bages 24h — motor 0.1.0 arrencant (port 47824, PID 2, dades a D:\\x)",
            "2026-11-11 22:10:00.000 WARN  No s'ha pogut obrir la pista 12",
            "2026-11-11 22:20:00.000 WARN  No s'ha pogut obrir la pista 13",
            "2026-11-11 22:30:00.000 ERROR La sortida ha fallat",
            "2026-11-11 22:40:00.000 INFO  AutoDJ actiu",
        ]);

        var (starts, problems) = BroadcastCheck.ReadLogs(logs, new DateTime(2026, 11, 11, 22, 0, 0), new DateTime(2026, 11, 12));

        Assert.Equal([new DateTime(2026, 11, 11, 22, 0, 0)], starts); // la de les 21:59 és d'abans del període
        Assert.Equal(2, problems.Count);
        Assert.Equal(("ERROR", 1), (problems[0].Level, problems[0].Count)); // els errors, primer
        Assert.Equal(("WARN", 2), (problems[1].Level, problems[1].Count));   // «pista 12» i «pista 13», el mateix avís
    }
}
