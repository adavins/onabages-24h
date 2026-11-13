using System.Text.Json;
using OnaBages24.Data;
using OnaBages24.Data.RadioDj;

namespace OnaBages24.Tests.Data;

/// <summary>
/// Migració dels esdeveniments de RadioDJ (fase 2, pas 6; docs/planificador.md, secció 6; D-066), amb el fitxer de
/// referència del repositori (<c>docs/radiodj</c>) i una biblioteca inventada amb les pistes que fan servir.
/// </summary>
public sealed class RadioDjEventsTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly Rotations _rotations;
    private readonly Events _events;
    private readonly MySqlDump _dump;
    private readonly Dictionary<long, long> _ids = []; // id de RadioDJ → id nostre

    public RadioDjEventsTests()
    {
        _db = Database.Open(_temp.File("esdeveniments.db"));
        _library = new Library(_db);
        _rotations = new Rotations(_db);
        _events = new Events(_db);
        string docs = RadioDjImportTests.RadioDjDocs();
        _dump = MySqlDump.Load(Path.Combine(docs, "esquema.sql"), Path.Combine(docs, "dades-referencia.sql"));
        // Categories, subcategories i rotacions (el fitxer de referència no porta les pistes)
        RadioDjImporter.ImportLibrary(_dump, _library, new RadioDjImportOptions { CheckFiles = false }, _rotations);

        // Les pistes que fan servir els esdeveniments que es comproven (la resta no hi són: surten a l'informe)
        Add(42, @"C:\RadioDJ\hores\SENYALS HORARIS.mp3", "SENYALS HORARIS", TrackType.TimeSignal);
        Add(20, @"C:\RadioDJ\hores\02.mp3", "02", TrackType.TimeAnnouncement);
        Add(28, @"C:\RadioDJ\hores\10.mp3", "10", TrackType.TimeAnnouncement);
        Add(41, @"C:\RadioDJ\hores\23.mp3", "23", TrackType.TimeAnnouncement);
        Add(10213, @"D:\EMISSIO\caretes\meteo\intro_temps.mp3", "El temps (intro)", TrackType.Promo);
        Add(9494, @"D:\EMISSIO\caretes\meteo\meteo.mp3", "Meteo", TrackType.Program);
        Add(10214, @"D:\EMISSIO\caretes\meteo\cua_temps.mp3", "El temps (cua)", TrackType.Promo);
        Add(25610, @"D:\continuitat\capsules\inf1.mp3", "Notícies (1)", TrackType.Program);
        Add(6774, "http://centauri.shoutca.st:8259", "Directe", TrackType.Stream);
        Add(2510, @"D:\EMISSIO\caretes\formules\FOR - INICI Dance90 (J Bosch).mp3", "INICI", TrackType.StationId);
        Add(2511, @"D:\EMISSIO\caretes\formules\FOR - INICI Elektro90.mp3", "INICI", TrackType.StationId);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private void Add(long radioDjId, string path, string title, TrackType type) =>
        _ids[radioDjId] = _library.AddTrack(new Track { Path = path, Title = title, Type = type, RadioDjId = radioDjId });

    private RadioDjEventsReport Import(bool allDisabled = false) =>
        RadioDjEvents.Import(_dump, _library, _rotations, _events, new RadioDjEventsOptions { AllDisabled = allDisabled });

    /// <summary>L'esdeveniment d'aquesta hora que sona aquests dies (exactament).</summary>
    private ScheduledEvent Event(int hour, int minute, int days) =>
        Assert.Single(_events.List(), e => e.StartTime == new TimeOnly(hour, minute) && e.DaysMask == days && e.Repeat == EventRepeat.None);

    private static long Param(EventAction action, string name)
    {
        using var json = JsonDocument.Parse(action.Params);
        return json.RootElement.GetProperty(name).GetInt64();
    }

    private const int Weekend = 0b110_0000;
    private const int MondayToThursday = 0b000_1111;

    [Fact]
    public void Un_TALLA_amb_les_pistes_en_l_ordre_real_i_els_dies_agrupats()
    {
        Import();

        // A RadioDJ: Clear, cua_temps|Top, meteo|Top, intro_temps|Top, 10.mp3|Top, Play (l'ordre escrit és l'invers)
        var ev = Event(10, 0, Weekdays.MondayToFriday);
        Assert.Equal(EventMode.Cut, ev.Mode);
        Assert.True(ev.ClearQueue);
        Assert.Equal(EventActionType.Track, Assert.Single(ev.Actions.Select(a => a.Type).Distinct()));
        Assert.Equal([_ids[28], _ids[10213], _ids[9494], _ids[10214]], ev.Actions.Select(a => Param(a, "songId")));
        Assert.Equal("El temps (intro) + Meteo + El temps (cua)", ev.Name); // la locució de l'hora no surt al nom
        Assert.True(ev.Enabled);
        Assert.StartsWith(RadioDjEvents.NotesMarker, ev.Notes);
    }

    [Fact]
    public void Els_senyals_horaris_sols_son_un_SUPERPOSA_pel_reproductor_1()
    {
        Import();

        var ev = Event(0, 0, Weekdays.All);
        Assert.Equal(EventMode.Overlay, ev.Mode);
        Assert.Equal("Senyals horaris", ev.Name);
        var action = Assert.Single(ev.Actions);
        Assert.Equal(EventActionType.Auxiliary, action.Type);
        Assert.Equal(_ids[42], Param(action, "songId"));
        Assert.Equal(1, Param(action, "player"));
    }

    [Fact]
    public void Senyals_i_una_pista_a_la_cua_son_un_ENCUA_amb_una_accio_AUXILIAR()
    {
        Import();

        var ev = Event(11, 0, MondayToThursday);
        Assert.Equal(EventMode.Queue, ev.Mode);
        Assert.False(ev.ClearQueue);
        Assert.Equal([EventActionType.Auxiliary, EventActionType.Track], ev.Actions.Select(a => a.Type));
        Assert.Equal(_ids[25610], Param(ev.Actions[1], "songId"));
        Assert.Equal("Senyals horaris + Notícies (1)", ev.Name);
    }

    [Fact]
    public void Un_stream_es_una_accio_STREAM_amb_la_durada_de_les_franges_desactivades_que_el_segueixen()
    {
        Import();

        // Dl-dv a les 8:00: el stream, i després 8:30, 9:00 i 9:30 desactivades → fins a les 10:00
        var ev = Event(8, 0, Weekdays.MondayToFriday);
        Assert.Equal(EventMode.Cut, ev.Mode);
        Assert.Equal(7200, ev.MaxDurationSeconds);
        var action = Assert.Single(ev.Actions); // sense Assisted Enable/Disable
        Assert.Equal(EventActionType.Stream, action.Type);
        using var json = JsonDocument.Parse(action.Params);
        Assert.Equal(_ids[6774], json.RootElement.GetProperty("songId").GetInt64()); // el stream de la biblioteca (D-108)
        Assert.Equal(7200, json.RootElement.GetProperty("durada_seg").GetInt32());
        Assert.Equal("Directe", ev.Name);
    }

    [Fact]
    public void La_rotacio_i_els_sweepers_es_converteixen_i_la_rotacio_surt_al_nom()
    {
        Import();

        var weekdays = Event(23, 0, Weekdays.MondayToFriday);
        var weekend = Event(23, 0, Weekend);
        Assert.Equal("INICI · rotació Dance90", weekdays.Name);
        Assert.Equal("INICI · rotació Elektro90", weekend.Name);
        Assert.Equal([EventActionType.Track, EventActionType.Track, EventActionType.Sweepers, EventActionType.Rotation],
            weekdays.Actions.Select(a => a.Type));
        Assert.Equal([_ids[41], _ids[2510]], weekdays.Actions.Take(2).Select(a => Param(a, "songId"))); // 23.mp3 i després la fórmula
        long dance90 = _rotations.List().Single(r => r.Name == "Dance90").Id;
        Assert.Equal(dance90, Param(weekdays.Actions[3], "rotacioId"));
    }

    [Fact]
    public void Un_TALLA_amb_les_pistes_que_no_son_a_la_biblioteca_continua_sent_un_TALLA_i_l_informe_ho_diu()
    {
        var report = Import();

        // Dl-dj a les 13:00: enprimerpla.mp3 (9952) i 13.mp3 (31), cap dels dos a la biblioteca de la prova
        var ev = Event(13, 0, MondayToThursday);
        Assert.Equal(EventMode.Cut, ev.Mode);
        Assert.Empty(ev.Actions);
        Assert.Contains(9952L, report.MissingTracks.Keys);
        Assert.Equal("enprimerpla", ev.Name); // el nom del fitxer, encara que no hi sigui
    }

    [Fact]
    public void Els_senyals_de_les_mitges_hores_son_cada_hora_i_queden_desactivats()
    {
        Import();

        var ev = Assert.Single(_events.List(), e => e.Repeat == EventRepeat.EveryHour);
        Assert.Equal(new TimeOnly(0, 30), ev.StartTime);
        Assert.Null(ev.HoursMask); // totes les hores
        Assert.Equal(Weekdays.All, ev.DaysMask);
        Assert.False(ev.Enabled);
        Assert.Equal(EventMode.Overlay, ev.Mode);
    }

    [Fact]
    public void No_queda_cap_ordre_de_RadioDJ_sense_convertir_i_l_informe_compta_els_agrupaments()
    {
        var report = Import();

        Assert.True(report.Created is > 70 and < 100, $"{report.Created} esdeveniments");
        Assert.Equal(195, report.Slots); // 194 franges setmanals + els senyals de les :30
        Assert.Equal(report.Created, _events.List().Count);
        Assert.Contains(report.Groups, g => g.StartsWith("00:00 cada dia: «Senyals horaris» (7 franges)"));
        Assert.Contains(report.NearlySame, n => n.StartsWith("08:00: dl-dv TALLA «Directe» · ds, dg SUPERPOSA"));
        Assert.Single(report.Warnings); // només el del servidor d'streaming, que no es migra
        Assert.DoesNotContain(_events.List(), e => e.Mode == EventMode.System);
    }

    [Fact]
    public void Tornar_a_importar_substitueix_els_importats_i_no_toca_els_fets_a_ma()
    {
        _events.Save(new ScheduledEvent { Name = "Fet a mà", Mode = EventMode.Queue, StartTime = new TimeOnly(9, 15) });
        var first = Import();
        var second = Import();

        Assert.Equal(first.Created, second.Replaced);
        Assert.Equal(first.Created + 1, _events.List().Count);
        Assert.Contains(_events.List(), e => e.Name == "Fet a mà");
    }

    [Fact]
    public void Amb_l_opcio_de_desactivats_no_se_n_dispara_cap()
    {
        var report = Import(allDisabled: true);

        Assert.True(report.Created > 0);
        Assert.All(_events.List(), e => Assert.False(e.Enabled));
        Assert.Contains("Tots desactivats", report.ToString());
    }
}
