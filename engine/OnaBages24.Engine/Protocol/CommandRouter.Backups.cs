using System.Text.Json.Nodes;
using OnaBages24.Data;

namespace OnaBages24.Engine.Protocol;

// Còpies de seguretat (fase 4, pas 7b; D-032, D-152): la llista, fer-ne una ara, veure què hi ha en una i restaurar-la
// sencera. La restauració conserva l'historial d'ara posterior a la còpia i les claus que depenen de l'ordinador.
public sealed partial class CommandRouter
{
    /// <summary>
    /// Després de restaurar una còpia: el motor torna a llegir el que té en memòria (esdeveniments, AutoDJ, processador…) i
    /// avisa les interfícies. Ho fa EngineServer.
    /// </summary>
    public event Action? DatabaseRestored;

    /// <summary>
    /// Les claus de configuració que es queden amb el valor d'ara en restaurar (D-152): la sortida d'àudio (la còpia pot
    /// ser d'un altre ordinador) i l'aturada dels esdeveniments en curs.
    /// </summary>
    public static readonly string[] MachineSettingKeys = [OutputSettingKey, "esdeveniments_aturats"];

    private Backups BackupsOrError() => _backups ?? throw new BadParamsException("El motor no té còpies de seguretat.");

    private static JsonObject BackupJson(BackupFile b, string kind)
    {
        long? size = null;
        try { size = new FileInfo(b.Path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new JsonObject
        {
            ["path"] = b.Path,
            ["name"] = Path.GetFileName(b.Path),
            ["created"] = new DateTimeOffset(b.Created).ToString("O"),
            ["size"] = size,
            ["kind"] = kind,
        };
    }

    /// <summary>
    /// Les còpies, de la més nova a la més antiga: les de cada dia i les fetes a mà (<c>auto</c>) i les de l'estat d'abans
    /// de cada restauració (<c>safety</c>) i d'abans del manteniment de la programació (<c>maintenance</c>, D-194). <c>folder</c>: on són.
    /// </summary>
    private JsonObject BackupsList()
    {
        var backups = BackupsOrError();
        var all = backups.List().Select(b => (b, Kind: "auto"))
            .Concat(backups.ListSafety().Select(b => (b, Kind: "safety")))
            .Concat(backups.ListMaintenance().Select(b => (b, Kind: "maintenance")))
            .OrderByDescending(x => x.b.Created);
        return new JsonObject
        {
            ["folder"] = backups.Folder,
            ["backups"] = new JsonArray(all.Select(x => (JsonNode)BackupJson(x.b, x.Kind)).ToArray()),
        };
    }

    /// <summary>Fa una còpia ara (amb la rotació de sempre).</summary>
    private JsonObject BackupsCreate()
    {
        string path;
        try { path = BackupsOrError().Create(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new BadParamsException($"No s'ha pogut fer la còpia: {ex.Message}");
        }
        return new JsonObject { ["path"] = path };
    }

    /// <summary>Què hi ha en una còpia (<c>path</c>), sense tocar-la: la versió, si es pot restaurar i quantes coses té.</summary>
    private JsonObject BackupsInspect(JsonObject msg)
    {
        string path = BackupPath(msg);
        BackupInfo info;
        try { info = Backups.Inspect(path); }
        catch (Exception ex) when (ex is DatabaseException or IOException or UnauthorizedAccessException)
        {
            throw new BadParamsException(ex.Message);
        }
        return new JsonObject
        {
            ["path"] = path,
            ["version"] = info.Version,
            ["currentVersion"] = Database.LatestVersion,
            ["problem"] = info.Problem,
            ["tracks"] = info.Tracks,
            ["events"] = info.Events,
            ["rotations"] = info.Rotations,
            ["campaigns"] = info.Campaigns,
            ["lastPlayed"] = info.LastPlayed?.ToString("O"),
        };
    }

    /// <summary>
    /// Restaura una còpia sencera (<c>path</c>). Abans comprova l'esquema i fa una còpia de l'estat actual. Amb
    /// <c>keepHistory</c> (per defecte, sí), hi torna a posar l'historial i el registre d'ara posteriors a la còpia. La
    /// sortida d'àudio i l'aturada dels esdeveniments es queden com ara. L'emissió no s'atura.
    /// </summary>
    private JsonObject BackupsRestore(JsonObject msg)
    {
        var backups = BackupsOrError();
        string path = BackupPath(msg);
        bool keepHistory = msg["keepHistory"] is not JsonValue kv || !kv.TryGetValue(out bool keep) || keep;
        RestoreResult result;
        try
        {
            result = backups.Restore(path, new RestoreOptions { KeepHistory = keepHistory, KeepSettings = MachineSettingKeys });
        }
        catch (Exception ex) when (ex is DatabaseException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new BadParamsException(ex.Message);
        }
        DatabaseRestored?.Invoke();
        return new JsonObject
        {
            ["safety"] = result.SafetyPath,
            ["keptHistory"] = result.KeptHistory,
            ["keptExecutions"] = result.KeptExecutions,
        };
    }

    /// <summary>La ruta completa d'una còpia (<c>path</c>) que existeixi.</summary>
    private static string BackupPath(JsonObject msg)
    {
        string path = (ReadString(msg["path"]) ?? "").Trim();
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) throw new BadParamsException("Falta 'path' (la ruta completa de la còpia).");
        if (!File.Exists(path)) throw new BadParamsException($"No es troba la còpia: {path}");
        return Path.GetFullPath(path);
    }
}
