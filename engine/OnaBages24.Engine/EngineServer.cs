using System.Net;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Infrastructure;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;
using OnaBages24.Engine.Supervision;

namespace OnaBages24.Engine;

/// <summary>
/// El motor en marxa (fase 1, pas 6): servidor WebSocket local amb testimoni, base de dades,
/// còpia de seguretat diària i esdeveniment <c>engine.status</c> cada segon.
/// L'àudio, la cua i l'AutoDJ s'hi afegeixen als passos 7 i 8.
/// </summary>
public static class EngineServer
{
    public static int Run(string[] args)
    {
        EngineOptions options;
        try
        {
            options = EngineOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (options.DataDir is { } dataDir)
            RuntimeFile.UseDataDirectory(dataDir);

        var builder = WebApplication.CreateSlimBuilder();

        // Només s'accepten connexions des del mateix ordinador.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        // En aturar-se, com a molt 5 s d'espera (per defecte en serien 30): el vigilant l'ha de poder reiniciar de seguida.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

        // Registre tècnic també en fitxer (un per dia a <dades>\logs\, es guarden 30 dies).
        using var logFile = new EngineLogFile(Path.Combine(RuntimeFile.DirectoryPath, "logs"));
        builder.Logging.AddProvider(new EngineLogFileProvider(logFile));

        var app = builder.Build();
        var log = app.Logger;
        log.LogInformation("{Name} {Version} arrencant (port {Port}, PID {Pid}, dades a {DataDir})",
            EngineInfo.Name, EngineInfo.Version, options.Port, Environment.ProcessId, RuntimeFile.DirectoryPath);
        if (RuntimeFile.UsingFallback)
            log.LogWarning("No s'ha trobat la carpeta «{Folder}» de l'aplicació (amb {Marker}): les dades van a la carpeta de reserva {DataDir}",
                RuntimeFile.PortableFolderName, RuntimeFile.PortableMarkerFile, RuntimeFile.DirectoryPath);

        // Un error no previst que fes caure el motor: com a mínim, que quedi al registre.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.LogCritical("Error no previst: el motor es tanca. {Error}", e.ExceptionObject);
            logFile.Flush(TimeSpan.FromSeconds(2));
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError("Error no previst en una tasca de fons (el motor continua): {Error}", e.Exception);
            e.SetObserved();
        };

        // Base de dades. Si no es pot obrir (p. ex. és d'una versió més nova), el motor no arrenca: ho diu el registre.
        string databasePath = Path.Combine(RuntimeFile.DirectoryPath, "onabages24.db");
        Database database;
        try
        {
            database = Database.Open(databasePath);
        }
        catch (DatabaseException ex)
        {
            log.LogCritical("No s'ha pogut obrir la base de dades {Path}: {Message}", databasePath, ex.Message);
            logFile.Flush(TimeSpan.FromSeconds(2));
            return 1;
        }
        using var databaseLifetime = database; // es tanca en sortir
        var library = new Library(database);
        log.LogInformation("Base de dades: {Path} (esquema {Version}, {Tracks} pistes)", databasePath, database.Version, library.CountTracks());

        var backups = new Backups(database, Path.Combine(RuntimeFile.DirectoryPath, "copies"));
        var state = new EngineState();
        var hub = new ClientHub(state.SetClients, log);

        void Broadcast(System.Text.Json.Nodes.JsonObject ev) => _ = Task.Run(async () =>
        {
            try { await hub.BroadcastAsync(ev, app.Lifetime.ApplicationStopping); }
            catch (OperationCanceledException) { }
        });

        // Incidències (fase 5, pas 1; D-156): tot el que va malament a l'emissió, desat i enviat a les interfícies
        var incidentStore = new IncidentStore(database);
        var incidents = new IncidentTracker(incidentStore, log);
        incidents.Changed += incident =>
        {
            var ev = Messages.Event("incidents.changed");
            ev["incident"] = CommandRouter.IncidentJson(incident);
            Broadcast(ev);
        };
        // Avisos per correu (fase 5, pas 5; D-160): abans del reinici inesperat, perquè també s'avisi
        var mail = new MailNotifier(SmtpMail.SendAsync, stationName: () => CommandRouter.ReadStationName(library),
            openNow: () => incidents.Open, log: log);
        CommandRouter.ConfigureMail(mail, library);
        incidents.Changed += mail.Add;

        // Usuaris i sessions (fase 5, pas 8; D-164): sense cap usuari actiu, no cal iniciar sessió
        var userStore = new UserStore(database);
        var sessions = new OnaBages24.Engine.Security.SessionManager(userStore) { Inactivity = CommandRouter.ReadInactivity(library) };
        log.LogInformation("Usuaris: {State}", sessions.UsersExist ? "cal iniciar sessió per operar" : "no n'hi ha cap (tot es pot fer sense sessió)");

        // Aturades (fase 5, passos 4 i 7; D-159, D-162): el motor no s'ha d'aturar mai, per tant qualsevol aturada anterior
        // és una incidència, amb el motiu que va deixar escrit al fitxer «en marxa» o, si no en va poder deixar cap, el
        // que diu el registre de Windows (reinici demanat, tall de llum…)
        var runMarker = new RunMarker(Path.Combine(RuntimeFile.DirectoryPath, RunMarker.FileName));
        if (runMarker.Start() is { } previous)
        {
            var now = DateTimeOffset.Now;
            var windowsEvents = previous.LastAlive is { } lastAlive
                ? WindowsRestarts.Read(lastAlive - TimeSpan.FromMinutes(1), now)
                : [];
            var (severity, text) = RunMarker.Describe(previous, now, WindowsRestarts.BootTime(), windowsEvents);
            log.LogWarning("{Text}", text);
            incidents.Report(IncidentKind.Restart, "reinici", severity, text);
        }
        // Per què s'atura aquesta vegada: l'ordre engine.shutdown o els senyals de la consola i de Windows. El senyal no
        // s'atura (Cancel = false): el motor es tanca igual que abans.
        using var onCtrlC = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT, _ => runMarker.Stop(StopReason.CtrlC));
        using var onCtrlBreak = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGQUIT, _ => runMarker.Stop(StopReason.CtrlBreak));
        using var onConsoleClosed = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGHUP, _ => runMarker.Stop(StopReason.ConsoleClosed));
        using var onWindowsShutdown = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, _ => runMarker.Stop(StopReason.WindowsShutdown));

        // Àudio (D-005, D-039): mesclador estèreo a 48 kHz, una sola sortida i la cua d'emissió
        var mixer = new Mixer(sampleRate: 48000);
        // Processador de sortida (fase 3, pas 6): el desat o «Suau», i el sostre de seguretat sempre. Es desa i s'avisa en canviar.
        var processor = new OutputProcessor(mixer.Processor);
        ConfigureProcessor(processor, library, log);
        log.LogInformation("Processador de sortida: {Preset}, volum {Volume} dB", processor.Preset.Name, processor.VolumeDb);
        processor.Changed += () =>
        {
            try
            {
                library.SetSetting(ProcessorSettingKey,
                    System.Text.Json.JsonSerializer.Serialize(new { preset = processor.Preset.Id, volumeDb = processor.VolumeDb }));
            }
            catch (DatabaseException ex) { log.LogWarning("No s'ha pogut desar el processador: {Message}", ex.Message); }
            var ev = Messages.Event("processor.changed");
            ev["processor"] = QueueJson.Processor(processor);
            Broadcast(ev);
        };
        var outputs = new OutputManager(mixer);
        outputs.Configure(ReadConfiguredOutput(library, log));
        var queue = new QueuePlayer(mixer);
        // Silenci a la sortida (fase 5, pas 3; D-158): el llindar i els segons desats
        var silence = new Supervision.SilenceDetector(mixer, queue);
        ConfigureSilence(silence, library);
        // Pistes d'emergència (fase 5, pas 4; D-159): la carpeta desada o dades\emergencia; la llista es llegeix ara i cada minut
        var emergency = new EmergencyTracks(CommandRouter.ReadEmergencyFolder(library));
        CommandRouter.EnsureDefaultEmergencyFolder(emergency.Folder);
        emergency.Refresh();
        queue.EmergencyPicker = emergency.Pick;
        // Rellotge de mostres (fase 2, pas 4): l'hora en què sona cada fotograma, amb la latència mesurada de la sortida (pas 4b)
        var sampleClock = new SampleClock(mixer, () => outputs.Latency);
        // Reproductors auxiliars (fase 2, pas 5): senyals horaris per sobre de la música, amb atenuació opcional
        var aux = new AuxPlayers(mixer);
        // Atenuació per defecte dels auxiliars (D-078): la desada a la configuració, o −10 dB
        aux.DefaultDuckingDb = CommandRouter.ReadDucking(library).Aux;

        // AutoDJ (fase 1, pas 8): arrenca sol amb la rotació desada (o la primera), i l'emissió comença de seguida
        var rotations = new Rotations(database);
        // Uns 30 minuts de música per davant a la cua (D-097): es veu què vindrà i la previsió és útil
        var autoDj = new AutoDj(library, rotations, queue, new AutoDjSettings { FillAhead = TimeSpan.FromMinutes(30) });
        // Trepitjador (fase 3, pas 5): el sweeper a sobre de l'inici de la pista, amb l'atenuació desada (D-078) o −8 dB
        var sweepers = new SweeperOverlay(queue, aux) { DuckingDb = CommandRouter.ReadDucking(library).Sweepers };
        var playback = new PlaybackEvents(queue, outputs, log, Broadcast, autoDj, sampleClock, aux);
        var history = new PlayHistory(queue, library, log, aux);
        // Les pistes que sonaven si el motor es va aturar de cop (fase 3, pas 2a): es tanquen com a tallades
        if (library.CloseOpenHistory() is var closed and > 0)
            log.LogInformation("Historial: {Count} pistes que sonaven quan el motor es va aturar, tancades com a tallades", closed);
        ConfigureAutoDj(autoDj, library, log);
        // Separació real (fase 3, pas 8b; D-095): la desada, o cançó 6 h i artista 1 h
        autoDj.Separation = CommandRouter.ReadSeparation(library);
        autoDj.Reserve = CommandRouter.ReadReserve(library);
        log.LogInformation("Separació: la mateixa cançó cada {Track} min com a mínim; el mateix artista, {Artist} min",
            autoDj.Separation.Track.TotalMinutes, autoDj.Separation.Artist.TotalMinutes);
        ConfigureSweepers(autoDj, library, log);
        // La categoria de sweepers es desa cada vegada que canvia (a mà o amb l'acció SWEEPERS): es manté si el motor es reinicia
        autoDj.SweepersChanged += subcategoryId =>
        {
            try { library.SetSetting(SweepersSettingKey, System.Text.Json.JsonSerializer.Serialize(new { subcategoryId })); }
            catch (DatabaseException ex) { log.LogWarning("No s'ha pogut desar la categoria de sweepers: {Message}", ex.Message); }
        };

        // Títol per a BUTT (fase 1, pas 9; D-009): per defecte, <dades>\NowPlaying.txt
        using var nowPlaying = new NowPlaying(queue, Path.Combine(RuntimeFile.DirectoryPath, "NowPlaying.txt"),
            ReadNowPlayingSettings(library, log), log);
        log.LogInformation("Títol per a BUTT: {State} {Path}", nowPlaying.Settings.Enabled ? "s'escriu a" : "desactivat;", nowPlaying.FilePath);
        // Títol fix (D-184): es desa cada vegada que canvia i, si el motor s'ha reiniciat a mig programa, es recupera
        try
        {
            if (library.GetSetting(FixedTitleSettingKey) is { } savedFixed && FixedTitle.FromJson(savedFixed) is { } fixedTitle)
                nowPlaying.Restore(fixedTitle);
        }
        catch (DatabaseException ex) { log.LogWarning("No s'ha pogut llegir el títol fix per a BUTT: {Message}", ex.Message); }
        nowPlaying.FixedChanged += title =>
        {
            try { library.SetSetting(FixedTitleSettingKey, title?.ToJson() ?? "null"); }
            catch (DatabaseException ex) { log.LogWarning("No s'ha pogut desar el títol fix per a BUTT: {Message}", ex.Message); }
        };

        // Planificador (fase 2, pas 3; D-008): al fil de control, amb el rellotge del motor. Si un esdeveniment canvia la
        // rotació, es desa com amb autodj.set, perquè es mantingui si el motor es reinicia (D-053).
        var events = new Events(database);
        // Campanyes (fase 4, pas 3b): el pla de passis es calcula ara i cada vegada que canvien les campanyes, els esdeveniments
        // o la biblioteca; l'acció CAMPANYA fa sonar la falca del pla
        var campaigns = new Campaigns(database);
        var campaignPlans = new Scheduling.CampaignPlans(campaigns, events, library);
        void RecalculatePlans()
        {
            try { campaignPlans.RecalculateAll(); }
            catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogWarning("No s'ha pogut calcular el pla de les campanyes: {Message}", ex.Message);
            }
        }
        RecalculatePlans();
        // TALLA a la mostra exacta (fase 2, pas 4): l'hora es converteix en el fotograma que sonarà, amb el rellotge de mostres
        var executor = new Scheduling.EventExecutor(library, queue, autoDj, rotationId =>
            library.SetSetting(CommandRouter.AutoDjSettingKey, System.Text.Json.JsonSerializer.Serialize(new { rotationId })), sampleClock, aux, campaignPlans,
            nowPlaying);
        var scheduler = new Scheduling.Scheduler(events, executor, log);
        scheduler.Fired += record =>
        {
            var ev = Messages.Event("scheduler.fired");
            ev["entry"] = EventJson.Execution(record);
            Broadcast(ev);
            // Omès, fallat o fet tard: incidència (fora del fil de control, perquè escriu a la base de dades)
            if (IncidentRules.FromExecution(record) is { } incident)
                _ = Task.Run(() => incidents.Report(incident.Kind, $"esdeveniment:{record.EventId}", incident.Severity, incident.Text));
        };
        scheduler.EventsChanged += () =>
        {
            RecalculatePlans();
            Broadcast(Messages.Event("scheduler.changed"));
        };

        // «Atura els esdeveniments» (fase 3, pas 2c; D-073): es desa, perquè si el motor es reinicia continuïn aturats fins
        // a l'hora prevista (i no més), i s'avisa la interfície
        const string PauseSettingKey = "esdeveniments_aturats";
        try
        {
            if (library.GetSetting(PauseSettingKey) is { } pauseJson && System.Text.Json.Nodes.JsonNode.Parse(pauseJson) is { } saved
                && saved["until"]?.GetValue<string>() is { } untilText && DateTimeOffset.TryParse(untilText, out var savedUntil)
                && savedUntil > DateTimeOffset.Now)
            {
                scheduler.Pause(savedUntil, saved["by"]?.GetValue<string>());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            log.LogWarning("L'aturada dels esdeveniments desada no és vàlida ({Message}): els esdeveniments funcionen", ex.Message);
        }
        scheduler.PauseChanged += () =>
        {
            var paused = EventJson.Paused(scheduler);
            try { library.SetSetting(PauseSettingKey, paused?.ToJsonString() ?? "null"); }
            catch (DatabaseException ex) { log.LogWarning("No s'ha pogut desar l'aturada dels esdeveniments: {Message}", ex.Message); }
            var ev = Messages.Event("scheduler.paused");
            ev["paused"] = paused;
            Broadcast(ev);
        };

        // Importació de carpetes des de la pantalla (fase 3, pas 7b): en segon pla; el progrés i, en acabar, library.changed
        var importer = new LibraryTools.LibraryImporter(library, log: message => log.LogInformation("{Message}", message));
        importer.Progress += status =>
        {
            var ev = Messages.Event("library.importProgress");
            ev["import"] = QueueJson.Import(status);
            Broadcast(ev);
            if (!status.Running)
            {
                var changed = Messages.Event("library.changed");
                changed["trackId"] = null;
                Broadcast(changed);
            }
        };

        // Carpetes vigilades (fase 3, pas 7c; D-090): es revisen cada 2 minuts (no mentre s'importa una carpeta des de la pantalla)
        var watcher = new LibraryTools.FolderWatcher(library, busy: () => importer.Status is { Running: true },
            log: message => log.LogInformation("{Message}", message));
        watcher.Scanned += libraryChanged =>
        {
            var ev = Messages.Event("folders.changed");
            ev["folders"] = QueueJson.Folders(watcher.Status);
            Broadcast(ev);
            if (libraryChanged)
            {
                var changed = Messages.Event("library.changed");
                changed["trackId"] = null;
                Broadcast(changed);
            }
        };

        // Agenda de bosses (D-170): el dia 1 de cada mes, les bosses passen a la subcategoria que els toca
        var agendaRunner = new BagAgendaRunner(new BagAgendas(database), autoDj, rotations, () => DateTimeOffset.UtcNow, TimeZoneInfo.Local, log, userStore);

        // engine.shutdown: el motor s'atura una mica després de respondre, perquè la resposta arribi al client.
        var router = new CommandRouter(state, () =>
            {
                runMarker.Stop(StopReason.Command);
                _ = Task.Delay(300).ContinueWith(_ => app.Lifetime.StopApplication());
            }, library,
            queue: queue, outputs: outputs, autoDj: autoDj, rotations: rotations, nowPlaying: nowPlaying,
            events: events, scheduler: scheduler, aux: aux, sweepers: sweepers, processor: processor, importer: importer, watcher: watcher,
            campaigns: campaigns, savedCalendars: new SavedCalendars(database), backups: backups,
            incidentStore: incidentStore, incidents: incidents, silence: silence, emergency: emergency, mail: mail, sessions: sessions, users: userStore, activity: new ActivityLog(database),
            agendas: agendaRunner);
        // Editor de rotacions (fase 3, pas 8a): les interfícies tornen a demanar les rotacions
        router.RotationsChanged += () => Broadcast(Messages.Event("rotations.changed"));
        // Agenda de bosses (D-170): les interfícies tornen a demanar les agendes
        router.AgendasChanged += () => Broadcast(Messages.Event("agendas.changed"));
        // Calendaris desats (fase 4, pas 6a): les interfícies tornen a demanar la llista
        router.SavedCalendarsChanged += () => Broadcast(Messages.Event("savedCalendars.changed"));
        // Campanyes (fase 4, pas 3a): les interfícies tornen a demanar-les
        router.CampaignsChanged += () =>
        {
            RecalculatePlans();
            Broadcast(Messages.Event("campaigns.changed"));
        };
        // Dades de l'emissora (fase 4, pas 4a): per a la capçalera dels informes
        router.StationChanged += () => Broadcast(Messages.Event("station.changed"));
        // Bosses amb nom (fase 3, pas 8c): també quan les canvia l'acció BOSSA d'un esdeveniment
        autoDj.BagsChanged += () =>
        {
            Broadcast(Messages.Event("bags.changed"));
            Broadcast(Messages.Event("rotations.changed")); // els passos diuen la subcategoria de la bossa
        };
        // Gestor de pistes (fase 3, pas 7a): quan es canvia una pista o les categories, les interfícies tornen a demanar el que mostren
        router.LibraryChanged += trackId =>
        {
            RecalculatePlans(); // les dates o l'estat d'una falca poden haver canviat
            var ev = Messages.Event("library.changed");
            ev["trackId"] = trackId;
            Broadcast(ev);
        };
        // Restauració d'una còpia (fase 4, pas 7b; D-152): la base de dades ha canviat sencera. Es torna a llegir el que el motor
        // té en memòria, sense aturar l'emissió, i s'avisa les interfícies perquè tornin a demanar el que mostren. La sortida
        // d'àudio no canvia (la restauració manté la d'ara).
        router.DatabaseRestored += () =>
        {
            log.LogWarning("S'ha restaurat una còpia de la base de dades: es torna a llegir la configuració");
            // L'AutoDJ: la rotació desada, sense canviar si està actiu o aturat
            long? rotationId = null;
            try
            {
                if (library.GetSetting(CommandRouter.AutoDjSettingKey) is { } json)
                    rotationId = System.Text.Json.Nodes.JsonNode.Parse(json)?["rotationId"]?.GetValue<long?>();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
            if (!autoDj.Configure(autoDj.Enabled, rotationId, "en restaurar una còpia")) autoDj.Configure(autoDj.Enabled, null, "en restaurar una còpia");
            autoDj.Separation = CommandRouter.ReadSeparation(library);
            autoDj.Reserve = CommandRouter.ReadReserve(library);
            ConfigureSweepers(autoDj, library, log);
            var ducking = CommandRouter.ReadDucking(library);
            aux.DefaultDuckingDb = ducking.Aux;
            sweepers.DuckingDb = ducking.Sweepers;
            ConfigureProcessor(processor, library, log);
            ConfigureSilence(silence, library);
            _ = Task.Run(() => emergency.SetFolder(CommandRouter.ReadEmergencyFolder(library)));
            CommandRouter.ConfigureMail(mail, library);
            sessions.Refresh(); // els usuaris no canvien en restaurar (D-164), però per si de cas
            sessions.Inactivity = CommandRouter.ReadInactivity(library);
            nowPlaying.Configure(ReadNowPlayingSettings(library, log));
            // Els esdeveniments i el pla de les campanyes (scheduler.changed)
            scheduler.Reload();
            foreach (string name in new[] { "rotations.changed", "bags.changed", "campaigns.changed", "savedCalendars.changed", "station.changed" })
                Broadcast(Messages.Event(name));
            var changed = Messages.Event("library.changed");
            changed["trackId"] = null;
            Broadcast(changed);
            var notes = Messages.Event("notes.changed");
            notes["text"] = library.GetSetting(CommandRouter.NotesSettingKey) ?? "";
            Broadcast(notes);
        };
        // Notes (fase 3, pas 4a): quan algú les desa, les altres interfícies connectades les reben
        router.NotesChanged += text =>
        {
            var ev = Messages.Event("notes.changed");
            ev["text"] = text;
            Broadcast(ev);
        };

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        // Canal principal amb la interfície.
        app.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!EngineOptions.TokenMatches(ctx.Request.Query["token"].ToString(), options.Token))
            {
                log.LogWarning("Connexió rebutjada: testimoni incorrecte");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // La connexió s'acaba si el client marxa o si el motor s'atura (si no, en aturar-se esperaria que el client tanqués).
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
            await hub.RunClientAsync(socket, router, ended.Token);
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            RuntimeFile.Write(options);
            Console.WriteLine($"{EngineInfo.Name} {EngineInfo.Version} (protocol {Messages.ProtocolVersion})");
            Console.WriteLine($"Escoltant a ws://127.0.0.1:{options.Port}/ws");
            Console.WriteLine($"Testimoni: {options.Token}");
            Console.WriteLine($"Fitxer d'execució: {RuntimeFile.FilePath}");
        });

        // Fil de control (docs/disseny-motor.md, 3): la cua cada 10 ms i la sortida cada 0,5 s. Mai no fa esperar el fil d'àudio.
        var control = new ControlLoop(queue, outputs, log, autoDj, scheduler, aux, sweepers, silence);

        // Comprovació de vida (sense testimoni). Només diu «ok» si el mesclador ha enviat àudio a la sortida en els
        // últims 2 segons (D-045) i el fil de control ha fet alguna volta en els últims 5 (fase 5, pas 2; D-157): un motor
        // penjat o sense sortida respon 503, i el vigilant el pot reiniciar.
        app.MapGet("/health", () =>
        {
            var since = mixer.SinceLastRender;
            if (since >= TimeSpan.FromSeconds(2))
                return Results.Text(since == TimeSpan.MaxValue ? "sense àudio: la sortida no ha començat" : $"sense àudio fa {since.TotalSeconds:0} s",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            var loop = control.SinceLastLoop;
            if (loop >= TimeSpan.FromSeconds(5))
                return Results.Text($"el fil de control no respon fa {loop.TotalSeconds:0} s", statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.Text("ok");
        });
        var deviceWatcher = new DeviceWatcher();
        deviceWatcher.Changed += _ => outputs.Poke();
        try { deviceWatcher.Start(); }
        catch (Exception ex) { log.LogWarning("No s'han pogut activar els avisos de dispositius: {Message}", ex.Message); }

        // Recàlcul dels punts amb el nostre analitzador, en segon pla (D-042)
        var recalculator = new LibraryTools.CueRecalculator(library, log: message => log.LogInformation("{Message}", message));

        // Pistes de durada variable (fase 4, pas 1; D-012): si el fitxer d'una pista de la cua canvia, durada i punts nous
        var changeChecker = new LibraryTools.TrackChangeChecker(library, queue, log: message => log.LogInformation("{Message}", message));
        changeChecker.TrackUpdated += trackId =>
        {
            var ev = Messages.Event("library.changed");
            ev["trackId"] = trackId;
            Broadcast(ev);
        };

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            control.Start();
            recalculator.Start();
            watcher.Start();
            changeChecker.Start();
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            log.LogInformation("Aturant el motor");
            changeChecker.Dispose();
            recalculator.Dispose();
            watcher.Dispose();
            importer.Dispose();
            deviceWatcher.Dispose();
            control.Dispose();
            history.CloseAll(); // abans d'aturar la cua: les que sonen es tanquen amb el temps que han sonat
            queue.Dispose();
            aux.Dispose();
            outputs.Dispose();
            RuntimeFile.DeleteIfOwned();
            runMarker.Stop(StopReason.Other); // si no n'hi ha cap de més concret (el senyal o l'ordre), per a la vegada següent (D-162)
            log.LogInformation("Motiu de l'aturada: {Reason}", runMarker.Reason);
        });

        var stopping = app.Lifetime.ApplicationStopping;

        // Incidències que duren (fase 5, pas 1): cada 2 s es mira com estan la sortida, l'AutoDJ, el títol per a BUTT i les
        // carpetes vigilades, i s'obren o es tanquen. Les d'un moment (una pista saltada, una pausa que continua sola) s'anoten
        // quan passen, fora del fil de control.
        // Les pistes que fallen seguides (incidència del NAS, 20-11-2026): a partir de la tercera, una sola incidència greu
        var failureBurst = new FailureBurst(incidents, new QueueSettings().FailuresForEmergency);
        queue.Started += failureBurst.Started;
        // Unitats de la música que no responen (D-197): una pista que falla o s'encalla en fa comprovar la unitat; mentre no
        // respon, les seves pistes s'esperen a la cua (sonen les d'emergència), amb una sola incidència greu per unitat
        var drives = new DriveWatch();
        queue.IsUnavailable = drives.IsDown;
        queue.Failed += e => { if (!e.Item.IsStream) drives.Suspect(e.Item.Path); };
        queue.Stuck += e => { if (!e.Item.IsStream) drives.Suspect(e.Item.Path); };
        // Les que s'han saltat mentre es comprovava (abans de saber-ho) tornen a la cua (D-199)
        drives.WentDown += (root, _) => queue.RequeueSkipped(path => string.Equals(DriveWatch.RootOf(path), root, StringComparison.OrdinalIgnoreCase));
        drives.WentDown += (root, since) => incidents.Set(IncidentKind.File, $"unitat:{root}", IncidentSeverity.Danger, IncidentRules.DriveDownText(root, since));
        drives.CameBack += (root, since) =>
        {
            // Primer el text amb el resum i després es tanca (així el registre diu quanta estona no ha respost)
            incidents.Set(IncidentKind.File, $"unitat:{root}", IncidentSeverity.Danger, IncidentRules.DriveBackText(root, since, DateTimeOffset.Now));
            incidents.Set(IncidentKind.File, $"unitat:{root}", IncidentSeverity.Danger, null);
        };
        queue.Failed += e => { if (failureBurst.Failed(e)) return; _ = Task.Run(() => incidents.Report(e.Item.IsStream ? IncidentKind.Stream : IncidentKind.File, $"fitxer:{e.Item.Path}",
            IncidentSeverity.Warning, e.Item.IsStream
                ? e.Fallback is { } fallback
                    ? $"No s'ha pogut {(e.Item.IsInput ? "obrir l'entrada" : "connectar al stream")} «{e.Item}»: {e.Sentence} Ha sonat la pista de reserva, «{fallback}»."
                    : $"No s'ha pogut {(e.Item.IsInput ? "obrir l'entrada" : "connectar al stream")} «{e.Item}» i s'ha saltat (sona la pista següent): {e.Message}"
                : $"No s'ha pogut fer sonar «{e.Item}» i s'ha saltat: {e.Message}", e.Item.Path, recovered: true)); };
        // Streams (fase 2, pas 7b; D-030): talls (oberta fins que es recupera o se salta), recuperacions i final per silenci
        queue.StreamNotice += e => _ = Task.Run(() =>
        {
            string key = $"stream:{e.Item.Path}";
            switch (e.Kind)
            {
                case "tall":
                    incidents.Set(IncidentKind.Stream, key, IncidentSeverity.Warning, $"{e.Item.SourceName()} s'ha tallat: {e.Message} Es prova de reconnectar.", e.Item.Path);
                    break;
                case "recuperat":
                    incidents.Set(IncidentKind.Stream, key, IncidentSeverity.Warning, null);
                    break;
                // Reconnexió durant tota la durada (D-031, D-180): oberta mentre és caigut; el resum, al final
                case "caigut":
                    incidents.Set(IncidentKind.Stream, key, IncidentSeverity.Warning, $"{e.Item.SourceName()} ha caigut: {e.Message}", e.Item.Path);
                    break;
                case "tornat":
                    incidents.Set(IncidentKind.Stream, key, IncidentSeverity.Warning, null);
                    incidents.Report(IncidentKind.Stream, key + ":tornat", IncidentSeverity.Info, $"{e.Item.SourceName()}: {e.Message}", e.Item.Path, recovered: true);
                    break;
                case "prova":
                    // Com va la connexió de prova (D-180): només al registre, per saber per què no ha tornat
                    log.LogInformation("{Source} «{Item}»: {Message}", e.Item.IsInput ? "Entrada" : "Stream", e.Item, e.Message);
                    break;
                case "resum":
                    incidents.Set(IncidentKind.Stream, key, IncidentSeverity.Warning, null);
                    incidents.Report(IncidentKind.Stream, key + ":resum", IncidentSeverity.Info, $"{e.Item.SourceName()} s'ha acabat. {e.Message}", e.Item.Path, recovered: true);
                    break;
                default:
                    incidents.Report(IncidentKind.Stream, key + ":silenci", IncidentSeverity.Warning, $"{e.Item.SourceName()} s'ha acabat abans d'hora. {e.Message}", e.Item.Path, recovered: true);
                    break;
            }
        });
        aux.Failed += e => _ = Task.Run(() => incidents.Report(IncidentKind.File, $"fitxer:{e.Item.Path}", IncidentSeverity.Warning,
            e.Player == AuxPlayers.SweeperPlayer
                ? $"No s'ha pogut fer sonar el sweeper «{e.Item}» (la pista ha sonat sense): {e.Message}"
                : $"No s'ha pogut fer sonar «{e.Item}» al reproductor auxiliar {e.Player}: {e.Message}", e.Item.Path));
        queue.Problem += message => _ = Task.Run(() => incidents.Report(IncidentKind.Pause, "pausa", IncidentSeverity.Warning, message, recovered: true));
        // Pista encallada i talls (fase 5, pas 2; D-157)
        queue.Stuck += e => _ = Task.Run(() =>
        {
            if (!e.Item.IsStream)
            {
                incidents.Report(IncidentKind.Stuck, $"fitxer:{e.Item.Path}", IncidentSeverity.Warning,
                    IncidentRules.StuckText(e.Item.ToString(), e.Position, e.Error), e.Item.Path, recovered: true);
                return;
            }
            // Un stream tallat que no s'ha recuperat a temps: s'ha passat a la següent (el tall queda tancat amb aquest text)
            incidents.Set(IncidentKind.Stream, $"stream:{e.Item.Path}", IncidentSeverity.Warning, null);
            incidents.Report(IncidentKind.Stream, $"stream:{e.Item.Path}:saltat", IncidentSeverity.Warning,
                $"{e.Item.SourceName()} s'ha tallat al minut {(int)e.Position.TotalMinutes}:{e.Position.Seconds:00} i no s'ha recuperat a temps: ha passat a la pista següent.", e.Item.Path, recovered: true);
        });
        queue.Ended += e =>
        {
            if (e.Dropouts > 0)
                _ = Task.Run(() => incidents.Report(IncidentKind.Dropouts, $"fitxer:{e.Item.Path}", IncidentSeverity.Warning,
                    IncidentRules.DropoutsText(e.Item.ToString(), e.Dropouts), e.Item.Path));
        };
        // Pistes d'emergència (fase 5, pas 4; D-159)
        queue.EmergencyStarted += e => _ = Task.Run(() => incidents.Report(IncidentKind.Emergency, $"fitxer:{e.Waiting.Path}", IncidentSeverity.Warning,
            IncidentRules.EmergencyText(e.Waiting.ToString(), e.Emergency.ToString()), e.Waiting.Path, recovered: true));
        _ = Task.Run(async () =>
        {
            // La carpeta d'emergència cada minut (algú hi pot afegir o treure cançons) i el fitxer «en marxa» cada 10 s
            using var timer = new PeriodicTimer(RunMarker.TouchInterval);
            int ticks = 0;
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    runMarker.Touch();
                    if (++ticks % 6 == 0) emergency.Refresh();
                }
            }
            catch (OperationCanceledException) { }
        });
        // Agenda de bosses (D-170): en arrencar (si el motor estava aturat el dia 1) i cada 30 s es mira si toca canviar-les
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                do
                {
                    try { agendaRunner.ApplyDue(); }
                    catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
                    {
                        log.LogWarning("Agenda de bosses: {Message}", ex.Message);
                    }
                }
                while (await timer.WaitForNextTickAsync(stopping));
            }
            catch (OperationCanceledException) { }
        });
        // Sessions (D-164): cada 15 s es tanquen les que fa massa estona que no tenen activitat, i se n'avisa la interfície
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    if (sessions.ExpireDue() is { Count: > 0 } expired) await hub.EndSessionsAsync(expired, router, stopping);
                }
            }
            catch (OperationCanceledException) { }
        });
        // Avisos per correu (D-160): cada 5 s es mira si toca enviar (l'enviament no bloqueja res més)
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    try { await mail.TickAsync(stopping); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { log.LogWarning("Avisos per correu: error ({Message})", ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
        });
        // Silenci a la sortida (fase 5, pas 3; D-158): una pista en silenci que s'ha saltat
        silence.Skipped += e => _ = Task.Run(() => incidents.Report(IncidentKind.Silence, $"fitxer:{e.Item.Path}", IncidentSeverity.Warning,
            IncidentRules.SilenceSkipText(e.Item.ToString(), e.Position, silence.Seconds), e.Item.Path, recovered: true));
        // Les carpetes, només després de la primera revisió: abans no se'n sap res, i es tancarien les que continuen d'abans
        bool foldersScanned = false;
        watcher.Scanned += _ => foldersScanned = true;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    try
                    {
                        incidents.Set(IncidentKind.Output, "sortida", IncidentSeverity.Danger, outputs.Problem);
                        incidents.Set(IncidentKind.AutoDj, "autodj", IncidentSeverity.Warning, autoDj.Problem);
                        // Greu (incidència del NAS, 20-11-2026): sense pistes d'emergència, una unitat de música que no respon és silenci
                        incidents.Set(IncidentKind.Emergency, "carpeta", IncidentSeverity.Danger, emergency.Problem);
                        incidents.Set(IncidentKind.Mail, "correu", IncidentSeverity.Warning, mail.Problem);
                        var silent = silence.Problem;
                        incidents.Set(IncidentKind.Silence, "silenci", silent?.Severity ?? IncidentSeverity.Danger, silent?.Text);
                        incidents.Set(IncidentKind.Butt, "butt", IncidentSeverity.Warning,
                            nowPlaying.Settings.Enabled && nowPlaying.Problem is { } butt ? $"Títol per a BUTT: {butt}" : null);
                        if (foldersScanned) incidents.SetAll(IncidentKind.Folder, IncidentSeverity.Warning, IncidentRules.Folders(watcher.Status));
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("Incidències: no s'ha pogut revisar l'estat ({Message})", ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // Cada segon, l'estat general a tots els clients.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    var ev = Messages.Event("engine.status");
                    foreach (var (key, value) in CommandRouter.EngineStatus(state))
                        ev[key] = value?.DeepClone();
                    await hub.BroadcastAsync(ev, stopping);
                }
            }
            catch (OperationCanceledException) { }
        });

        // Mesuradors i posició de la pista que sona, unes 20 vegades per segon, només si hi ha alguna interfície connectada
        // (fase 1, pas 10a). Si no n'hi ha cap, es buida el pic igualment perquè el primer valor no sigui d'abans.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            bool warned = false;
            try
            {
                while (await timer.WaitForNextTickAsync(stopping))
                {
                    var peak = mixer.Meter.Take();
                    float reduction = processor.TakeReductionDb();
                    if (state.Clients == 0) continue;
                    try
                    {
                        await hub.BroadcastAsync(QueueJson.Meters(peak, queue.Position, reduction), stopping);
                        warned = false;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!warned) log.LogWarning("No s'han pogut enviar els mesuradors: {Message}", ex.Message); // es torna a provar al següent
                        warned = true;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        // Còpia de seguretat diària (D-032): en arrencar i, després, cada hora es mira si avui ja se n'ha fet.
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
            try
            {
                do
                {
                    try
                    {
                        if (backups.CreateIfDue() is { } copy)
                            log.LogInformation("Còpia de seguretat: {Path}", copy);
                        // Les incidències es conserven 1 any (D-156); el registre de canvis, 2 anys (D-164)
                        incidents.Purge();
                        if (userStore.PurgeChanges(DateTimeOffset.UtcNow.AddYears(-2)) is var removed and > 0)
                            log.LogInformation("Registre de canvis: {Count} entrades de fa més de 2 anys, esborrades", removed);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning("No s'ha pogut fer la còpia de seguretat: {Message}", ex.Message);
                    }
                }
                while (await timer.WaitForNextTickAsync(stopping));
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            app.Run();
        }
        catch (IOException ex)
        {
            // Normalment, el port ja és ocupat (p. ex. el motor ja funciona).
            log.LogCritical("No s'ha pogut arrencar el servidor al port {Port}: {Message}", options.Port, ex.Message);
            logFile.Flush(TimeSpan.FromSeconds(2));
            return 1;
        }

        log.LogInformation("Motor aturat");
        return 0;
    }

    /// <summary>
    /// L'AutoDJ en arrencar el motor: sempre actiu (D-053), amb la rotació desada amb <c>autodj.set</c>. Si no n'hi ha cap
    /// de desada (primera vegada) o ja no existeix, es fa servir la primera, amb avís. Si s'havia aturat abans de tancar el
    /// motor, no es recorda: el motor sempre arrenca sonant (D-035).
    /// </summary>
    internal static void ConfigureAutoDj(AutoDj autoDj, Library library, ILogger log)
    {
        const bool enabled = true;
        long? rotationId = null;
        try
        {
            if (library.GetSetting(CommandRouter.AutoDjSettingKey) is { } json)
                rotationId = System.Text.Json.Nodes.JsonNode.Parse(json)?["rotationId"]?.GetValue<long?>();
        }
        catch (Exception ex)
        {
            log.LogWarning("La configuració desada de l'AutoDJ no és vàlida ({Message}): s'activa amb la primera rotació", ex.Message);
        }
        if (!autoDj.Configure(enabled, rotationId, "en arrencar el motor"))
        {
            log.LogWarning("La rotació desada ({Id}) ja no existeix: es fa servir la primera", rotationId);
            autoDj.Configure(enabled, null, "en arrencar el motor");
        }
    }

    /// <summary>Clau de la configuració amb la categoria de sweepers del trepitjador: <c>{ "subcategoryId": 55 }</c>.</summary>
    internal const string SweepersSettingKey = "sweepers";
    /// <summary>Clau de la configuració on es desa el títol fix per a BUTT (D-184).</summary>
    internal const string FixedTitleSettingKey = "nowplaying.fixed";

    /// <summary>
    /// La categoria de sweepers en arrencar el motor (fase 3, pas 5): la desada. Si no n'hi ha cap (primera vegada), cap:
    /// la posarà el primer esdeveniment amb l'acció SWEEPERS o l'operador.
    /// </summary>
    internal static void ConfigureSweepers(AutoDj autoDj, Library library, ILogger log)
    {
        try
        {
            if (library.GetSetting(SweepersSettingKey) is { } json
                && System.Text.Json.Nodes.JsonNode.Parse(json)?["subcategoryId"]?.GetValue<long?>() is { } id
                && !autoDj.SetSweepers(id, "en arrencar el motor"))
            {
                log.LogWarning("La categoria de sweepers desada ({Id}) ja no existeix: no n'hi ha cap", id);
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            log.LogWarning("La categoria de sweepers desada no és vàlida ({Message}): no n'hi ha cap", ex.Message);
        }
    }

    /// <summary>Silenci a la sortida (fase 5, pas 3; D-158): el llindar i els segons desats, o els per defecte.</summary>
    private static void ConfigureSilence(Supervision.SilenceDetector silence, Library library)
    {
        var (thresholdDb, seconds) = CommandRouter.ReadSilence(library);
        silence.ThresholdDb = thresholdDb;
        silence.Seconds = seconds;
    }

    /// <summary>Clau de la configuració amb el processador de sortida: <c>{ "preset": "suau", "volumeDb": 0 }</c>.</summary>
    internal const string ProcessorSettingKey = "processador";

    /// <summary>
    /// El processador de sortida en arrencar el motor (fase 3, pas 6): el desat. Si no n'hi ha (primera vegada) o no és
    /// vàlid, el per defecte: «Suau» i volum 0 dB.
    /// </summary>
    internal static void ConfigureProcessor(OutputProcessor processor, Library library, ILogger log)
    {
        try
        {
            if (library.GetSetting(ProcessorSettingKey) is { } json && System.Text.Json.Nodes.JsonNode.Parse(json) is { } saved)
                processor.Configure(saved["preset"]?.GetValue<string>() ?? OutputProcessor.StandardPresetId, saved["volumeDb"]?.GetValue<double>() ?? 0);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            log.LogWarning("El processador desat no és vàlid ({Message}): es fa servir el per defecte", ex.Message);
        }
    }

    /// <summary>L'exportació del títol, tal com es va desar amb <c>nowplaying.set</c>; si no n'hi ha o no és vàlida, la per defecte.</summary>
    private static NowPlayingSettings ReadNowPlayingSettings(Library library, ILogger log)
    {
        try
        {
            return library.GetSetting(CommandRouter.NowPlayingSettingKey) is { } json ? NowPlayingSettings.FromJson(json) : new NowPlayingSettings();
        }
        catch (Exception ex)
        {
            log.LogWarning("La configuració desada del títol per a BUTT no és vàlida ({Message}): es fa servir la per defecte", ex.Message);
            return new NowPlayingSettings();
        }
    }

    /// <summary>La sortida desada a la configuració (D-038). Si no n'hi ha cap, s'emet per la d'emergència.</summary>
    private static ConfiguredOutput ReadConfiguredOutput(Library library, ILogger log)
    {
        try
        {
            if (library.GetSetting(CommandRouter.OutputSettingKey) is not { } json) return new ConfiguredOutput(null, null);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            return new ConfiguredOutput(node?["id"]?.GetValue<string>(), node?["name"]?.GetValue<string>());
        }
        catch (Exception ex)
        {
            log.LogWarning("La sortida desada no és vàlida ({Message}): s'emetrà per la d'emergència", ex.Message);
            return new ConfiguredOutput(null, null);
        }
    }
}
