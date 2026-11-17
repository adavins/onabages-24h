using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Security;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Usuaris, sessions, permisos i registre de canvis (fase 5, pas 8a; D-164).</summary>
public sealed class UsersTests : IDisposable
{
    /// <summary>Poques iteracions: les contrasenyes de debò en fan 600.000 (unes dècimes de segon cadascuna).</summary>
    private const int FastIterations = 1000;

    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly UserStore _users;
    private DateTimeOffset _now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private readonly SessionManager _sessions;
    private readonly CommandRouter _router;

    public UsersTests()
    {
        _db = Database.Open(_temp.File("ob24.db"));
        _users = new UserStore(_db);
        _sessions = new SessionManager(_users, () => _now, FastIterations);
        _router = new CommandRouter(new EngineState(), () => { }, new Library(_db), sessions: _sessions, users: _users);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private User NewUser(string name, UserRole role, string password = "contrasenya", bool active = true) =>
        _sessions.SaveUser(new User { Name = name, Role = role, Active = active }, password);

    private JsonObject Send(ClientContext client, string cmd, JsonObject? args = null)
    {
        var msg = new JsonObject { ["v"] = 1, ["id"] = 1, ["cmd"] = cmd };
        foreach (var (key, value) in args ?? []) msg[key] = value?.DeepClone();
        return _router.Handle(msg.ToJsonString(), client);
    }

    private static string? ErrorCode(JsonObject response) => response["error"]?["code"]?.GetValue<string>();

    [Fact]
    public void Totes_les_ordres_del_protocol_tenen_permisos()
    {
        // Les ordres de CommandRouter (les línies «"àmbit.acció" => …»); session.* les tracta la sessió
        string folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "OnaBages24.Engine", "Protocol");
        var commands = Directory.GetFiles(folder, "CommandRouter*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "\"([a-zA-Z]+\\.[a-zA-Z]+)\" =>").Select(m => m.Groups[1].Value))
            .Distinct().ToList();
        Assert.True(commands.Count > 100);
        var missing = commands.Where(c => !Permissions.Rules.ContainsKey(c)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Sense_usuaris_tot_es_pot_fer_i_el_primer_ha_de_ser_administrador()
    {
        var client = new ClientContext();
        Assert.False(_sessions.UsersExist);
        Assert.Null(ErrorCode(Send(client, "season.set", new() { ["startDate"] = null, ["endDate"] = null })));

        Assert.Throws<SessionException>(() => NewUser("operador", UserRole.Operator));
        var error = Assert.Throws<SessionException>(() => NewUser("admin", UserRole.Administrator, "curta"));
        Assert.Contains("8 caràcters", error.Message);
        NewUser("admin", UserRole.Administrator);
        Assert.True(_sessions.UsersExist);
        Assert.Equal("not_logged_in", ErrorCode(Send(client, "season.set", new() { ["startDate"] = null, ["endDate"] = null })));
    }

    [Fact]
    public void Cada_rol_pot_fer_el_seu_i_les_consultes_son_per_a_tothom()
    {
        NewUser("admin", UserRole.Administrator);
        NewUser("operadora", UserRole.Operator);
        NewUser("programador", UserRole.Programmer);
        var client = new ClientContext();

        // Mode de consulta: es pot llegir, no canviar
        Assert.Null(ErrorCode(Send(client, "station.get")));
        Assert.Null(ErrorCode(Send(client, "season.get")));
        Assert.Equal("not_logged_in", ErrorCode(Send(client, "player.next")));

        // Operadora: la cua sí, la programació no
        var login = Send(client, "session.login", new() { ["user"] = "OPERADORA", ["password"] = "contrasenya" }); // sense distingir majúscules
        Assert.Equal("operator", login["result"]!["session"]!["role"]!.GetValue<string>());
        Assert.Equal("bad_params", ErrorCode(Send(client, "queue.remove"))); // pot, però li falten paràmetres
        var forbidden = Send(client, "season.set", new() { ["startDate"] = null, ["endDate"] = null });
        Assert.Equal("forbidden", ErrorCode(forbidden));
        Assert.Equal("El teu rol (Operador) no ho permet: cal ser programador.", forbidden["error"]!["message"]!.GetValue<string>());

        // Programador: la programació sí, la configuració no
        Send(client, "session.login", new() { ["user"] = "programador", ["password"] = "contrasenya" });
        Assert.Null(ErrorCode(Send(client, "season.set", new() { ["startDate"] = null, ["endDate"] = null })));
        Assert.Equal("forbidden", ErrorCode(Send(client, "users.list")));

        // Administrador: tot
        Send(client, "session.login", new() { ["user"] = "admin", ["password"] = "contrasenya" });
        Assert.Equal(3, Send(client, "users.list")["result"]!["users"]!.AsArray().Count);

        // Tancar la sessió: torna al mode de consulta
        Send(client, "session.logout");
        Assert.Equal("not_logged_in", ErrorCode(Send(client, "users.list")));
    }

    [Fact]
    public void La_sessio_es_tanca_per_inactivitat_i_es_pot_recuperar_si_es_reconnecta()
    {
        NewUser("admin", UserRole.Administrator);
        var client = new ClientContext();
        string sessionId = Send(client, "session.login", new() { ["user"] = "admin", ["password"] = "contrasenya" })["result"]!["session"]!["id"]!.GetValue<string>();

        // Es perd la connexió: una de nova la recupera
        var reconnected = new ClientContext();
        Assert.Null(ErrorCode(Send(reconnected, "session.resume", new() { ["sessionId"] = sessionId })));
        Assert.Null(ErrorCode(Send(reconnected, "users.list")));

        // L'activitat (session.touch o una ordre que canvia alguna cosa) la manté oberta
        _now += TimeSpan.FromMinutes(20);
        Send(reconnected, "session.touch");
        _now += TimeSpan.FromMinutes(20);
        Assert.Empty(_sessions.ExpireDue());
        _now += TimeSpan.FromMinutes(11);
        Assert.Equal([sessionId], _sessions.ExpireDue());
        Assert.Equal("not_logged_in", ErrorCode(Send(reconnected, "users.list")));
        Assert.Equal("not_logged_in", ErrorCode(Send(new ClientContext(), "session.resume", new() { ["sessionId"] = sessionId })));
    }

    [Fact]
    public void Massa_intents_fallits_bloquegen_el_nom_uns_minuts()
    {
        NewUser("admin", UserRole.Administrator);
        for (int i = 0; i < SessionManager.MaxFailures; i++)
        {
            var error = Assert.Throws<SessionException>(() => _sessions.Login("admin", "dolenta"));
            Assert.Equal("El nom o la contrasenya no són correctes.", error.Message);
        }
        // Ara ni amb la bona
        Assert.Equal("locked", Assert.Throws<SessionException>(() => _sessions.Login("admin", "contrasenya")).Code);
        _now += SessionManager.LockTime;
        Assert.Equal("admin", _sessions.Login("admin", "contrasenya").Name);
        // Un nom que no existeix dona el mateix error que una contrasenya dolenta
        Assert.Equal("bad_login", Assert.Throws<SessionException>(() => _sessions.Login("ningu", "contrasenya")).Code);
    }

    [Fact]
    public void Ha_de_quedar_un_administrador_i_desactivar_un_usuari_li_tanca_la_sessio()
    {
        var admin = NewUser("admin", UserRole.Administrator);
        var operadora = NewUser("operadora", UserRole.Operator);
        Assert.Contains("administrador actiu", Assert.Throws<SessionException>(() => _sessions.SaveUser(admin with { Role = UserRole.Operator }, null)).Message);
        Assert.Throws<SessionException>(() => _sessions.SaveUser(admin with { Active = false }, null));
        Assert.Contains("Ja hi ha un usuari", Assert.Throws<DatabaseException>(() => NewUser("Admin", UserRole.Programmer)).Message);

        var session = _sessions.Login("operadora", "contrasenya");
        _sessions.SaveUser(operadora with { Role = UserRole.Programmer }, null); // la sessió pren el rol nou
        Assert.Equal(UserRole.Programmer, _sessions.Get(session.Id)!.Role);
        _sessions.SaveUser(operadora with { Active = false }, null);
        Assert.Null(_sessions.Get(session.Id));
        Assert.Throws<SessionException>(() => _sessions.Login("operadora", "contrasenya"));
        // Sense contrasenya nova, es conserva la d'abans
        _sessions.SaveUser(operadora with { Active = true, FullName = "Operadora de nit" }, null);
        Assert.Equal("Operadora de nit", _sessions.Login("operadora", "contrasenya").DisplayName);
    }

    [Fact]
    public void Una_contrasenya_provisional_s_ha_de_canviar_abans_de_fer_res()
    {
        NewUser("cap", UserRole.Administrator);
        var (name, password) = SessionManager.ResetAdministrator(_users, _now, FastIterations);
        Assert.Equal("cap", name); // l'administrador que ja hi havia
        _sessions.Refresh();

        var client = new ClientContext();
        var login = Send(client, "session.login", new() { ["user"] = name, ["password"] = password })["result"]!;
        Assert.True(login["session"]!["mustChangePassword"]!.GetValue<bool>());
        Assert.Equal("must_change_password", ErrorCode(Send(client, "users.list")));

        Assert.Equal("bad_login", ErrorCode(Send(client, "session.changePassword", new() { ["current"] = "no", ["new"] = "nova contrasenya" })));
        Assert.Equal("bad_params", ErrorCode(Send(client, "session.changePassword", new() { ["current"] = password, ["new"] = "curta" })));
        var changed = Send(client, "session.changePassword", new() { ["current"] = password, ["new"] = "nova contrasenya" })["result"]!;
        Assert.False(changed["session"]!["mustChangePassword"]!.GetValue<bool>());
        Assert.Null(ErrorCode(Send(client, "users.list")));
    }

    [Fact]
    public void Restablir_l_administrador_sense_cap_usuari_el_crea()
    {
        var (name, password) = SessionManager.ResetAdministrator(_users, _now, FastIterations);
        Assert.Equal("admin", name);
        Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", password);
        _sessions.Refresh();
        Assert.True(_sessions.Login("admin", password).MustChangePassword);
    }

    [Fact]
    public void El_registre_de_canvis_diu_qui_ha_fet_que_i_mai_les_contrasenyes()
    {
        NewUser("admin", UserRole.Administrator);
        var client = new ClientContext();
        Send(client, "session.login", new() { ["user"] = "admin", ["password"] = "contrasenya" });
        Assert.Null(ErrorCode(Send(client, "users.save", new() { ["name"] = "Joan", ["role"] = "operator", ["password"] = "secreta123" })));
        Assert.Null(ErrorCode(Send(client, "station.set", new() { ["name"] = "Ràdio Prova", ["logo"] = "data:image/png;base64,AAAA" })));
        Send(client, "notes.set", new() { ["text"] = "es desa sol: no s'anota" });
        Send(client, "station.get"); // consulta: no s'anota

        var list = Send(client, "changes.list")["result"]!;
        Assert.Equal(2, list["total"]!.GetValue<int>());
        var entries = list["entries"]!.AsArray();
        Assert.Equal("Ha canviat les dades de l'emissora «Ràdio Prova»", entries[0]!["summary"]!.GetValue<string>());
        Assert.Equal("admin", entries[0]!["user"]!.GetValue<string>());
        Assert.Contains("(imatge)", entries[0]!["detail"]!.GetValue<string>());
        Assert.Equal("Ha desat l'usuari «Joan»", entries[1]!["summary"]!.GetValue<string>());
        Assert.DoesNotContain("secreta123", entries[1]!["detail"]!.GetValue<string>());

        var filtered = Send(client, "changes.list", new() { ["commands"] = new JsonArray("users.") })["result"]!;
        Assert.Equal(1, filtered["total"]!.GetValue<int>());

        // Es conserva 2 anys
        Assert.Equal(0, _users.PurgeChanges(_now.AddYears(-2)));
        Assert.Equal(2, _users.PurgeChanges(DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Fact]
    public void El_temps_d_inactivitat_es_canvia_i_es_conserva()
    {
        NewUser("admin", UserRole.Administrator);
        var client = new ClientContext();
        Assert.Equal(30, Send(client, "session.get")["result"]!["inactivityMinutes"]!.GetValue<int>());
        Send(client, "session.login", new() { ["user"] = "admin", ["password"] = "contrasenya" });
        Assert.Equal(45, Send(client, "security.set", new() { ["inactivityMinutes"] = 45 })["result"]!["inactivityMinutes"]!.GetValue<int>());
        Assert.Equal(TimeSpan.FromMinutes(45), CommandRouter.ReadInactivity(new Library(_db)));
        Assert.Equal("bad_params", ErrorCode(Send(client, "security.set", new() { ["inactivityMinutes"] = 2 })));
    }

    [Fact]
    public void Restaurar_una_copia_no_toca_els_usuaris_ni_el_registre()
    {
        var backups = new Backups(_db, _temp.File("copies"));
        NewUser("admin", UserRole.Administrator);
        string copy = backups.Create(new DateTime(2026, 11, 17, 3, 0, 0));
        NewUser("nou", UserRole.Operator, "contrasenya nova");
        _users.AddChange(new ChangeEntry { At = _now, Command = "prova", Summary = "Després de la còpia" });

        backups.Restore(copy, new RestoreOptions { KeepHistory = false }, new DateTime(2026, 11, 17, 12, 0, 0));

        Assert.Equal(["admin", "nou"], _users.List().Select(u => u.Name).ToArray());
        Assert.Equal("Després de la còpia", _users.ListChanges(new ChangeFilter()).Entries.Single().Summary);
        Assert.Equal("nou", _sessions.Login("nou", "contrasenya nova").Name);
    }

    [Fact]
    public void La_contrasenya_es_desa_com_a_empremta()
    {
        var (hash, salt, iterations) = PasswordHash.Create("contrasenya", FastIterations);
        Assert.True(PasswordHash.Verify("contrasenya", hash, salt, iterations));
        Assert.False(PasswordHash.Verify("Contrasenya", hash, salt, iterations));
        Assert.NotEqual(hash, PasswordHash.Create("contrasenya", FastIterations).Hash); // sal diferent
        Assert.Equal(600_000, PasswordHash.StandardIterations);
    }
}
