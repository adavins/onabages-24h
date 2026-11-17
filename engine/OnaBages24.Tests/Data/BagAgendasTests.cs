using OnaBages24.Data;

namespace OnaBages24.Tests.Data;

/// <summary>Agenda de bosses (D-170): quina subcategoria toca a cada bossa cada mes, i com es desa.</summary>
public sealed class BagAgendasTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly BagAgendas _agendas;
    /// <summary>Adult 1…9 (l'índex 0 és Adult 1).</summary>
    private readonly long[] _adult = new long[9];
    private readonly long _bagA, _bagB;

    public BagAgendasTests()
    {
        _db = Database.Open(_temp.File("agendes.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _agendas = new BagAgendas(_db);
        long music = _library.EnsureCategory("Music");
        for (int i = 0; i < 9; i++) _adult[i] = _library.EnsureSubcategory(music, $"Adult {i + 1}");
        _bagA = _rotations.CreateBag("Generalista A", _adult[0], "a mà", DateTimeOffset.UtcNow);
        _bagB = _rotations.CreateBag("Generalista B", _adult[1], "a mà", DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private BagAgenda Adult(DateOnly start) =>
        new(1, "Adult", start, true, [_bagA, _bagB], _adult, null);

    /// <summary>Les subcategories (1…9) de les bosses A i B el mes indicat.</summary>
    private (int A, int B) Month(BagAgenda agenda, DateOnly date)
    {
        var a = agenda.Assignment(date);
        Assert.Equal([_bagA, _bagB], a.Select(x => x.BagId));
        return (Array.IndexOf(_adult, a[0].SubcategoryId) + 1, Array.IndexOf(_adult, a[1].SubcategoryId) + 1);
    }

    [Fact]
    public void Cas_real_alterna_i_la_que_continua_es_queda_a_la_mateixa_bossa()
    {
        var agenda = Adult(new DateOnly(2026, 12, 1));
        Assert.Equal((1, 2), Month(agenda, new DateOnly(2026, 12, 1)));
        Assert.Equal((3, 2), Month(agenda, new DateOnly(2027, 1, 15)));
        Assert.Equal((3, 4), Month(agenda, new DateOnly(2027, 2, 28)));
        Assert.Equal((5, 4), Month(agenda, new DateOnly(2027, 3, 1)));
        // Mes 8: Adult 9 i 8; mes 9: Adult 9 i 1 (després d'Adult 9 es torna a Adult 1, sense salt); mes 10: Adult 2 i 1
        Assert.Equal((9, 8), Month(agenda, new DateOnly(2027, 7, 1)));
        Assert.Equal((9, 1), Month(agenda, new DateOnly(2027, 8, 1)));
        Assert.Equal((2, 1), Month(agenda, new DateOnly(2027, 9, 1)));
        Assert.Equal((2, 3), Month(agenda, new DateOnly(2027, 10, 1)));
    }

    [Fact]
    public void Cada_mes_nomes_canvia_una_bossa_i_totes_les_subcategories_hi_passen()
    {
        var agenda = Adult(new DateOnly(2026, 1, 1));
        var seen = new HashSet<int>();
        var previous = Month(agenda, new DateOnly(2026, 1, 1));
        for (int m = 1; m < 36; m++)
        {
            var now = Month(agenda, new DateOnly(2026, 1, 1).AddMonths(m));
            Assert.Equal(1, (now.A != previous.A ? 1 : 0) + (now.B != previous.B ? 1 : 0));
            Assert.NotEqual(now.A, now.B);
            seen.Add(now.A);
            seen.Add(now.B);
            previous = now;
        }
        Assert.Equal(9, seen.Count);
    }

    [Fact]
    public void Abans_del_mes_d_inici_no_toca_res()
    {
        var agenda = Adult(new DateOnly(2026, 12, 1));
        Assert.Empty(agenda.Assignment(new DateOnly(2026, 11, 30)));
        Assert.False(agenda.IsDue(new DateOnly(2026, 11, 30)));
        Assert.True(agenda.IsDue(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Un_sol_canvi_per_mes_i_si_el_motor_estava_aturat_el_dia_1_es_fa_despres()
    {
        var agenda = Adult(new DateOnly(2026, 1, 1)) with { AppliedMonth = new DateOnly(2026, 3, 1) };
        Assert.False(agenda.IsDue(new DateOnly(2026, 3, 31)));
        // El dia 1 d'abril el motor estava aturat: el dia 3, en arrencar, encara toca
        Assert.True(agenda.IsDue(new DateOnly(2026, 4, 3)));
        Assert.False((agenda with { Active = false }).IsDue(new DateOnly(2026, 4, 3)));
    }

    [Fact]
    public void Els_mesos_es_compten_amb_dates_de_calendari_tambe_amb_el_canvi_d_horari()
    {
        // L'últim diumenge de març (2026-03-29) i d'octubre (2026-10-25) canvia l'hora: el mes no es mou
        Assert.Equal(1, BagAgenda.MonthsBetween(new DateOnly(2026, 3, 31), new DateOnly(2026, 4, 1)));
        Assert.Equal(0, BagAgenda.MonthsBetween(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.Equal(13, BagAgenda.MonthsBetween(new DateOnly(2025, 12, 31), new DateOnly(2027, 1, 1)));
        Assert.Equal(new DateOnly(2026, 12, 1), BagAgenda.ParseMonth("2026-12"));
        Assert.Null(BagAgenda.ParseMonth("2026-13"));
        Assert.Equal("2027-01", BagAgenda.MonthText(new DateOnly(2027, 1, 20)));
    }

    [Fact]
    public void Funciona_amb_una_sola_bossa_i_amb_tres()
    {
        var one = new BagAgenda(1, "Una", new DateOnly(2026, 1, 1), true, [_bagA], [_adult[0], _adult[1], _adult[2]], null);
        Assert.Equal(_adult[2], one.Assignment(new DateOnly(2026, 3, 1))[0].SubcategoryId);
        Assert.Equal(_adult[0], one.Assignment(new DateOnly(2026, 4, 1))[0].SubcategoryId);

        long bagC = _rotations.CreateBag("Generalista C", null, "a mà", DateTimeOffset.UtcNow);
        var three = new BagAgenda(1, "Tres", new DateOnly(2026, 1, 1), true, [_bagA, _bagB, bagC], _adult[..4], null);
        // Mes 2: posicions 1, 2 i 3 → B=Adult 2, C=Adult 3 (es queden) i A=Adult 4
        Assert.Equal([_adult[3], _adult[1], _adult[2]], three.Assignment(new DateOnly(2026, 2, 1)).Select(a => a.SubcategoryId));
    }

    [Fact]
    public void Es_desa_i_es_llegeix_amb_l_ordre_i_en_tornar_a_desar_es_torna_a_aplicar()
    {
        long id = _agendas.Save(null, " Adult ", new DateOnly(2026, 12, 1), true, [_bagB, _bagA], _adult);
        var agenda = _agendas.Get(id)!;
        Assert.Equal("Adult", agenda.Name);
        Assert.Equal(new DateOnly(2026, 12, 1), agenda.StartMonth);
        Assert.Equal([_bagB, _bagA], agenda.BagIds);
        Assert.Equal(_adult, agenda.SubcategoryIds);
        Assert.Null(agenda.AppliedMonth);

        _agendas.MarkApplied(id, new DateOnly(2026, 12, 17));
        Assert.Equal(new DateOnly(2026, 12, 1), _agendas.Get(id)!.AppliedMonth);

        _agendas.Save(id, "Adult", new DateOnly(2026, 11, 1), false, [_bagA], _adult[..3]);
        agenda = _agendas.Get(id)!;
        Assert.False(agenda.Active);
        Assert.Equal([_bagA], agenda.BagIds);
        Assert.Null(agenda.AppliedMonth);
        Assert.Equal(["Adult"], _agendas.UsingSubcategory(_adult[0]));
        Assert.Empty(_agendas.UsingSubcategory(_adult[5]));
        Assert.Empty(_agendas.UsingBag(_bagB));

        _agendas.Delete(id);
        Assert.Empty(_agendas.List());
    }

    [Fact]
    public void No_es_desa_si_no_es_valida_i_no_deixa_res_a_mitges()
    {
        Assert.Contains("nom", Assert.Throws<DatabaseException>(() => _agendas.Save(null, " ", new DateOnly(2026, 1, 1), true, [_bagA], _adult)).Message);
        Assert.Contains("com a mínim 2 subcategories",
            Assert.Throws<DatabaseException>(() => _agendas.Save(null, "X", new DateOnly(2026, 1, 1), true, [_bagA, _bagB], [_adult[0]])).Message);
        Assert.Contains("dues vegades",
            Assert.Throws<DatabaseException>(() => _agendas.Save(null, "X", new DateOnly(2026, 1, 1), true, [_bagA], [_adult[0], _adult[0]])).Message);

        _agendas.Save(null, "Adult", new DateOnly(2026, 1, 1), true, [_bagA], _adult);
        Assert.Contains("Ja hi ha una agenda «adult»",
            Assert.Throws<DatabaseException>(() => _agendas.Save(null, "adult", new DateOnly(2026, 1, 1), true, [_bagB], _adult)).Message);
        Assert.Contains("La bossa «Generalista A» ja és a l'agenda «Adult»",
            Assert.Throws<DatabaseException>(() => _agendas.Save(null, "Altra", new DateOnly(2026, 1, 1), true, [_bagA], _adult)).Message);
        Assert.Contains("no existeix",
            Assert.Throws<DatabaseException>(() => _agendas.Save(null, "Altra", new DateOnly(2026, 1, 1), true, [_bagB], [_adult[0], 9999])).Message);
        Assert.Single(_agendas.List());
    }
}
