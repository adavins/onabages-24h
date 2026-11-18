using System.Globalization;
using OnaBages.Audio;

namespace OnaBages24.Engine;

/// <summary>
/// Ordre <c>prova-entrada</c> (entrades de línia, D-185): sense res, llista les entrades de Windows; amb una entrada (el
/// número de la llista, el nom o l'identificador), l'escolta uns segons sense fer sonar res i diu si funciona, el format, el
/// nivell, el silenci i a quina velocitat va el seu rellotge comparat amb el de l'ordinador. Es pot fer amb el motor en
/// marxa: Windows deixa que diversos programes escoltin la mateixa entrada, i no toca la base de dades ni la sortida.
/// </summary>
public static class InputProbe
{
    public static int Run(string[] args)
    {
        string? which = null;
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
                    if (which is not null || args[i].StartsWith("--")) throw new ArgumentException($"Opció desconeguda: {args[i]} (ajuda: OnaBages24.Engine ajuda)");
                    which = args[i];
                    break;
            }
        }

        var inputs = DeviceList.GetAll().Where(d => d.Kind == DeviceKind.Input).OrderBy(d => d.Status).ThenBy(d => d.Name).ToList();
        if (which is null)
        {
            ShowList(inputs);
            return 0;
        }

        var device = Find(inputs, which);
        if (device is null)
        {
            Console.WriteLine($"No es troba l'entrada «{which}».");
            ShowList(inputs);
            return 1;
        }
        if (device.Status != DeviceStatus.Active)
        {
            Console.WriteLine($"L'entrada «{device.Name}» no està activa ({StatusText(device.Status)}).");
            return 1;
        }

        Console.WriteLine($"Prova de l'entrada «{device.Name}»");
        Console.WriteLine("Obrint…");
        using var reader = new LineInputReader(() => LineInputReader.OpenDevice(device.Id, device.Name), 48000, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        while (reader.State == LineInputState.Opening) Thread.Sleep(50);
        if (reader.Format is { } format) Console.WriteLine($"Format de l'entrada: {format}");

        // Uns segons escoltant: cada segon, el que ha arribat i el nivell
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        int shown = 0;
        while (DateTime.UtcNow < until && reader.State == LineInputState.Receiving)
        {
            Thread.Sleep(100);
            reader.Buffer.Skip(reader.Buffer.Count); // no es fa sonar: es llença
            int second = (int)(seconds - (until - DateTime.UtcNow).TotalSeconds);
            if (second > shown)
            {
                shown = second;
                Console.WriteLine($"  {second,3} s · rebut {Number(reader.Received.TotalSeconds)} s d'àudio · pic {Db(reader.PeakDb)}");
            }
        }

        Console.WriteLine();
        if (reader.State == LineInputState.Failed)
        {
            Console.WriteLine($"No funciona: {reader.Error}");
            return 1;
        }
        Console.WriteLine("Resum");
        Console.WriteLine($"  Format: {reader.Format}");
        Console.WriteLine($"  Pic màxim: {Db(reader.MaxPeakDb)}" + (reader.SilentFor >= TimeSpan.FromSeconds(1) ? $" · arriba en silenci des de fa {Number(reader.SilentFor.TotalSeconds)} s" : ""));
        if (reader.ClockPpm is { } ppm)
        {
            double perHour = Math.Abs(ppm) * 3600 / 1e6;
            Console.WriteLine($"  Rellotge de l'entrada: {(ppm >= 0 ? "+" : "−")}{Math.Abs(ppm).ToString("0.0", Ca)} ppm respecte del de l'ordinador " +
                              $"({(ppm >= 0 ? "envia" : "falta")} {perHour.ToString("0.00", Ca)} s per hora; el motor ho corregirà sense que se senti)");
        }
        else Console.WriteLine("  Rellotge de l'entrada: cal escoltar-la més estona per mesurar-lo (--segons 30).");
        if (reader.Discontinuities > 0) Console.WriteLine($"  Atenció: Windows diu que s'han perdut dades {(reader.Discontinuities == 1 ? "1 vegada" : $"{reader.Discontinuities} vegades")} (ordinador carregat o controlador amb problemes).");
        bool ok = reader.MaxPeakDb > LineInputReader.DefaultSilenceDb;
        Console.WriteLine(ok ? "Funciona: arriba so." : "S'obre, però arriba en silenci (hi ha res connectat i sonant?).");
        return ok ? 0 : 1;
    }

    private static readonly CultureInfo Ca = CultureInfo.GetCultureInfo("ca-ES");

    /// <summary>L'entrada pel número de la llista, l'identificador o el nom (primer l'exacte i després el que el contingui).</summary>
    private static AudioDevice? Find(IReadOnlyList<AudioDevice> inputs, string which)
    {
        if (int.TryParse(which, out int number) && number >= 1 && number <= inputs.Count) return inputs[number - 1];
        return inputs.FirstOrDefault(d => d.Id == which)
            ?? inputs.FirstOrDefault(d => string.Equals(d.Name, which, StringComparison.OrdinalIgnoreCase))
            ?? inputs.FirstOrDefault(d => d.Name.Contains(which, StringComparison.OrdinalIgnoreCase));
    }

    private static void ShowList(IReadOnlyList<AudioDevice> inputs)
    {
        if (inputs.Count == 0)
        {
            Console.WriteLine("Windows no té cap entrada d'àudio.");
            return;
        }
        Console.WriteLine("Entrades de Windows:");
        for (int i = 0; i < inputs.Count; i++)
        {
            var d = inputs[i];
            string format = d.Format is { } f ? $" · {f.SampleRate.ToString("N0", Ca)} Hz, {(f.Channels == 1 ? "mono" : f.Channels == 2 ? "estèreo" : $"{f.Channels} canals")}" : "";
            Console.WriteLine($"  [{i + 1}] {d.Name}{format}{(d.Status == DeviceStatus.Active ? "" : $" · {StatusText(d.Status)}")}");
        }
        Console.WriteLine("Per provar-ne una: prova-entrada NÚMERO (o el nom) [--segons N]");
    }

    private static string StatusText(DeviceStatus status) => status switch
    {
        DeviceStatus.Disabled => "desactivada a Windows",
        DeviceStatus.Unplugged => "no hi ha res connectat al connector",
        _ => "activa",
    };

    private static string Number(double value) => value.ToString("0.0", Ca);

    private static string Db(double db) => double.IsNegativeInfinity(db) ? "silenci" : $"{db.ToString("0", Ca).Replace("-", "−")} dB";
}
