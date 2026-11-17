namespace OnaBages24.Data;

/// <summary>Els rols (D-035), del que pot fer menys al que pot fer més: cada rol pot fer el que poden fer els de sota.</summary>
public enum UserRole
{
    /// <summary>La pantalla d'emissió: la cua, el reproductor, els auxiliars, les notes, l'AutoDJ, la sortida d'àudio.</summary>
    Operator = 1,
    /// <summary>A més: esdeveniments, campanyes, biblioteca, rotacions, bosses, calendaris.</summary>
    Programmer = 2,
    /// <summary>Tot: la configuració, els usuaris, les còpies de seguretat.</summary>
    Administrator = 3,
}

/// <summary>Un usuari (taula <c>usuari</c>, migració 011). L'empremta de la contrasenya no surt mai d'aquí cap a la interfície.</summary>
public sealed record User
{
    public long Id { get; init; }
    /// <summary>El nom per iniciar sessió (sense distingir majúscules).</summary>
    public string Name { get; init; } = "";
    public string FullName { get; init; } = "";
    public UserRole Role { get; init; }
    /// <summary>L'empremta PBKDF2 de la contrasenya, la sal i les iteracions (Base64).</summary>
    public string Hash { get; init; } = "";
    public string Salt { get; init; } = "";
    public int Iterations { get; init; }
    public bool Active { get; init; } = true;
    /// <summary>La contrasenya és provisional: s'ha de canviar en iniciar sessió.</summary>
    public bool Provisional { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; init; }

    /// <summary>El nom que es mostra: el complet o, si no n'hi ha, el d'iniciar sessió.</summary>
    public string DisplayName => FullName.Length > 0 ? FullName : Name;
}

/// <summary>Una entrada del registre de canvis (taula <c>registre_canvis</c>).</summary>
public sealed record ChangeEntry
{
    public long Id { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>Qui ho ha fet; null = sense usuari (encara no n'hi havia cap).</summary>
    public long? UserId { get; init; }
    public string UserName { get; init; } = "";
    /// <summary>L'ordre del protocol (p. ex. <c>scheduler.save</c>).</summary>
    public string Command { get; init; } = "";
    public string Summary { get; init; } = "";
    /// <summary>Els paràmetres, en JSON (sense contrasenyes ni imatges).</summary>
    public string? Detail { get; init; }
}

/// <summary>Filtres del registre de canvis. Els que són null no filtren.</summary>
public sealed record ChangeFilter
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public long? UserId { get; init; }
    /// <summary>Les ordres que comencen així (p. ex. <c>scheduler.</c>); buit = totes.</summary>
    public IReadOnlyList<string> CommandPrefixes { get; init; } = [];
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
}

/// <summary>
/// Usuaris i registre de canvis (fase 5, pas 8; D-035, D-164): desar-los i llegir-los. Qui pot fer què i les sessions
/// ho decideix el motor.
/// </summary>
public sealed class UserStore(Database database)
{
    private const string Columns = "id, nom, nom_complet, rol, empremta, sal, iteracions, actiu, provisional, creat, darrer_acces";

    public static string RoleText(UserRole role) => role switch
    {
        UserRole.Administrator => "ADMINISTRADOR",
        UserRole.Programmer => "PROGRAMADOR",
        _ => "OPERADOR",
    };

    public static UserRole ParseRole(string text) => text switch
    {
        "ADMINISTRADOR" => UserRole.Administrator,
        "PROGRAMADOR" => UserRole.Programmer,
        _ => UserRole.Operator,
    };

    /// <summary>Si hi ha algun usuari actiu (si no, no cal iniciar sessió: tot es pot fer com abans).</summary>
    public bool AnyActive()
    {
        lock (database.Sync)
        {
            using var command = database.Command("SELECT EXISTS (SELECT 1 FROM usuari WHERE actiu = 1);");
            return Convert.ToInt64(command.ExecuteScalar()) == 1;
        }
    }

