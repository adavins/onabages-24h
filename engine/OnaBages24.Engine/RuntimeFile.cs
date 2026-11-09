using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Engine.Infrastructure;

namespace OnaBages24.Engine;

/// <summary>
/// Fitxer d'execució <c>&lt;dades&gt;\engine.json</c>: diu a la interfície on és el motor (port i testimoni).
/// Permet que una interfície reiniciada es torni a connectar a un motor que continua funcionant.
///
/// <see cref="DirectoryPath"/> és també la carpeta de totes les dades del motor (base de dades,
/// còpies de seguretat, registres). L'aplicació és portable (D-044): les dades van a la carpeta
/// <c>dades\</c> de l'aplicació (vegeu <see cref="FindPortableDataFolder"/>). Es pot canviar amb <c>--data-dir</c>.
/// </summary>
public static class RuntimeFile
{
    public const int SchemaVersion = 1;

    /// <summary>Nom de la carpeta de dades de la versió portable.</summary>
    public const string PortableFolderName = "dades";

    /// <summary>Fitxer que marca la carpeta de dades (perquè no es confongui amb una altra carpeta «dades» del disc).</summary>
    public const string PortableMarkerFile = "LLEGEIX-ME.txt";

    /// <summary>Carpeta de reserva, si no es troba la carpeta portable: comuna a tots els usuaris de l'ordinador.</summary>
    public static string FallbackDirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OnaBages24");

    /// <summary>Carpeta de dades per defecte: la portable si es troba; si no, la de reserva.</summary>
    public static string DefaultDirectoryPath { get; } = FindPortableDataFolder(AppContext.BaseDirectory) ?? FallbackDirectoryPath;

    /// <summary>Si la carpeta de dades és la de reserva (per avisar-ne al registre).</summary>
    public static bool UsingFallback => string.Equals(DirectoryPath, FallbackDirectoryPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Busca la carpeta <c>dades\</c> (amb el fitxer <see cref="PortableMarkerFile"/>) a la carpeta del motor i
    /// a les de sobre, com a molt 6 nivells. Així la troba tant a la versió portable
    /// (<c>OnaBages24\resources\engine\</c> → <c>OnaBages24\dades\</c>) com en desenvolupament
    /// (<c>engine\OnaBages24.Engine\bin\Debug\net10.0-windows\</c> → <c>dades\</c> del repositori).
    /// </summary>
    public static string? FindPortableDataFolder(string startFolder)
    {
        var dir = new DirectoryInfo(startFolder);
        for (int level = 0; dir is not null && level <= 6; level++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, PortableFolderName);
            if (File.Exists(Path.Combine(candidate, PortableMarkerFile)))
                return candidate;
        }
        return null;
    }

    /// <summary>Carpeta de dades. Només es canvia en arrencar (<see cref="UseDataDirectory"/>).</summary>
    public static string DirectoryPath { get; private set; } = DefaultDirectoryPath;

    public static string FilePath => Path.Combine(DirectoryPath, "engine.json");

    /// <summary>Fa servir una altra carpeta de dades. S'ha de cridar abans de llegir o escriure res.</summary>
    public static void UseDataDirectory(string path) => DirectoryPath = Path.GetFullPath(path);

    public static void Write(EngineOptions options)
    {
        var data = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["pid"] = Environment.ProcessId,
            ["port"] = options.Port,
            ["token"] = options.Token,
            ["startedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["version"] = EngineInfo.Version,
        };

        AtomicFile.WriteAllText(FilePath, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Esborra el fitxer només si l'ha escrit aquest mateix procés.</summary>
    public static void DeleteIfOwned()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            var node = JsonNode.Parse(File.ReadAllText(FilePath));
            int? pid = node?["pid"]?.GetValue<int>();
            if (pid == Environment.ProcessId)
                File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Si no es pot llegir o esborrar, no és greu: la interfície
            // comprova igualment si el motor respon abans de fer-lo servir.
        }
    }
}
