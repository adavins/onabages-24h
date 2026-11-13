using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Tests.Engine;

/// <summary>Avisos de l'editor en desar (fase 2, pas 8c; D-021, D-028; docs/planificador.md, 2.1).</summary>
public sealed class EventWarningsTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
    /// <summary>Dilluns 16 de novembre del 2026, a les 8 del matí.</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 8, 0, 0, TimeSpan.FromHours(1));

    /// <summary>Les pistes de prova: l'id és la durada en segons (p. ex. la 3680 dura 61:20).</summary>
    private static TrackFacts? Track(long id) => id switch
    {
        999 => null,
        998 => new TrackFacts("Sense fitxer", 60_000, Missing: true),
        997 => new TrackFacts("Sense durada", null, Missing: false),
        _ => new TrackFacts($"Pista de {id} s", id * 1000, Missing: false),
    };

    private static EventAction Pista(long id) => new() { Type = EventActionType.Track, Params = $$"""{"songId":{{id}}}""" };

    private static ScheduledEvent Ev(long id, string name, EventMode mode, int hour, int minute, params long[] tracks) => new()
    {
        Id = id, Name = name, Mode = mode, StartTime = new TimeOnly(hour, minute), DaysMask = 1, // només els dilluns
        ClearQueue = mode == EventMode.Cut, Actions = tracks.Select(Pista).ToList(),
    };

    private static IReadOnlyList<EventWarning> For(ScheduledEvent subject, params ScheduledEvent[] others) =>
        EventWarnings.For(subject, others, Track, Zone, Now);

    [Fact]
    public void Un_programa_mes_llarg_que_la_seva_franja_diu_quant_se_n_tallara()
    {
        var sons = Ev(1, "Sons a cau d'orella", EventMode.Cut, 15, 0, 3680);   // 61:20
        var country = Ev(2, "El club del country", EventMode.Cut, 16, 0, 1800);

        var warning = Assert.Single(For(sons, country));

        Assert.Equal("«Sons a cau d'orella» (15:00) dura 1:01:20: «El club del country» el tallarà a les 16:00 i se'n perdran 1:20.", warning.Text);
        Assert.Equal([new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)], warning.Dates); // els dos dilluns de les dues setmanes
        // També surt si el que s'edita és el que talla
        Assert.Single(For(country, sons));
    }

    [Fact]
    public void Un_programa_que_ja_ha_comencat_tambe_avisa_si_el_tallaran_mes_tard()
    {
        // Són les 8:00 del dilluns: el de les 7:30 ja sona i el de les 8:15 el tallarà (cas trobat provant-ho, 11-11-2026)
        var sonant = Ev(1, "Ja sona", EventMode.Cut, 7, 30, 3600) with { Recurrence = EventRecurrence.Dates, IncludedDates = [new DateOnly(2026, 11, 16)] };
        var talla = Ev(2, "Talla", EventMode.Cut, 8, 15, 60) with { Recurrence = EventRecurrence.Dates, IncludedDates = [new DateOnly(2026, 11, 16)] };

        Assert.Equal("«Ja sona» (07:30) dura 1:00:00: «Talla» el tallarà a les 08:15 i se'n perdran 15:00.", Assert.Single(For(sonant, talla)).Text);
        Assert.Single(For(talla, sonant));
    }

    [Fact]
    public void Del_que_ja_ha_passat_no_s_avisa()
    {
        // El tall de les 7:00 ja ha passat (són les 8:00)
        var abans = Ev(1, "Abans", EventMode.Cut, 6, 0, 7200) with { Recurrence = EventRecurrence.Dates, IncludedDates = [new DateOnly(2026, 11, 16)] };
        var set = Ev(2, "Set", EventMode.Cut, 7, 0, 60) with { Recurrence = EventRecurrence.Dates, IncludedDates = [new DateOnly(2026, 11, 16)] };

        Assert.Empty(For(abans, set));
    }

    [Fact]
    public void Un_ENCUA_que_no_arribara_a_sonar_perque_el_programa_d_abans_s_allarga()
    {
        var programa = Ev(1, "Programa", EventMode.Cut, 10, 0, 3600);       // de 10:00 a 11:00
        var promo = Ev(2, "Promo", EventMode.Queue, 10, 30, 30);              // hauria de sonar a les 11:00…
        var noticies = Ev(3, "Notícies", EventMode.Cut, 11, 0, 300);          // …però a les 11:00 un TALLA esborra la cua

        var warning = Assert.Single(For(promo, programa, noticies));

        Assert.Equal("«Promo» (10:30) no arribarà a sonar: el contingut d'abans s'acaba a les 11:00 i «Notícies» esborra la cua a les 11:00.", warning.Text);
    }

    [Fact]
    public void Un_ENCUA_que_comenca_pero_el_talla_un_TALLA()
    {
        var programa = Ev(1, "Programa", EventMode.Cut, 10, 0, 3540);       // de 10:00 a 10:59
        var promo = Ev(2, "Promo llarga", EventMode.Queue, 10, 30, 120);      // de 10:59 a 11:01
        var noticies = Ev(3, "Notícies", EventMode.Cut, 11, 0, 300);

        var warning = Assert.Single(For(promo, programa, noticies));

        Assert.Equal("«Promo llarga» (10:30) començarà a les 10:59 i «Notícies» el tallarà a les 11:00.", warning.Text);
    }

    [Fact]
    public void Si_tot_hi_cap_no_hi_ha_cap_avis()
    {
        var programa = Ev(1, "Programa", EventMode.Cut, 10, 0, 3000);
        var promo = Ev(2, "Promo", EventMode.Queue, 10, 30, 30);
        var noticies = Ev(3, "Notícies", EventMode.Cut, 11, 0, 300);

        Assert.Empty(For(promo, programa, noticies));
        Assert.Empty(For(programa, promo, noticies));
    }

    [Fact]
    public void Els_avisos_d_altres_esdeveniments_no_surten()
    {
        var a = Ev(1, "A", EventMode.Cut, 10, 0, 4000);   // A s'allarga sobre B
        var b = Ev(2, "B", EventMode.Cut, 11, 0, 60);
        var editat = Ev(3, "El que s'edita", EventMode.Cut, 18, 0, 60);

        Assert.Empty(For(editat, a, b));
    }

    [Fact]
    public void Un_desactivat_que_s_edita_compta_pero_els_altres_desactivats_no()
    {
        var desactivat = Ev(1, "Desactivat", EventMode.Cut, 10, 0, 4000) with { Enabled = false };
        var altre = Ev(2, "Altre desactivat", EventMode.Cut, 11, 0, 60) with { Enabled = false };
        var actiu = Ev(3, "Actiu", EventMode.Cut, 11, 0, 60);

        Assert.Empty(For(desactivat, altre));
        Assert.Single(For(desactivat, actiu));
    }

    [Fact]
    public void Contingut_mes_llarg_que_la_durada_maxima()
    {
        var programa = Ev(1, "Programa", EventMode.Cut, 10, 0, 3600) with { MaxDurationSeconds = 3300 };

        var warning = Assert.Single(For(programa));

        Assert.Equal("El contingut dura 1:00:00 i la durada màxima de l'esdeveniment és 55:00.", warning.Text);
    }

    [Fact]
    public void Pistes_que_no_existeixen_sense_fitxer_o_sense_durada()
    {
        var ev = Ev(1, "Programa", EventMode.Cut, 10, 0, 999, 998, 997);

        var texts = For(ev).Select(w => w.Text).ToList();

        Assert.Contains("La pista 999 no existeix a la biblioteca: no sonarà.", texts);
        Assert.Contains("«Sense fitxer» no sonarà en aquest ordinador: el fitxer falta.", texts);
        Assert.Contains("No se sap quant dura «Sense durada»: els càlculs de sota no la compten.", texts);
        Assert.All(For(ev), w => Assert.Empty(w.Dates)); // no depenen del dia
    }

    [Fact]
    public void Una_hora_que_no_existeix_pel_canvi_d_hora()
    {
        // Diumenge 28 de març del 2027, de 2:00 a 3:00 (hora d'estiu): les 2:30 no existeixen
        var nit = Ev(1, "Nit", EventMode.Overlay, 2, 30) with { DaysMask = Weekdays.All };

        var warning = Assert.Single(For(nit));

        Assert.Equal("Les 02:30:00 no existeixen pel canvi d'hora: aquell dia no sonarà.", warning.Text);
        Assert.Equal([new DateOnly(2027, 3, 28)], warning.Dates);
    }

    [Fact]
    public void Dos_al_mateix_instant_compta_nomes_el_de_prioritat_mes_alta()
    {
        var guanya = Ev(1, "Guanya", EventMode.Cut, 10, 0, 600) with { Priority = 80 };
        var perd = Ev(2, "Perd", EventMode.Cut, 10, 0, 7200);   // si comptés, s'allargaria sobre el de les 11
        var onze = Ev(3, "Onze", EventMode.Cut, 11, 0, 60);

        Assert.Empty(For(onze, guanya, perd));
    }

    [Fact]
    public void Una_pista_fora_de_les_seves_dates_avisa_dels_dies_que_no_sonara()
    {
        // La falca acaba el 25/11 i la promo comença el 1/12: l'esdeveniment sona els dilluns (16, 23 i 30 de novembre…)
        TrackFacts? Dated(long id) => id switch
        {
            1 => new TrackFacts("Falca de novembre", 20_000, false, ValidUntil: new DateOnly(2026, 11, 25)),
            2 => new TrackFacts("Promo de desembre", 30_000, false, ValidFrom: new DateOnly(2026, 12, 1)),
            _ => null,
        };
        var ev = Ev(1, "Bloc", EventMode.Queue, 10, 0, 1, 2) with { DaysMask = 1 };

        var warnings = EventWarnings.For(ev, [], Dated, Zone, Now);

        var ended = Assert.Single(warnings, w => w.Text.StartsWith("«Falca de novembre»"));
        Assert.Equal("«Falca de novembre» no sonarà: la seva data de fi és el 25/11/2026.", ended.Text);
        Assert.Equal(new DateOnly(2026, 11, 30), ended.Dates[0]); // el primer dilluns després del 25
        var notYet = Assert.Single(warnings, w => w.Text.StartsWith("«Promo de desembre»"));
        Assert.Equal("«Promo de desembre» no sonarà: la seva data d'inici és el 1/12/2026.", notYet.Text);
        Assert.Equal([new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23), new DateOnly(2026, 11, 30)], notYet.Dates);
    }
}