    /// <summary>Tots, per nom.</summary>
    public IReadOnlyList<User> List()
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {Columns} FROM usuari ORDER BY nom COLLATE NOCASE;");
            return ReadUsers(command);
        }
    }

    public User? Get(long id)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {Columns} FROM usuari WHERE id = $id;");
            command.Parameters.AddWithValue("$id", id);
            return ReadUsers(command).FirstOrDefault();
        }
    }

    /// <summary>Per nom (sense distingir majúscules).</summary>
    public User? GetByName(string name)
    {
        lock (database.Sync)
        {
            using var command = database.Command($"SELECT {Columns} FROM usuari WHERE nom = $nom;");
            command.Parameters.AddWithValue("$nom", name);
            return ReadUsers(command).FirstOrDefault();
        }
    }

    /// <summary>Un usuari nou (<see cref="User.Id"/> = 0) o canviat. Retorna l'id. Un nom repetit és un <see cref="DatabaseException"/>.</summary>
    public long Save(User user)
    {
        lock (database.Sync)
        {
            using var command = database.Command(user.Id == 0
                ? "INSERT INTO usuari (nom, nom_complet, rol, empremta, sal, iteracions, actiu, provisional, creat, darrer_acces) " +
                  "VALUES ($nom, $complet, $rol, $empremta, $sal, $iteracions, $actiu, $provisional, $creat, $acces) RETURNING id;"
                : "UPDATE usuari SET nom = $nom, nom_complet = $complet, rol = $rol, empremta = $empremta, sal = $sal, " +
                  "iteracions = $iteracions, actiu = $actiu, provisional = $provisional, darrer_acces = $acces WHERE id = $id RETURNING id;");
            command.Parameters.AddWithValue("$id", user.Id);
            command.Parameters.AddWithValue("$nom", user.Name);
            command.Parameters.AddWithValue("$complet", user.FullName);
            command.Parameters.AddWithValue("$rol", RoleText(user.Role));
            command.Parameters.AddWithValue("$empremta", user.Hash);
            command.Parameters.AddWithValue("$sal", user.Salt);
            command.Parameters.AddWithValue("$iteracions", user.Iterations);
            command.Parameters.AddWithValue("$actiu", user.Active ? 1 : 0);
            command.Parameters.AddWithValue("$provisional", user.Provisional ? 1 : 0);
            command.Parameters.AddWithValue("$creat", DbTime.ToText(user.CreatedAt));
            command.Parameters.AddWithValue("$acces", user.LastLoginAt is { } at ? DbTime.ToText(at) : DBNull.Value);
            try
            {
                return Convert.ToInt64(command.ExecuteScalar()
                    ?? throw new DatabaseException($"No hi ha cap usuari amb l'id {user.Id}."));
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE
            {
                throw new DatabaseException($"Ja hi ha un usuari que es diu «{user.Name}».");
            }
        }
    }

    private static List<User> ReadUsers(Microsoft.Data.Sqlite.SqliteCommand command)
    {
        using var r = command.ExecuteReader();
        var list = new List<User>();
        while (r.Read())
        {
            list.Add(new User
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                FullName = r.GetString(2),
                Role = ParseRole(r.GetString(3)),
                Hash = r.GetString(4),
                Salt = r.GetString(5),
                Iterations = r.GetInt32(6),
                Active = r.GetInt64(7) != 0,
                Provisional = r.GetInt64(8) != 0,
                CreatedAt = DbTime.ParseInstant(r.GetString(9)),
                LastLoginAt = r.IsDBNull(10) ? null : DbTime.ParseInstant(r.GetString(10)),
            });
        }
        return list;
    }

    // --- Registre de canvis ---

    /// <summary>Desa una entrada. Retorna l'id.</summary>
    public long AddChange(ChangeEntry entry)
    {
        lock (database.Sync)
        {
            using var command = database.Command(
                "INSERT INTO registre_canvis (moment, usuari_id, usuari_nom, ordre, resum, detall) " +
                "VALUES ($moment, $usuari, $nom, $ordre, $resum, $detall) RETURNING id;");
            command.Parameters.AddWithValue("$moment", DbTime.ToText(entry.At));
            command.Parameters.AddWithValue("$usuari", (object?)entry.UserId ?? DBNull.Value);
            command.Parameters.AddWithValue("$nom", entry.UserName);
            command.Parameters.AddWithValue("$ordre", entry.Command);
            command.Parameters.AddWithValue("$resum", entry.Summary);
            command.Parameters.AddWithValue("$detall", (object?)entry.Detail ?? DBNull.Value);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    /// <summary>Les entrades, de la més nova a la més antiga, i quantes n'hi ha en total amb aquests filtres.</summary>
    public (IReadOnlyList<ChangeEntry> Entries, int Total) ListChanges(ChangeFilter filter)
    {
        var where = new List<string>();
        if (filter.From is not null) where.Add("moment >= $from");
        if (filter.To is not null) where.Add("moment < $to");
        if (filter.UserId is not null) where.Add("usuari_id = $usuari");
        if (filter.CommandPrefixes.Count > 0)
            where.Add("(" + string.Join(" OR ", filter.CommandPrefixes.Select((_, i) => $"ordre LIKE $p{i} ESCAPE '\\'")) + ")");
        string condition = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        void Bind(Microsoft.Data.Sqlite.SqliteCommand command)
        {
            if (filter.From is { } from) command.Parameters.AddWithValue("$from", DbTime.ToText(from));
            if (filter.To is { } to) command.Parameters.AddWithValue("$to", DbTime.ToText(to));
            if (filter.UserId is { } user) command.Parameters.AddWithValue("$usuari", user);
            for (int i = 0; i < filter.CommandPrefixes.Count; i++)
                command.Parameters.AddWithValue($"$p{i}", filter.CommandPrefixes[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        }

        lock (database.Sync)
        {
            int total;
            using (var count = database.Command($"SELECT count(*) FROM registre_canvis {condition};"))
            {
                Bind(count);
                total = Convert.ToInt32(count.ExecuteScalar());
            }
            using var command = database.Command(
                $"SELECT id, moment, usuari_id, usuari_nom, ordre, resum, detall FROM registre_canvis {condition} " +
                "ORDER BY moment DESC, id DESC LIMIT $limit OFFSET $offset;");
            Bind(command);
            command.Parameters.AddWithValue("$limit", Math.Max(1, filter.Limit));
            command.Parameters.AddWithValue("$offset", Math.Max(0, filter.Offset));
            using var r = command.ExecuteReader();
            var list = new List<ChangeEntry>();
            while (r.Read())
            {
                list.Add(new ChangeEntry
                {
                    Id = r.GetInt64(0),
                    At = DbTime.ParseInstant(r.GetString(1)),
                    UserId = r.IsDBNull(2) ? null : r.GetInt64(2),
                    UserName = r.GetString(3),
                    Command = r.GetString(4),
                    Summary = r.GetString(5),
                    Detail = r.IsDBNull(6) ? null : r.GetString(6),
                });
            }
            return (list, total);
        }
    }

    /// <summary>Esborra les entrades d'abans d'aquest instant. Retorna quantes.</summary>
    public int PurgeChanges(DateTimeOffset before)
    {
        lock (database.Sync)
        {
            using var command = database.Command("DELETE FROM registre_canvis WHERE moment < $before;");
            command.Parameters.AddWithValue("$before", DbTime.ToText(before));
            return command.ExecuteNonQuery();
        }
    }
}
