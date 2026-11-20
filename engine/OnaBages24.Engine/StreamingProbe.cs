using System.Globalization;
using OnaBages.Audio;
using OnaBages24.Engine.Streaming;

namespace OnaBages24.Engine;

/// <summary>
/// Ordre <c>prova-emissio</c> (emissió per internet, D-202): emet una entrada de Windows a un servidor (Icecast o
/// Shoutcast) uns segons, amb el mateix emissor que farà servir el motor, i diu cada segon com va (connectat, enviat,
/// nivell, reconnexions). Serveix per provar un servidor i un punt de muntatge de prova abans de configurar-ho.
/// La contrasenya es demana per pantalla (no a la línia d'ordres, que quedaria a l'historial). Es pot fer amb el motor en
/// marxa: Windows deixa captar la mateixa entrada diverses vegades, i no toca la base de dades ni la sortida. Compte: un
/// punt de muntatge on ja emet un altre programa (BUTT o el motor) no l'accepta.
/// </summary>
public static class StreamingProbe
{
    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("ca-ES");

    public static int Run(string[] args)
    {
        string? which = null;
        double seconds = 60;
        string? title = null;
        var output = new StreamOutput { Name = "Prova", Host = "" };
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}.");
            int Number(string option, int min, int max)
            {
                string text = Value();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max
                    ? n : throw new ArgumentException($"{option} ha de ser un nombre entre {min} i {max}: «{text}».");
            }
            switch (args[i])
            {
                case "--servidor":
                    output = output with
                    {
                        ServerType = Value().ToLowerInvariant() switch
                        {
                            "icecast" => StreamServerType.Icecast,
                            "shoutcast" => StreamServerType.Shoutcast,
                            var other => throw new ArgumentException($"--servidor ha de ser icecast o shoutcast: «{other}»."),
                        },
                    };
                    break;
                case "--adreca": output = output with { Host = StreamOutput.CleanHost(Value()) }; break;
                case "--port": output = output with { Port = Number("--port", 1, 65535) }; break;
                case "--muntatge":
                    string mount = Value();
                    output = output with { Mount = mount.StartsWith('/') ? mount : "/" + mount };
                    break;
                case "--usuari": output = output with { User = Value() }; break;
                case "--sid": output = output with { StreamId = Number("--sid", 1, 999) }; break;
                case "--kbps": output = output with { BitrateKbps = Number("--kbps", 32, 320) }; break;
                case "--hz": output = output with { SampleRate = Number("--hz", 8000, 96000) }; break;
                case "--mono": output = output with { Mono = true }; break;
                case "--titol": title = Value(); break;
                case "--segons": seconds = Number("--segons", 5, 3600); break;
                default:
                    if (which is not null || args[i].StartsWith("--")) throw new ArgumentException($"Opció desconeguda: {args[i]} (ajuda: OnaBages24.Engine ajuda)");
                    which = args[i];
                    break;
            }
        }

        var inputs = DeviceList.GetAll().Where(d => d.Kind == DeviceKind.Input).OrderBy(d => d.Status).ThenBy(d => d.Name).ToList();
        if (which is null)
        {
            Console.WriteLine("Falta l'entrada que s'emet (el número de la llista o el nom).");
            InputProbe.ShowList(inputs);
            return 1;
        }
        var device = InputProbe.Find(inputs, which);
        if (device is null || device.Status != DeviceStatus.Active)
        {
            Console.WriteLine(device is null ? $"No es troba l'entrada «{which}»." : $"L'entrada «{device.Name}» no està activa.");
            InputProbe.ShowList(inputs);
            return 1;
        }
        if (output.ServerProblem() is { } problem)
        {
            Console.WriteLine(problem);
            return 1;
        }

        string password = StopCommand.ReadHidden($"Contrasenya del servidor {output.Host}: ");
        if (password.Length == 0)
        {
            Console.WriteLine("Sense contrasenya no es pot emetre.");
            return 1;
        }

        Console.WriteLine($"Prova d'emissió: «{device.Name}» → {(output.ServerType == StreamServerType.Icecast ? $"Icecast {output.Host}:{output.Port}{output.Mount}" : $"Shoutcast {output.Host}:{output.Port} (sid {output.StreamId})")}");
        Console.WriteLine($"MP3 {output.BitrateKbps} kbps, {output.SampleRate.ToString("N0", Ca)} Hz, {(output.Mono ? "mono" : "estèreo")}, {seconds} s. Ctrl+C per acabar abans.");

        using var streamer = new Streamer("prova", _ => LineInputReader.OpenDevice(device.Id, device.Name));
        streamer.SetDevice(device.Id);
        streamer.SetServer(output.ToServer(password), new StreamInfo { Name = "Ona Bages 24h (prova)" });
        streamer.SetQuality(output.BitrateKbps, output.SampleRate, output.Mono);
        streamer.SetTitle(title ?? "Prova d'emissió d'Ona Bages 24h");

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        streamer.Start();

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        var lastState = streamer.State;
        string? lastError = null;
        int shown = 0;
        while (DateTime.UtcNow < until && !cancel.IsCancellationRequested)
        {
            Thread.Sleep(100);
            streamer.Tick();
            var (left, right) = streamer.Meter.Take();
            float peak = Math.Max(left, right);
            string? error = streamer.State == StreamerState.Error ? streamer.ErrorMessage : streamer.ConnectionError;
            if (streamer.State != lastState || error != lastError)
            {
                Console.WriteLine($"  {StateText(streamer.State)}{(error is null ? "" : $": {error}")}");
                lastState = streamer.State;
                lastError = error;
            }
            if (streamer.State == StreamerState.Error) break;
            int second = (int)(seconds - (until - DateTime.UtcNow).TotalSeconds);
            if (second > shown && second % 5 == 0)
            {
                shown = second;
                Console.WriteLine($"  {second,4} s · enviat {(streamer.BytesSent / 1024.0).ToString("N0", Ca)} kB · nivell {Db(peak)}"
                    + (streamer.Reconnects > 0 ? $" · {streamer.Reconnects} reconnexions" : "")
                    + (streamer.BytesDropped > 0 ? $" · {(streamer.BytesDropped / 1024.0).ToString("N0", Ca)} kB perduts (la xarxa no hi arriba)" : "")
                    + (streamer.TitleError is { } t ? $" · títol: {t}" : ""));
            }
        }

        bool ok = streamer.State == StreamerState.Streaming && streamer.BytesSent > 0;
        if (streamer.IsActive) streamer.Stop();
        Console.WriteLine(ok
            ? $"Ha funcionat: s'han enviat {(streamer.BytesSent / 1024.0).ToString("N0", Ca)} kB. Escolta el punt de muntatge per comprovar el so i el títol."
            : "No ha funcionat (vegeu el missatge de més amunt).");
        return ok ? 0 : 1;
    }

    private static string StateText(StreamerState state) => state switch
    {
        StreamerState.Connecting => "Connectant…",
        StreamerState.Streaming => "Connectat: s'emet",
        StreamerState.Reconnecting => "S'ha tallat: es torna a connectar",
        StreamerState.Error => "Aturat",
        _ => "Aturat",
    };

    private static string Db(float peak) => peak <= 0.00001f ? "silenci" : $"{(20 * Math.Log10(peak)).ToString("0", Ca).Replace("-", "−")} dB";
}
