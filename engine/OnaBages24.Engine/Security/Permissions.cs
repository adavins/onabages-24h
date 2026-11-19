using OnaBages24.Data;

namespace OnaBages24.Engine.Security;

/// <summary>
/// Qui pot fer cada ordre (fase 5, pas 8; D-035, D-164) i com es diu al registre de canvis. Cada rol pot fer el que poden
/// fer els de sota. Les ordres de consulta són per a tothom, també sense sessió. Una ordre que no és en aquesta llista
/// només la pot fer l'administrador (per si se n'afegeix una i s'oblida de posar-la aquí: la prova ho comprova).
/// </summary>
public static class Permissions
{
    /// <summary>Una ordre: el rol mínim (null = tothom, també sense sessió) i el text del registre de canvis (null = no s'hi anota).</summary>
    public sealed record Rule(UserRole? Role, string? Summary);

    private static Rule Anyone => new(null, null);
    private static Rule Operator(string summary) => new(UserRole.Operator, summary);
    private static Rule Programmer(string summary) => new(UserRole.Programmer, summary);
    private static Rule Admin(string? summary) => new(UserRole.Administrator, summary);

    /// <summary>Totes les ordres del protocol (excepte <c>session.*</c>, que són per a tothom i les tracta la sessió).</summary>
    public static readonly IReadOnlyDictionary<string, Rule> Rules = new Dictionary<string, Rule>
    {
        // Consultes: tothom
        ["engine.hello"] = Anyone, ["engine.ping"] = Anyone, ["state.get"] = Anyone, ["devices.list"] = Anyone,
        ["output.get"] = Anyone, ["queue.get"] = Anyone, ["aux.get"] = Anyone, ["notes.get"] = Anyone, ["ducking.get"] = Anyone,
        ["processor.get"] = Anyone, ["ui.get"] = Anyone, ["ui.set"] = Anyone, // ui.set: preferències de la pantalla (zoom, columnes)
        ["library.list"] = Anyone, ["library.search"] = Anyone, ["library.filters"] = Anyone, ["library.get"] = Anyone,
        ["library.ids"] = Anyone, ["tags.list"] = Anyone, ["folders.get"] = Anyone, ["folders.status"] = Anyone,
        ["folders.log"] = Anyone, ["library.waveform"] = Anyone, ["library.missingFolders"] = Anyone,
        ["library.rerootPreview"] = Anyone, ["folders.renewPreview"] = Anyone, ["library.importStatus"] = Anyone,
        ["autodj.get"] = Anyone, ["rotations.list"] = Anyone, ["rotations.preview"] = Anyone, ["bags.list"] = Anyone, ["agendas.list"] = Anyone, ["agendas.preview"] = Anyone,
        ["separation.get"] = Anyone, ["calendar.get"] = Anyone, ["calendar.content"] = Anyone, ["campaigns.list"] = Anyone,
        ["campaigns.get"] = Anyone, ["campaigns.plan"] = Anyone, ["savedCalendars.list"] = Anyone, ["season.get"] = Anyone,
        ["validity.list"] = Anyone, ["station.get"] = Anyone, ["reports.programming"] = Anyone, ["reports.certificate"] = Anyone,
        ["reserve.get"] = Anyone, ["history.list"] = Anyone, ["incidents.list"] = Anyone, ["silence.get"] = Anyone,
        ["emergency.get"] = Anyone, ["nowplaying.get"] = Anyone, ["scheduler.list"] = Anyone, ["scheduler.get"] = Anyone,
        ["scheduler.preview"] = Anyone, ["scheduler.occurrences"] = Anyone, ["scheduler.log"] = Anyone, ["scheduler.status"] = Anyone,

        // Operador: la pantalla d'emissió
        ["output.set"] = Operator("Ha canviat la sortida d'àudio"),
        ["queue.insert"] = Operator("Ha afegit a la cua"),
        ["queue.remove"] = Operator("Ha tret de la cua"),
        ["queue.move"] = Operator("Ha mogut a la cua"),
        ["queue.regenerate"] = Operator("Ha refet la cua"),
        ["player.playItem"] = Operator("Ha fet sonar una pista de la cua"),
        ["player.next"] = Operator("Ha passat a la pista següent"),
        ["player.pause"] = Operator("Ha posat en pausa"),
        ["player.resume"] = Operator("Ha tret la pausa"),
        ["player.seek"] = Operator("Ha mogut la posició de la pista"),
        ["aux.load"] = Operator("Ha carregat un auxiliar"),
        ["aux.unload"] = Operator("Ha buidat un auxiliar"),
        ["aux.play"] = Operator("Ha fet sonar un auxiliar"),
        ["aux.stop"] = Operator("Ha aturat un auxiliar"),
        ["notes.set"] = new(UserRole.Operator, null), // es desa sol mentre s'escriu: no s'anota cada lletra
        ["autodj.set"] = Operator("Ha canviat l'AutoDJ"),
        ["scheduler.pause"] = Operator("Ha pausat el planificador"),
        ["scheduler.resume"] = Operator("Ha reprès el planificador"),

        // Programador: la programació i la biblioteca
        ["library.update"] = Programmer("Ha desat la pista"),
        ["library.relink"] = Programmer("Ha canviat el fitxer de la pista"),
        ["library.addStream"] = Programmer("Ha afegit un stream a la biblioteca"),
        ["library.delete"] = Programmer("Ha esborrat pistes"),
        ["library.bulk"] = Programmer("Ha canviat diverses pistes alhora"),
        ["library.setCues"] = Programmer("Ha desat els punts de la pista"),
        ["library.recalculateCues"] = Programmer("Ha recalculat els punts"),
        ["library.reroot"] = Programmer("Ha canviat la carpeta de les pistes"),
        ["library.import"] = Programmer("Ha importat pistes"),
        ["library.importCancel"] = Programmer("Ha aturat la importació"),
        ["category.save"] = Programmer("Ha desat la categoria"),
        ["category.delete"] = Programmer("Ha esborrat la categoria"),
        ["subcategory.save"] = Programmer("Ha desat la subcategoria"),
        ["subcategory.delete"] = Programmer("Ha esborrat la subcategoria"),
        ["subcategory.empty"] = Programmer("Ha buidat la subcategoria"),
        ["tags.rename"] = Programmer("Ha canviat el nom d'una etiqueta"),
        ["tags.merge"] = Programmer("Ha ajuntat etiquetes"),
        ["tags.delete"] = Programmer("Ha esborrat una etiqueta"),
        ["folders.set"] = Programmer("Ha canviat les carpetes vigilades"),
        ["folders.scan"] = Programmer("Ha revisat les carpetes vigilades"),
        ["folders.renew"] = Programmer("Ha renovat una carpeta vigilada"),
        ["sweepers.set"] = Programmer("Ha canviat les falques de pas"),
        ["rotations.save"] = Programmer("Ha desat la rotació"),
        ["rotations.delete"] = Programmer("Ha esborrat la rotació"),
        ["bags.save"] = Programmer("Ha desat la bossa"),
        ["bags.set"] = Programmer("Ha canviat la subcategoria d'una bossa"),
        ["bags.delete"] = Programmer("Ha esborrat la bossa"),
        ["agendas.save"] = Programmer("Ha desat l'agenda de bosses"),
        ["agendas.delete"] = Programmer("Ha esborrat l'agenda de bosses"),
        ["separation.set"] = Programmer("Ha canviat la separació d'artistes i títols"),
        ["calendar.set"] = Programmer("Ha canviat el calendari"),
        ["calendar.move"] = Programmer("Ha mogut una pista al calendari"),
        ["campaigns.save"] = Programmer("Ha desat la campanya"),
        ["campaigns.delete"] = Programmer("Ha esborrat la campanya"),
        ["campaigns.exportPackage"] = Programmer("Ha exportat el paquet de la campanya"),
        ["campaigns.importPackage"] = Programmer("Ha importat un paquet de campanya"),
        ["savedCalendars.save"] = Programmer("Ha desat el calendari"),
        ["savedCalendars.delete"] = Programmer("Ha esborrat el calendari desat"),
        ["savedCalendars.apply"] = Programmer("Ha aplicat un calendari desat"),
        ["validity.removeFromEvents"] = Programmer("Ha tret dels esdeveniments una pista o campanya caducada"),
        // Manteniment de la programació (D-194): l'anàlisi, només per a qui la pot aplicar (no s'anota: només llegeix)
        ["maintenance.scan"] = new(UserRole.Programmer, null),
        // Aplicar-lo: cada canvi s'anota a part (MaintenanceApply), amb el motiu; aquí no cal una entrada general
        ["maintenance.apply"] = new(UserRole.Programmer, null),
        ["season.set"] = Programmer("Ha canviat les dates de la temporada"),
        ["reserve.set"] = Programmer("Ha canviat la reserva de l'AutoDJ"),
        ["scheduler.save"] = Programmer("Ha desat l'esdeveniment"),
        ["scheduler.delete"] = Programmer("Ha esborrat l'esdeveniment"),

        // Administrador: la configuració, els usuaris i les còpies
        ["engine.shutdown"] = Admin("Ha aturat el motor"),
        ["ducking.set"] = Admin("Ha canviat l'atenuació"),
        ["processor.set"] = Admin("Ha canviat el processador de sortida"),
        ["station.set"] = Admin("Ha canviat les dades de l'emissora"),
        ["silence.set"] = Admin("Ha canviat el silenci a la sortida"),
        ["emergency.set"] = Admin("Ha canviat la carpeta de pistes d'emergència"),
        ["mail.get"] = Admin(null),
        ["mail.set"] = Admin("Ha canviat els avisos per correu"),
        ["mail.test"] = Admin(null),
        ["nowplaying.set"] = Admin("Ha canviat el títol per a BUTT"),
        ["nowplaying.fixed.set"] = Operator("Ha posat un títol fix per a BUTT"),
        ["nowplaying.fixed.clear"] = Operator("Ha tret el títol fix per a BUTT"),
        ["backups.list"] = Admin(null),
        ["backups.inspect"] = Admin(null),
        ["backups.create"] = Admin("Ha fet una còpia de seguretat"),
        ["backups.restore"] = Admin("Ha restaurat una còpia de seguretat"),
        ["users.list"] = Admin(null),
        ["users.save"] = Admin("Ha desat l'usuari"),
        ["security.get"] = Admin(null),
        ["security.set"] = Admin("Ha canviat el temps d'inactivitat de la sessió"),
        ["changes.list"] = Admin(null),
        // El registre unificat (D-167): tothom, però els canvis i les sessions només els veu l'administrador (ho filtra log.list)
        ["log.list"] = Anyone,
        // La sessió (session.*) és per a tothom; només s'anota el canvi de contrasenya
        ["session.changePassword"] = new(null, "Ha canviat la seva contrasenya"),
    };

    /// <summary>La regla d'una ordre (una que no hi és: només l'administrador, i sense anotar-la).</summary>
    public static Rule For(string command) => Rules.TryGetValue(command, out var rule) ? rule : new Rule(UserRole.Administrator, null);

    /// <summary>El nom d'un rol en català.</summary>
    public static string RoleName(UserRole role) => role switch
    {
        UserRole.Administrator => "Administrador",
        UserRole.Programmer => "Programador",
        _ => "Operador",
    };
}
