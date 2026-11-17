using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Security;

namespace OnaBages24.Engine.Protocol;

/// <summary>Una connexió d'una interfície: la sessió que hi ha oberta (fase 5, pas 8; D-164).</summary>
public sealed class ClientContext
{
    /// <summary>L'id de la sessió d'aquesta connexió (null = sense sessió: mode de consulta).</summary>
    public string? SessionId { get; set; }
}

// Usuaris, sessions, permisos i registre de canvis (fase 5, pas 8; D-035, D-164).
public sealed partial class CommandRouter
{
    private SessionManager? _sessions;
    private UserStore? _users;

    public const string SecuritySettingKey = "sessio";
    public const int MinInactivityMinutes = 5, MaxInactivityMinutes = 240;

    /// <summary>Els minuts d'inactivitat desats (o els per defecte).</summary>
    public static TimeSpan ReadInactivity(Library? library)
    {
        try
        {
            if (library?.GetSetting(SecuritySettingKey) is { } json && JsonNode.Parse(json) is JsonObject saved
                && saved["minuts"] is JsonValue v && v.TryGetValue(out int minutes))
                return TimeSpan.FromMinutes(Math.Clamp(minutes, MinInactivityMinutes, MaxInactivityMinutes));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return SessionManager.StandardInactivity;
    }

    private static string RoleCode(UserRole role) => role switch
    {
        UserRole.Administrator => "administrator",
        UserRole.Programmer => "programmer",
        _ => "operator",
    };

    private static UserRole ParseRoleCode(string? code) => code switch
    {
        "administrator" => UserRole.Administrator,
        "programmer" => UserRole.Programmer,
        "operator" => UserRole.Operator,
        _ => throw new BadParamsException("El camp 'role' ha de ser 'operator', 'programmer' o 'administrator'."),
    };

    // --- Permisos: abans de cada ordre ---

    /// <summary>
    /// Si aquesta connexió pot fer l'ordre. Sense context (les proves, el motor mateix) o sense usuaris actius, tot es pot
    /// fer. Retorna la sessió (si n'hi ha) o l'error.
    /// </summary>
    private (Session? Session, JsonObject? Error) Authorize(JsonNode? id, string cmd, ClientContext? client)
    {
        if (client is null || _sessions is null) return (null, null);
        var rule = Permissions.For(cmd);
        var session = _sessions.Get(client.SessionId);
        if (session is null && client.SessionId is not null) client.SessionId = null; // ha caducat
        if (!_sessions.UsersExist || rule.Role is not { } required) return (session, null);
        if (session is null)
            return (null, Messages.Error(id, "not_logged_in", "Cal iniciar sessió per fer això."));
        if (session.MustChangePassword)
            return (session, Messages.Error(id, "must_change_password", "Abans has de canviar la contrasenya provisional."));
        if (session.Role < required)
            return (session, Messages.Error(id, "forbidden",
                $"El teu rol ({Permissions.RoleName(session.Role)}) no ho permet: cal ser {Permissions.RoleName(required).ToLowerInvariant()}."));
        return (_sessions.Touch(session.Id), null);
    }

    // --- Registre de canvis: després de cada ordre que ha canviat alguna cosa ---

    /// <summary>Anota l'ordre al registre de canvis, si és de les que s'hi anoten. Un error aquí no fa fallar l'ordre.</summary>
    private void LogChange(string cmd, JsonObject msg, Session? session)
    {
        if (_users is null || Permissions.For(cmd).Summary is not { } summary) return;
        try
        {
            _users.AddChange(new ChangeEntry
            {
                At = DateTimeOffset.UtcNow,
                UserId = session?.UserId,
                UserName = session?.Name ?? "",
                Command = cmd,
                Summary = summary + Subject(msg),
                Detail = Detail(msg),
            });
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException) { }
    }

    /// <summary>De què es tracta: « «Butlletí 13 h»» si l'ordre porta un nom o un títol; si no, l'id.</summary>
    private static string Subject(JsonObject msg)
    {
        foreach (string field in new[] { "name", "title" })
            if (ReadString(msg[field]) is { Length: > 0 } name) return $" «{name}»";
        foreach (var (key, value) in msg)
            if (key.EndsWith("Id", StringComparison.Ordinal) && value is JsonValue v && v.TryGetValue(out long number)) return $" ({key} {number})";
        return "";
    }

    /// <summary>Els paràmetres de l'ordre per al registre: sense contrasenyes ni imatges, i els textos llargs retallats.</summary>
    private static string? Detail(JsonObject msg)
    {
        var copy = new JsonObject();
        foreach (var (key, value) in msg)
        {
            if (key is "v" or "id" or "cmd") continue;
            if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || key is "current" or "new") copy[key] = "(no es desa)";
            else if (key == "logo") copy[key] = value is null ? null : "(imatge)";
            else if (value is JsonValue v && v.TryGetValue(out string? text) && text.Length > 300) copy[key] = text[..300] + "…";
            else copy[key] = value?.DeepClone();
        }
        if (copy.Count == 0) return null;
        string json = copy.ToJsonString();
        return json.Length > 4000 ? json[..4000] + "…" : json;
    }

