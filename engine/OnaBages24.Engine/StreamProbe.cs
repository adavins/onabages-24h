using System.Globalization;
using OnaBages.Audio;

namespace OnaBages24.Engine;

/// <summary>
/// Ordre <c>prova-stream</c> (fase 2, pas 7a; D-175): es connecta a un stream uns segons, sense fer sonar res, i diu si
/// funciona, el format, el títol, el nivell i quants segons envia el servidor de cop en connectar-s'hi (el retard que es
/// podria guanyar començant pel directe). Es pot fer amb el motor en marxa: no toca la base de dades ni la sortida.
/// </summary>
public static class StreamProbe
{
    public static int Run(string[] args)
    {
        string? address = null;
        double seconds = 10;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--segons":
                    if (i + 1 >= args.Length || !double.TryParse(args[++i].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                        || seconds is < 1 or > 600)
                        throw new ArgumentException("--segons ha de ser un nombre de segons entre 1 i 600.");
                    break;
                default:
                    if (address is not null || args[i].StartsWith("--")) throw new ArgumentException($"Opció desconeguda: {args[i]} (ajuda: OnaBages24.Engine ajuda)");
                    address = args[i];
                    break;
            }
        }
        if (address is null || !Uri.TryCreate(address, UriKind.Absolute, out var url))
            throw new ArgumentException("Falta l'adreça del stream (p. ex. prova-stream http://servidor:8000/directe).");

        Console.WriteLine($"Prova del stream {url}");
        Console.WriteLine("Connectant…");
        using var reader = new RadioStreamReader(url, 48000, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        // Fins que es connecta (o falla)
        while (reader.State == RadioStreamState.Connecting) Thread.Sleep(50);
        if (reader.Info is { } info)
        {
            var parts = new List<string> { $"connectat en {Number(reader.ConnectTime?.TotalSeconds ?? 0)} s" };
            if (info.Server is { } server) parts.Add($"servidor: {server}");
            if (info.Name is { } name) parts.Add($"nom: «{name}»");
            if (info.ContentType is { } type) parts.Add($"tipus: {type}");
            if (info.BitrateKbps is { } kbps) parts.Add($"{kbps} kbps");
            if (info.Url != url) parts.Add($"redirigit a {info.Url}");
            Console.WriteLine(Capitalize(string.Join(" · ", parts)));
        }

        // Uns segons rebent: cada segon, el que ha arribat i el nivell
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        int shown = 0;
        while (DateTime.UtcNow < until && reader.State == RadioStreamState.Receiving)
        {
            Thread.Sleep(100);
            reader.Buffer.Skip(reader.Buffer.Count); // no es fa sonar: es llença
            int second = (int)(seconds - (until - DateTime.UtcNow).TotalSeconds);
            if (second > shown)
            {
                shown = second;
                Console.WriteLine($"  {second,3} s · rebut {Number(reader.Received.TotalSeconds)} s d'àudio · pic {Db(reader.PeakDb)}" +
                                  (reader.Title is { } t ? $" · «{t}»" : ""));
            }
        }

        Console.WriteLine();
        if (reader.State == RadioStreamState.Failed)
        {
            Console.WriteLine($"No funciona: {reader.Error}");
            if (reader.Received > TimeSpan.Zero) Console.WriteLine($"(Abans de fallar n'havien arribat {Number(reader.Received.TotalSeconds)} s.)");
            return 1;
        }
        Console.WriteLine("Resum");
        Console.WriteLine($"  Format: {reader.Format ?? "no ha arribat àudio"}");
        if (reader.Title is { } title) Console.WriteLine($"  Títol que envia: «{title}»");
        Console.WriteLine($"  Ràfega en connectar-s'hi: {Number(reader.Burst.TotalSeconds)} s (el retard que hi afegeix el servidor: es pot guanyar començant pel directe)");
        Console.WriteLine($"  Pic màxim: {Db(reader.MaxPeakDb)}" + (reader.SilentFor >= TimeSpan.FromSeconds(1) ? $" · arriba en silenci des de fa {Number(reader.SilentFor.TotalSeconds)} s" : ""));
        if (reader.Received < TimeSpan.FromSeconds(seconds * 0.8))
            Console.WriteLine($"  Atenció: en {Number(seconds)} s només han arribat {Number(reader.Received.TotalSeconds)} s d'àudio (connexió lenta o servidor saturat).");
        bool ok = reader.Format is not null && reader.MaxPeakDb > RadioStreamReader.DefaultSilenceDb;
        Console.WriteLine(ok ? "Funciona: es pot fer sonar." : reader.Format is null ? "No ha arribat àudio." : "Arriba, però en silenci.");
        return ok ? 0 : 1;
    }

    private static string Number(double value) => value.ToString("0.0", CultureInfo.GetCultureInfo("ca-ES"));

    private static string Db(double db) => double.IsNegativeInfinity(db) ? "silenci" : $"{db.ToString("0", CultureInfo.GetCultureInfo("ca-ES")).Replace("-", "−")} dB";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
}
