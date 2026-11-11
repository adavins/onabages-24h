using System.Text;
using OnaBages24.Data;
using OnaBages24.Data.RadioDj;

namespace OnaBages24.Engine;

/// <summary>
/// Punt d'entrada del motor.
/// - Sense ordre (o només amb opcions): arrenca el servidor (<see cref="EngineServer"/>).
///   <c>OnaBages24.Engine [--port 47824] [--token TESTIMONI] [--data-dir CARPETA]</c>
/// - Amb una ordre (<c>importa-radiodj</c>, <c>importa-carpeta</c>, <c>compara-punts</c>, <c>ajuda</c>): la fa i acaba.
/// </summary>
public static class Program
{
    /// <summary>On es desa la base de dades si no es diu el contrari.</summary>
    public static string DefaultDatabasePath => Path.Combine(RuntimeFile.DefaultDirectoryPath, "onabages24.db");

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return args.FirstOrDefault() switch
            {
                "importa-radiodj" => ImportRadioDj(args[1..]),
                "importa-carpeta" => ImportFolder(args[1..]),
                "compara-punts" => CompareCues(args[1..]),
                "ajuda" or "--help" or "-h" or "/?" => Help(),
                _ => EngineServer.Run(args),
            };
        }
        catch (Exception ex) when (ex is DatabaseException or IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            Ona Bages 24h — motor (fase 1)

            Sense cap ordre, arrenca el motor:
              OnaBages24.Engine [--port 47824] [--token TESTIMONI] [--data-dir CARPETA]

            Ordres:
              importa-radiodj FITXER.sql [FITXER.sql…] [opcions]
                  Importa la biblioteca de RadioDJ (categories, subcategories, gèneres, pistes i rotacions) i els
                  esdeveniments del Programador des d'un backup.
                  Es pot tornar a executar: les pistes ja importades s'actualitzen, i les rotacions i els esdeveniments
                  importats abans se substitueixen (els esdeveniments creats a mà no es toquen).
                  --bd RUTA                     base de dades (per defecte, la de la carpeta «dades» de l'aplicació)
                  --arrel DE A                  canvi d'arrel de les rutes (es pot repetir; s'aplica el primer que coincideixi).
                                                Es recorda: les pròximes importacions l'apliquen soles si no se'n diu cap
                  --sense-arrel                 rutes de RadioDJ tal com són (oblida el canvi d'arrel recordat)
                  --sense-fitxers               no comprova si els fitxers existeixen
                  --sense-esdeveniments         no importa els esdeveniments
                  --esdeveniments-desactivats   els importa tots desactivats (en un ordinador sense els fitxers dels programes)

              importa-carpeta CARPETA --categoria NOM --subcategoria NOM [--bd RUTA]
                  Importa els fitxers d'àudio d'una carpeta (i subcarpetes) que encara no hi són: etiquetes,
                  durada i punts d'encadenament calculats amb l'analitzador.

              compara-punts CARPETA [--bd RUTA]
                  Compara els punts de RadioDJ de les pistes d'una carpeta amb els que calcula l'analitzador.
                  No modifica res.
            """);
        return 2;
    }

    private static int ImportRadioDj(string[] args)
    {
        var files = new List<string>();
        var rootChanges = new List<RootChange>();
        string databasePath = DefaultDatabasePath;
        bool checkFiles = true;
        bool importEvents = true;
        bool eventsDisabled = false;
        bool noRoot = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bd":
                    databasePath = Value(args, ++i, "--bd");
                    break;
                case "--arrel":
                    rootChanges.Add(new RootChange(Value(args, ++i, "--arrel"), Value(args, ++i, "--arrel")));
                    break;
                case "--sense-arrel":
                    noRoot = true;
                    break;
                case "--sense-fitxers":
                    checkFiles = false;
                    break;
                case "--sense-esdeveniments":
                    importEvents = false;
                    break;
                case "--esdeveniments-desactivats":
                    eventsDisabled = true;
                    break;
                default:
                    files.Add(args[i]);
                    break;
            }
        }
        if (files.Count == 0) return Help();
        if (noRoot && rootChanges.Count > 0)
        {
            Console.Error.WriteLine("Error: --sense-arrel i --arrel no es poden fer servir alhora.");
            return 2;
        }

        Console.WriteLine($"Base de dades: {databasePath}");
        using var database = Database.Open(databasePath);
        var library = new Library(database);

        // Canvis d'arrel (D-067): els que es diuen ara substitueixen els recordats; sense cap, es tornen a aplicar els
        // de l'última importació en aquesta base de dades (i es diu sempre quins); --sense-arrel els oblida
        if (rootChanges.Count > 0)
        {
            RadioDjImporter.RememberRootChanges(library, rootChanges);
            foreach (var change in rootChanges) Console.WriteLine($"Canvi d'arrel: {change.From} → {change.To} (es recordarà per a les pròximes importacions)");
        }
        else if (noRoot)
        {
            RadioDjImporter.RememberRootChanges(library, []);
            Console.WriteLine("Sense canvi d'arrel: les rutes de RadioDJ tal com són (s'oblida el que es recordava).");
        }
        else if (RadioDjImporter.RememberedRootChanges(library) is { Count: > 0 } remembered)
        {
            rootChanges.AddRange(remembered);
            foreach (var change in remembered)
                Console.WriteLine($"Canvi d'arrel recordat d'una importació anterior: {change.From} → {change.To} (per no aplicar-lo: --sense-arrel)");
        }

        if (library.CountTracks() > 0)
        {
            string copy = new Backups(database, Path.Combine(Path.GetDirectoryName(databasePath)!, "copies")).Create();
            Console.WriteLine($"Còpia de seguretat abans d'importar: {copy}");
        }

        Console.WriteLine($"Llegint {string.Join(", ", files)}…");
        var dump = MySqlDump.Load(files.ToArray());

        var started = DateTime.Now;
        var report = RadioDjImporter.ImportLibrary(dump, library, new RadioDjImportOptions
        {
            RootChanges = rootChanges,
            CheckFiles = checkFiles,
        }, new Rotations(database));

        Console.WriteLine();
        Console.WriteLine(report);

        // Esdeveniments del Programador (fase 2, pas 6): després de la biblioteca, perquè les pistes es busquen pel seu id de RadioDJ
        if (importEvents && dump.Rows("events").Count > 0)
        {
            var eventsReport = RadioDjEvents.Import(dump, library, new Rotations(database), new Events(database),
                new RadioDjEventsOptions { AllDisabled = eventsDisabled });
            Console.WriteLine();
            Console.WriteLine(eventsReport);
        }
        Console.WriteLine($"Temps: {(DateTime.Now - started).TotalSeconds:0.0} s");
        return 0;
    }

    private static int ImportFolder(string[] args)
    {
        string? folder = null, category = null, subcategory = null;
        string databasePath = DefaultDatabasePath;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bd": databasePath = Value(args, ++i, "--bd"); break;
                case "--categoria": category = Value(args, ++i, "--categoria"); break;
                case "--subcategoria": subcategory = Value(args, ++i, "--subcategoria"); break;
                default: folder = args[i]; break;
            }
        }
        if (folder is null || category is null || subcategory is null) return Help();

        Console.WriteLine($"Base de dades: {databasePath}");
        using var database = Database.Open(databasePath);
        var library = new Library(database);
        long subcategoryId = library.EnsureSubcategory(library.EnsureCategory(category), subcategory);

        var started = DateTime.Now;
        int done = 0;
        var report = LibraryTools.FolderImporter.Import(library, folder, subcategoryId, progress: _ =>
        {
            int n = Interlocked.Increment(ref done);
            if (n % 25 == 0) Console.WriteLine($"  {n} pistes analitzades…");
        });

        Console.WriteLine();
        Console.WriteLine(report);
        Console.WriteLine($"Temps: {(DateTime.Now - started).TotalSeconds:0.0} s");
        return 0;
    }

    private static int CompareCues(string[] args)
    {
        string? folder = null;
        string databasePath = DefaultDatabasePath;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bd") databasePath = Value(args, ++i, "--bd");
            else folder = args[i];
        }
        if (folder is null) return Help();

        using var database = Database.Open(databasePath);
        var tracks = new Library(database).TracksInFolder(folder);
        Console.WriteLine($"Analitzant {tracks.Count} pistes de {folder}…");

        var started = DateTime.Now;
        var results = LibraryTools.CueComparison.Compare(tracks);
        Console.WriteLine();
        Console.WriteLine(LibraryTools.CueComparison.Summary(results));
        Console.WriteLine($"Temps: {(DateTime.Now - started).TotalSeconds:0.0} s");
        return 0;
    }

    private static string Value(string[] args, int i, string option) =>
        i < args.Length ? args[i] : throw new ArgumentException($"Falta el valor de {option}.");
}
