using System.Security.Cryptography;
using OnaBages24.Data;

namespace OnaBages24.Engine.Security;

/// <summary>Una sessió oberta: qui és, quin rol té i quan ha fet l'última cosa.</summary>
public sealed record Session(string Id, long UserId, string Name, string DisplayName, UserRole Role, bool MustChangePassword,
    DateTimeOffset LastActivity);

/// <summary>Per què no s'ha pogut fer una cosa de la sessió (el text va a la interfície tal com és).</summary>
public sealed class SessionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Les sessions (fase 5, pas 8; D-035, D-164). Cada connexió de la interfície en pot tenir una (la interfície guarda l'id i
/// la recupera si es reconnecta; si el motor es reinicia, cal tornar a entrar). Es tanca sola després d'un temps sense
/// activitat. Mentre no hi ha cap usuari actiu, no cal iniciar sessió: tot es pot fer com abans.
/// Cinc intents fallits seguits amb el mateix nom el bloquegen 5 minuts (per no poder endevinar la contrasenya provant).
/// </summary>
public sealed class SessionManager
{
    public static readonly TimeSpan StandardInactivity = TimeSpan.FromMinutes(30);
    public const int MaxFailures = 5;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(5);

    private readonly UserStore _users;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _iterations;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, (int Count, DateTimeOffset Last)> _failures = new(StringComparer.OrdinalIgnoreCase);
    private bool _usersExist;

    /// <param name="iterations">Les iteracions de PBKDF2 per a les contrasenyes noves (les proves en fan servir poques).</param>
    public SessionManager(UserStore users, Func<DateTimeOffset>? clock = null, int iterations = PasswordHash.StandardIterations)
    {
        _users = users;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _iterations = iterations;
        Refresh();
    }

    /// <summary>Quanta estona sense activitat tanca la sessió.</summary>
    public TimeSpan Inactivity { get; set; } = StandardInactivity;

    /// <summary>Si hi ha algun usuari actiu (si no, no cal iniciar sessió).</summary>
    public bool UsersExist { get { lock (_sync) return _usersExist; } }

    /// <summary>Torna a llegir els usuaris (en arrencar, en desar-ne un i després de restaurar una còpia).</summary>
    public void Refresh()
    {
        bool exist = _users.AnyActive();
        lock (_sync) _usersExist = exist;
    }

    /// <summary>Inicia sessió. Si no pot, llança <see cref="SessionException"/> amb el motiu.</summary>
    public Session Login(string name, string password)
    {
        name = name.Trim();
        var now = _clock();
        lock (_sync)
        {
            if (_failures.TryGetValue(name, out var failures) && failures.Count >= MaxFailures && now - failures.Last < LockTime)
                throw new SessionException("locked", $"Massa intents fallits: torna-ho a provar d'aquí a {Math.Ceiling((LockTime - (now - failures.Last)).TotalMinutes)} min.");
        }
        var user = _users.GetByName(name);
        // El mateix error si el nom no existeix o la contrasenya no és bona: no es diu quins noms existeixen
        if (user is null || !user.Active || !PasswordHash.Verify(password, user.Hash, user.Salt, user.Iterations))
        {
            lock (_sync)
            {
                var previous = _failures.GetValueOrDefault(name);
                _failures[name] = (now - previous.Last < LockTime ? previous.Count + 1 : 1, now);
            }
            throw new SessionException("bad_login", "El nom o la contrasenya no són correctes.");
        }
        _users.Save(user with { LastLoginAt = now });
        var session = new Session(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), user.Id, user.Name, user.DisplayName,
            user.Role, user.Provisional, now);
        lock (_sync)
        {
            _failures.Remove(name);
            _sessions[session.Id] = session;
        }
        return session;
    }

    /// <summary>La sessió, si encara és oberta (una de caducada es tanca ara).</summary>
    public Session? Get(string? id)
    {
        if (id is null) return null;
        var now = _clock();
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out var session)) return null;
            if (now - session.LastActivity < Inactivity) return session;
            _sessions.Remove(id);
            return null;
        }
    }

    /// <summary>Hi ha hagut activitat (el teclat o el ratolí a la interfície, o una ordre que canvia alguna cosa).</summary>
    public Session? Touch(string? id)
    {
        if (Get(id) is not { } session) return null;
        var touched = session with { LastActivity = _clock() };
        lock (_sync) _sessions[touched.Id] = touched;
        return touched;
    }

    public void Logout(string? id)
    {
        if (id is null) return;
        lock (_sync) _sessions.Remove(id);
    }

    /// <summary>Tanca les sessions que fa massa estona que no tenen activitat. Retorna els ids, per avisar-ne les interfícies.</summary>
    public IReadOnlyList<string> ExpireDue()
    {
        var now = _clock();
        lock (_sync)
        {
            var due = _sessions.Values.Where(s => now - s.LastActivity >= Inactivity).Select(s => s.Id).ToList();
            foreach (string id in due) _sessions.Remove(id);
            return due;
        }
    }

    /// <summary>Canvia la contrasenya de qui té la sessió (cal la d'ara). La provisional deixa de ser-ho.</summary>
    public Session ChangePassword(string sessionId, string current, string replacement)
    {
        var session = Get(sessionId) ?? throw new SessionException("not_logged_in", "Cal iniciar sessió.");
        var user = _users.Get(session.UserId) ?? throw new SessionException("not_logged_in", "Aquest usuari ja no existeix.");
        if (!PasswordHash.Verify(current, user.Hash, user.Salt, user.Iterations))
            throw new SessionException("bad_login", "La contrasenya d'ara no és correcta.");
        if (PasswordHash.Problem(replacement) is { } problem) throw new SessionException("bad_params", problem);
        if (replacement == current) throw new SessionException("bad_params", "La contrasenya nova ha de ser diferent de la d'ara.");
        var (hash, salt, iterations) = PasswordHash.Create(replacement, _iterations);
        _users.Save(user with { Hash = hash, Salt = salt, Iterations = iterations, Provisional = false });
        var updated = session with { MustChangePassword = false, LastActivity = _clock() };
        lock (_sync) _sessions[updated.Id] = updated;
        return updated;
    }

    /// <summary>
    /// Un usuari nou o canviat (des de la gestió d'usuaris). <paramref name="password"/>: la nova (obligatòria per a un de
    /// nou), o null per no canviar-la. No es pot deixar sense cap administrador actiu.
    /// </summary>
    public User SaveUser(User user, string? password)
    {
        if (user.Name.Trim().Length == 0) throw new SessionException("bad_params", "Falta el nom de l'usuari.");
        user = user with { Name = user.Name.Trim(), FullName = user.FullName.Trim() };
        var existing = user.Id == 0 ? null : _users.Get(user.Id) ?? throw new SessionException("bad_params", $"No hi ha cap usuari amb l'id {user.Id}.");
        if (existing is null && password is null) throw new SessionException("bad_params", "Un usuari nou necessita una contrasenya.");
        if (password is not null)
        {
            if (PasswordHash.Problem(password) is { } problem) throw new SessionException("bad_params", problem);
            var (hash, salt, iterations) = PasswordHash.Create(password, _iterations);
            user = user with { Hash = hash, Salt = salt, Iterations = iterations };
        }
        else user = user with { Hash = existing!.Hash, Salt = existing.Salt, Iterations = existing.Iterations, Provisional = existing.Provisional };
        user = user with { CreatedAt = existing?.CreatedAt ?? _clock(), LastLoginAt = existing?.LastLoginAt };

        // El primer usuari ha de ser administrador, i no es pot treure l'últim administrador actiu
        var admins = _users.List().Where(u => u.Active && u.Role == UserRole.Administrator && u.Id != user.Id).ToList();
        bool wasAnyActive = _users.AnyActive();
        if (!wasAnyActive && (user.Role != UserRole.Administrator || !user.Active))
            throw new SessionException("bad_params", "El primer usuari ha de ser un administrador actiu.");
        if (wasAnyActive && admins.Count == 0 && (user.Role != UserRole.Administrator || !user.Active))
            throw new SessionException("bad_params", "Ha de quedar com a mínim un administrador actiu.");

        long id = _users.Save(user);
        user = user with { Id = id };
        // Les sessions obertes d'aquest usuari: el rol nou, o es tanquen si s'ha desactivat
        lock (_sync)
        {
            foreach (var session in _sessions.Values.Where(s => s.UserId == id).ToList())
            {
                if (!user.Active) _sessions.Remove(session.Id);
                else _sessions[session.Id] = session with { Name = user.Name, DisplayName = user.DisplayName, Role = user.Role };
            }
        }
        Refresh();
        return user;
    }

    /// <summary>
    /// Restableix la contrasenya d'un administrador (des de l'ordinador, amb el motor aturat: <c>--restableix-administrador</c>).
    /// Si no n'hi ha cap d'actiu, en crea un («admin»). Retorna el nom i la contrasenya provisional, que s'haurà de canviar.
    /// </summary>
    public static (string Name, string Password) ResetAdministrator(UserStore users, DateTimeOffset now, int iterations = PasswordHash.StandardIterations)
    {
        string password = PasswordHash.Provisional();
        var (hash, salt, count) = PasswordHash.Create(password, iterations);
        var admin = users.List().FirstOrDefault(u => u.Active && u.Role == UserRole.Administrator)
            ?? users.GetByName("admin")
            ?? new User { Name = "admin", FullName = "Administrador", CreatedAt = now };
        admin = admin with { Role = UserRole.Administrator, Active = true, Hash = hash, Salt = salt, Iterations = count, Provisional = true };
        users.Save(admin);
        return (admin.Name, password);
    }
}
