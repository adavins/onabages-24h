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
                "desfes-importacio" => UndoImport(args[1..]),
                "compara-punts" => CompareCues(args[1..]),
                "comprova-emissio" => CheckBroadcast(args[1..]),
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

              desfes-importacio CARPETA [--inclou-sonades] [--aplica] [--bd RUTA]
                  Esborra de la biblioteca les pistes d'una carpeta (i subcarpetes) que s'hi han afegit amb una importació
                  i encara no han servit de res: no venen de RadioDJ, no han sonat mai i no les fa servir cap esdeveniment.
                  --inclou-sonades: també les que ja han sonat (l'historial en conserva l'artista i el títol).
                  Sense --aplica només diu quines esborraria. Els fitxers no es toquen mai.

              compara-punts CARPETA [--bd RUTA]
                  Compara els punts de RadioDJ de les pistes d'una carpeta amb els que calcula l'analitzador.
                  No modifica res.

              comprova-emissio [--des DATA] [--fins DATA] [--forat SEGONS] [--bd RUTA]
                  Comprova una prova llarga d'emissió (fase 1, pas 11): busca forats entre pistes a l'historial i
                  resumeix les arrencades del motor i els avisos del registre. No modifica res; es pot fer amb el
                  motor en marxa.
                  --des, --fins   «2026-11-11» o «2026-11-11 22:00» (hora local). Per defecte, les últimes 72 hores
                  --forat         forats a partir de quants segons (per defecte, 1)
            """);
        return 2;
    }

    /// <summary>Ordre <c>comprova-emissio</c>: forats a l'historial, arrencades del motor i avisos del registre.</summary>
    private static int CheckBroadcast(string[] args)
    {
        string databasePath = DefaultDatabasePath;
        DateTime to = DateTime.Now;
        DateTime from = to.AddHours(-72);
        double gapSeconds = 1;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bd": databasePath = Value(args, ++i, "--bd"); break;
                case "--des": from = LocalDate(Value(args, ++i, "--des")); break;
                case "--fins": to = LocalDate(Value(args, ++i, "--fins")); break;
                case "--forat":
                    if (!double.TryParse(Value(args, ++i, "--forat").Replace(',', '.'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out gapSeconds) || gapSeconds <= 0)
                        throw new ArgumentException("--forat ha de ser un nombre de segons més gran que 0.");
                    break;
                default: throw new ArgumentException($"Opció desconeguda: {args[i]} (ajuda: OnaBages24.Engine ajuda)");
            }
        }
        if (to <= from) throw new ArgumentException("--fins ha de ser després de --des.");
        if (!File.Exists(databasePath)) throw new ArgumentException($"No hi ha cap base de dades a {databasePath}.");

        using var database = Database.Open(databasePath);
        var library = new Library(database);
        var entries = library.History(new DateTimeOffset(from), new DateTimeOffset(to));
        var report = BroadcastCheck.Analyze(entries, TimeSpan.FromSeconds(gapSeconds));
        string logs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "logs");
        var (starts, problems) = BroadcastCheck.ReadLogs(logs, from, to);

        static string When(DateTimeOffset t) => t.ToLocalTime().ToString("dd/MM HH:mm:ss");
        static string Hours(TimeSpan t) => $"{(int)t.TotalHours} h {t.Minutes:00} min";
        static string Track(HistoryEntry e) => string.IsNullOrWhiteSpace(e.Artist) ? $"«{e.Title}»" : $"«{e.Artist} - {e.Title}»";

        Console.WriteLine($"Comprovació de l'emissió del {from:dd/MM/yyyy HH:mm} al {to:dd/MM/yyyy HH:mm}");
        Console.WriteLine($"Base de dades: {Path.GetFullPath(databasePath)}");
        Console.WriteLine();
        if (report.Tracks == 0)
        {
            Console.WriteLine("No ha sonat cap pista en aquest període.");
            return 1;
        }
        var span = (report.Open is not null ? DateTimeOffset.Now : report.Last!.Value) - report.First!.Value;
        Console.WriteLine($"Pistes: {report.Tracks}, de {When(report.First!.Value)} a {When(report.Last!.Value)}" +
                          (report.Open is not null ? $" (encara sona {Track(report.Open)})" : ""));
        Console.WriteLine($"Temps amb música de la cua: {Hours(report.Covered)} de {Hours(span)}");
        Console.WriteLine();

        Console.WriteLine($"Forats de més de {gapSeconds:0.#} s entre pistes: {report.Gaps.Count}");
        foreach (var gap in report.Gaps.Take(50))
        {
            // Si entremig el motor ha tornat a arrencar, el forat és d'una aturada del motor, no d'un silenci amb el motor en marxa
            bool restarted = starts.Any(s => s >= gap.From.LocalDateTime.AddSeconds(-1) && s <= gap.After.Started.LocalDateTime.AddSeconds(1));
            Console.WriteLine($"  {When(gap.From)}  {gap.Length.TotalSeconds,8:0.0} s  entre {(gap.Before is null ? "?" : Track(gap.Before))} i {Track(gap.After)}" +
                              (restarted ? "  [el motor estava aturat]" : ""));
        }
        if (report.Gaps.Count > 50) Console.WriteLine($"  … i {report.Gaps.Count - 50} més");
        if (report.Unknown.Count > 0)
        {
            Console.WriteLine($"Pistes que sonaven quan el motor es va aturar de cop (no se sap quant van sonar): {report.Unknown.Count}");
            foreach (var e in report.Unknown.Take(20)) Console.WriteLine($"  {When(e.Started)}  {Track(e)}");
        }
        Console.WriteLine();

        Console.WriteLine($"Arrencades del motor: {starts.Count}" + (starts.Count > 0 ? " (" + string.Join(", ", starts.Take(10).Select(s => s.ToString("dd/MM HH:mm:ss"))) + (starts.Count > 10 ? ", …" : "") + ")" : ""));
        Console.WriteLine($"Avisos i errors del registre ({logs}): {problems.Sum(p => p.Count)}");
        foreach (var p in problems.Take(25))
            Console.WriteLine($"  {p.Count,5} × {p.Level} {p.Message}  (el primer, {p.FirstAt:dd/MM HH:mm:ss})");
        Console.WriteLine();

        bool ok = report.Gaps.Count == 0 && report.Unknown.Count == 0 && starts.Count <= 1;
        Console.WriteLine(ok
            ? "CORRECTE: cap forat entre pistes i el motor no s'ha reiniciat."
            : "ATENCIÓ: hi ha forats, aturades de cop o reinicis del motor; mira el detall de sobre.");
        return ok ? 0 : 1;
    }

    /// <summary>«2026-11-11» o «2026-11-11 22:00» (hora local).</summary>
    private static DateTime LocalDate(string text)
    {
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];
        if (DateTime.TryParseExact(text.Trim(), formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var date))
            return date;
        throw new ArgumentException($"Data no vàlida: «{text}». Exemples: 2026-11-11 o \"2026-11-11 22:00\".");
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

    /// <summary>
    /// Desfà una importació (fase 3, pas 7b): esborra les pistes d'una carpeta que no venen de RadioDJ, no han sonat mai i
    /// no fa servir cap esdeveniment. Per defecte només ho diu; amb <c>--aplica</c>, les esborra.
    /// </summary>
    private static int UndoImport(string[] args)
    {
        string? folder = null;
        bool apply = false, includePlayed = false;
        string databasePath = DefaultDatabasePath;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bd": databasePath = Value(args, ++i, "--bd"); break;
                case "--aplica": apply = true; break;
                case "--inclou-sonades": includePlayed = true; break;
                default: folder = args[i]; break;
            }
        }
        if (folder is null) return Help();

        Console.WriteLine($"Base de dades: {databasePath}");
        using var database = Database.Open(databasePath);
        var library = new Library(database);
        var inFolder = library.TracksInFolder(folder);
        var candidates = inFolder
            .Where(t => t.RadioDjId is null && (includePlayed || t.PlayCount == 0) && library.EventsUsingTrack(t.Id).Count == 0)
            .ToList();

        Console.WriteLine($"Pistes de la carpeta: {inFolder.Count}. Es poden esborrar (no venen de RadioDJ, " +
                          $"{(includePlayed ? "" : "no han sonat mai, ")}no les fa servir cap esdeveniment): {candidates.Count}. " +
                          $"Es queden: {inFolder.Count - candidates.Count}.");
        if (includePlayed && candidates.Any(t => t.PlayCount > 0))
            Console.WriteLine($"  {candidates.Count(t => t.PlayCount > 0)} han sonat: l'historial en conservarà l'artista i el títol, sense l'enllaç.");
        foreach (var t in candidates.Take(10)) Console.WriteLine($"  {t.Id}: {t.Path}");
        if (candidates.Count > 10) Console.WriteLine($"  … i {candidates.Count - 10} més");

        if (!apply)
        {
            Console.WriteLine();
            Console.WriteLine("No s'ha esborrat res. Per esborrar-les, torna a executar l'ordre amb --aplica.");
            return 0;
        }
        foreach (var t in candidates) library.DeleteTrack(t.Id);
        Console.WriteLine($"Esborrades: {candidates.Count} (els fitxers no s'han tocat).");
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