    // --- session.* : per a tothom, amb la connexió ---

    private JsonObject SessionCommand(JsonNode? id, string cmd, JsonObject msg, ClientContext? client)
    {
        try
        {
            if (_sessions is null || client is null)
                return Messages.Error(id, "bad_params", "Les sessions només es fan servir des d'una connexió de la interfície.");
            var sessions = _sessions;
            switch (cmd)
            {
                case "session.get":
                    break;
                case "session.login":
                {
                    string name = ReadString(msg["user"]) ?? throw new BadParamsException("Falta el camp 'user'.");
                    string password = ReadString(msg["password"]) ?? throw new BadParamsException("Falta el camp 'password'.");
                    try
                    {
                        sessions.Logout(client.SessionId);
                        client.SessionId = sessions.Login(name, password).Id;
                    }
                    catch (SessionException ex) when (ex.Code == "bad_login")
                    {
                        Thread.Sleep(1000); // més lent d'endevinar provant (només aquesta connexió espera)
                        throw;
                    }
                    break;
                }
                case "session.resume":
                {
                    string sessionId = ReadString(msg["sessionId"]) ?? throw new BadParamsException("Falta el camp 'sessionId'.");
                    if (sessions.Touch(sessionId) is null)
                        return Messages.Error(id, "not_logged_in", "La sessió ja s'ha tancat: torna a iniciar-la.");
                    client.SessionId = sessionId;
                    break;
                }
                case "session.logout":
                    sessions.Logout(client.SessionId);
                    client.SessionId = null;
                    break;
                case "session.touch":
                    if (sessions.Touch(client.SessionId) is null) client.SessionId = null;
                    break;
                case "session.changePassword":
                {
                    string current = ReadString(msg["current"]) ?? throw new BadParamsException("Falta el camp 'current'.");
                    string replacement = ReadString(msg["new"]) ?? throw new BadParamsException("Falta el camp 'new'.");
                    var changed = sessions.ChangePassword(client.SessionId ?? "", current, replacement);
                    LogChange("session.changePassword", new JsonObject(), changed);
                    break;
                }
                default:
                    return Messages.Error(id, "unknown_command", $"Ordre desconeguda: {cmd}");
            }
            return Messages.Ok(id, SessionJson(client));
        }
        catch (SessionException ex)
        {
            return Messages.Error(id, ex.Code, ex.Message);
        }
        catch (BadParamsException ex)
        {
            return Messages.Error(id, "bad_params", ex.Message);
        }
        catch (DatabaseException ex)
        {
            return Messages.Error(id, "database_error", ex.Message);
        }
    }

    /// <summary>L'estat de la sessió d'una connexió (<c>session.get</c> i les respostes de <c>session.*</c>).</summary>
    public JsonObject SessionJson(ClientContext client)
    {
        var session = _sessions?.Get(client.SessionId);
        return new JsonObject
        {
            ["usersExist"] = _sessions?.UsersExist ?? false,
            ["inactivityMinutes"] = (int)(_sessions?.Inactivity ?? SessionManager.StandardInactivity).TotalMinutes,
            ["session"] = session is null ? null : new JsonObject
            {
                ["id"] = session.Id,
                ["user"] = session.Name,
                ["displayName"] = session.DisplayName,
                ["role"] = RoleCode(session.Role),
                ["roleName"] = Permissions.RoleName(session.Role),
                ["mustChangePassword"] = session.MustChangePassword,
            },
        };
    }

    // --- Usuaris (administrador) ---

    private SessionManager SessionsOrError() => _sessions ?? throw new BadParamsException("El motor no té usuaris.");
    private UserStore UsersOrError() => _users ?? throw new BadParamsException("El motor no té usuaris.");

