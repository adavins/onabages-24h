using System.Net;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Infrastructure;
using OnaBages24.Engine.Playback;
using OnaBages24.Engine.Protocol;

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
        // Rellotge de mostres (fase 2, pas 4): l'hora en què sona cada fotograma, amb la latència mesurada de la sortida (pas 4b)
        var sampleClock = new SampleClock(mixer, () => outputs.Latency);
        // Reproductors auxiliars (fase 2, pas 5): senyals horaris per sobre de la música, amb atenuació opcional
        var aux = new AuxPlayers(mixer);
        // Atenuació per defecte dels auxiliars (D-078): la desada a la configuració, o −10 dB
        aux.DefaultDuckingDb = CommandRouter.ReadDucking(library).Aux;

        // AutoDJ (fase 1, pas 8): arrenca sol amb la rotació desada (o la primera), i l'emissió comença de seguida
        var rotations = new Rotations(database);
        var autoDj = new AutoDj(library, rotations, queue);
        // Trepitjador (fase 3, pas 5): el sweeper a sobre de l'inici de la pista, amb l'atenuació desada (D-078) o −8 dB
        var sweepers = new SweeperOverlay(queue, aux) { DuckingDb = CommandRouter.ReadDucking(library).Sweepers };
        var playback = new PlaybackEvents(queue, outputs, log, Broadcast, autoDj, sampleClock, aux);
        var history = new PlayHistory(queue, library, log, aux);
        // Les pistes que sonaven si el motor es va aturar de cop (fase 3, pas 2a): es tanquen com a tallades
        if (library.CloseOpenHistory() is var closed and > 0)
            log.LogInformation("Historial: {Count} pistes que sonaven quan el motor es va aturar, tancades com a tallades", closed);
        ConfigureAutoDj(autoDj, library, log);
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

        // Planificador (fase 2, pas 3; D-008): al fil de control, amb el rellotge del motor. Si un esdeveniment canvia la
        // rotació, es desa com amb autodj.set, perquè es mantingui si el motor es reinicia (D-053).
        var events = new Events(database);
        // TALLA a la mostra exacta (fase 2, pas 4): l'hora es converteix en el fotograma que sonarà, amb el rellotge de mostres
        var executor = new Scheduling.EventExecutor(library, queue, autoDj, rotationId =>
            library.SetSetting(CommandRouter.AutoDjSettingKey, System.Text.Json.JsonSerializer.Serialize(new { rotationId })), sampleClock, aux);
        var scheduler = new Scheduling.Scheduler(events, executor, log);
        scheduler.Fired += record =>
        {
            var ev = Messages.Event("scheduler.fired");
            ev["entry"] = EventJson.Execution(record);
            Broadcast(ev);
        };
        scheduler.EventsChanged += () => Broadcast(Messages.Event("scheduler.changed"));

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

        // engine.shutdown: el motor s'atura una mica després de respondre, perquè la resposta arribi al client.
        var router = new CommandRouter(state, () => _ = Task.Delay(300).ContinueWith(_ => app.Lifetime.StopApplication()), library,
            queue: queue, outputs: outputs, autoDj: autoDj, rotations: rotations, nowPlaying: nowPlaying,
            events: events, scheduler: scheduler, aux: aux, sweepers: sweepers, processor: processor, importer: importer, watcher: watcher);
        // Gestor de pistes (fase 3, pas 7a): quan es canvia una pista o les categories, les interfícies tornen a demanar el que mostren
        router.LibraryChanged += trackId =>
        {
            var ev = Messages.Event("library.changed");
            ev["trackId"] = trackId;
            Broadcast(ev);
        };
        // Notes (fase 3, pas 4a): quan algú les desa, les altres interfícies connectades les reben
        router.NotesChanged += text =>
        {
            var ev = Messages.Event("notes.changed");
            ev["text"] = text;
            Broadcast(ev);
        };

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        // Comprovació de vida (sense testimoni). Només diu «ok» si el mesclador ha enviat àudio a la sortida en els
        // últims 2 segons (D-045): un motor penjat o sense sortida respon 503, i el vigilant el pot reiniciar.
        app.MapGet("/health", () =>
        {
            var since = mixer.SinceLastRender;
            return since < TimeSpan.FromSeconds(2)
                ? Results.Text("ok")
                : Results.Text(since == TimeSpan.MaxValue ? "sense àudio: la sortida no ha començat" : $"sense àudio fa {since.TotalSeconds:0} s",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        });

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
        var control = new ControlLoop(queue, outputs, log, autoDj, scheduler, aux, sweepers);
        var deviceWatcher = new DeviceWatcher();
        deviceWatcher.Changed += _ => outputs.Poke();
        try { deviceWatcher.Start(); }
        catch (Exception ex) { log.LogWarning("No s'han pogut activar els avisos de dispositius: {Message}", ex.Message); }

        // Recàlcul dels punts amb el nostre analitzador, en segon pla (D-042)
        var recalculator = new LibraryTools.CueRecalculator(library, log: message => log.LogInformation("{Message}", message));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            control.Start();
            recalculator.Start();
            watcher.Start();
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            log.LogInformation("Aturant el motor");
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
        });

        var stopping = app.Lifetime.ApplicationStopping;

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
