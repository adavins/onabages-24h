using System.Net;
using OnaBages24.Data;
using OnaBages24.Engine.Infrastructure;
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
        // engine.shutdown: el motor s'atura una mica després de respondre, perquè la resposta arribi al client.
        var router = new CommandRouter(state, () => _ = Task.Delay(300).ContinueWith(_ => app.Lifetime.StopApplication()), library);

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        // Comprovació de vida (sense testimoni): la fa servir la interfície per saber si el motor ja funciona.
        app.MapGet("/health", () => Results.Text("ok"));

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

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            log.LogInformation("Aturant el motor");
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
}