    private static JsonObject UserJson(User user) => new()
    {
        ["id"] = user.Id,
        ["name"] = user.Name,
        ["fullName"] = user.FullName,
        ["role"] = RoleCode(user.Role),
        ["roleName"] = Permissions.RoleName(user.Role),
        ["active"] = user.Active,
        ["provisional"] = user.Provisional,
        ["createdAt"] = user.CreatedAt.ToString("O"),
        ["lastLoginAt"] = user.LastLoginAt?.ToString("O"),
    };

    private JsonObject UsersList() =>
        new() { ["users"] = new JsonArray(UsersOrError().List().Select(u => (JsonNode)UserJson(u)).ToArray()) };

    /// <summary><c>users.save</c>: <c>id</c> (cap = nou), <c>name</c>, <c>fullName</c>, <c>role</c>, <c>active</c> i <c>password</c> (la nova, o res).</summary>
    private JsonObject UsersSave(JsonObject msg)
    {
        var sessions = SessionsOrError();
        long userId = msg["userId"] is JsonValue idValue && idValue.TryGetValue(out long value) ? value : 0;
        var existing = userId == 0 ? null : UsersOrError().Get(userId) ?? throw new BadParamsException($"No hi ha cap usuari amb l'id {userId}.");
        var user = (existing ?? new User()) with
        {
            Name = ReadString(msg["name"]) ?? existing?.Name ?? "",
            FullName = ReadString(msg["fullName"]) ?? existing?.FullName ?? "",
            Role = msg["role"] is null ? existing?.Role ?? UserRole.Operator : ParseRoleCode(ReadString(msg["role"])),
            Active = msg["active"] is JsonValue a && a.TryGetValue(out bool active) ? active : existing?.Active ?? true,
        };
        string? password = ReadString(msg["password"]) is { Length: > 0 } p ? p : null;
        try
        {
            return new JsonObject { ["user"] = UserJson(sessions.SaveUser(user, password)) };
        }
        catch (SessionException ex)
        {
            throw new BadParamsException(ex.Message);
        }
    }

    private JsonObject SecurityGet() => new()
    {
        ["inactivityMinutes"] = (int)SessionsOrError().Inactivity.TotalMinutes,
        ["standardInactivityMinutes"] = (int)SessionManager.StandardInactivity.TotalMinutes,
    };

    /// <summary><c>security.set</c>: <c>inactivityMinutes</c> (de 5 a 240).</summary>
    private JsonObject SecuritySet(JsonObject msg)
    {
        var library = _library ?? throw new BadParamsException("El motor no té biblioteca.");
        if (msg["inactivityMinutes"] is not JsonValue v || !v.TryGetValue(out int minutes) || minutes is < MinInactivityMinutes or > MaxInactivityMinutes)
            throw new BadParamsException($"El camp 'inactivityMinutes' ha de ser un nombre entre {MinInactivityMinutes} i {MaxInactivityMinutes}.");
        library.SetSetting(SecuritySettingKey, new JsonObject { ["minuts"] = minutes }.ToJsonString());
        SessionsOrError().Inactivity = TimeSpan.FromMinutes(minutes);
        return SecurityGet();
    }

    /// <summary><c>changes.list</c>: <c>from</c>, <c>to</c> (instants), <c>userId</c>, <c>commands</c> (prefixos), <c>offset</c>, <c>limit</c> (fins a 500).</summary>
    private JsonObject ChangesList(JsonObject msg)
    {
        static DateTimeOffset? Instant(JsonNode? node, string name) => node is null ? null
            : DateTimeOffset.TryParse(ReadString(node), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at) ? at
            : throw new BadParamsException($"El camp '{name}' ha de ser una data i hora ISO 8601.");
        var filter = new ChangeFilter
        {
            From = Instant(msg["from"], "from"),
            To = Instant(msg["to"], "to"),
            UserId = msg["userId"] is JsonValue u && u.TryGetValue(out long userId) ? userId : null,
            CommandPrefixes = msg["commands"] is JsonArray commands ? commands.Select(ReadString).OfType<string>().ToList() : [],
            Offset = Math.Max(0, ReadInt(msg["offset"]) ?? 0),
            Limit = Math.Clamp(ReadInt(msg["limit"]) ?? 100, 1, 500),
        };
        var (entries, total) = UsersOrError().ListChanges(filter);
        return new JsonObject
        {
            ["total"] = total,
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id,
                ["at"] = e.At.ToString("O"),
                ["userId"] = e.UserId,
                ["user"] = e.UserName,
                ["command"] = e.Command,
                ["summary"] = e.Summary,
                ["detail"] = e.Detail,
            }).ToArray()),
        };
    }
}
