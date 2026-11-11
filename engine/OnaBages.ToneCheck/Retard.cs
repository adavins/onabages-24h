using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using OnaBages.Audio;

namespace OnaBages.ToneCheck;

/// <summary>
/// Mesura de veritat del retard de la sortida (Ona Bages 24h, fase 2, pas 4b): comprova amb el so que surt de debò que
/// una pista comença a l'hora que el motor diu (<c>heardAt</c> de <c>player.trackStarted</c>).
///
/// Com ho fa:
/// 1. Es connecta al motor (port i testimoni de <c>dades\engine.json</c>) i li pregunta per quina sortida emet.
/// 2. Comença a enregistrar: per defecte, el que surt per aquella sortida (loopback de Windows, sense cable); amb
///    <c>--entrada</c>, una entrada de la targeta connectada amb un cable a la sortida (així també compta el maquinari).
/// 3. Posa a sonar de seguida (tall sec) un fitxer amb 1 s de silenci i 5 xiulets, un cada 2 s.
/// 4. Busca l'inici de cada xiulet a l'enregistrament (amb l'hora que Windows dona a cada paquet capturat) i el compara
///    amb l'hora prevista: <c>heardAt</c> + la posició del xiulet al fitxer.
///
/// Atenció: talla el que soni i fa uns segons de silenci. Només en un ordinador de proves, mai en antena.
///
/// Amb <c>--auxiliar</c> (fase 2, pas 5b), els xiulets sonen pel reproductor auxiliar 1, com els senyals horaris, i es
/// compara amb el <c>heardAt</c> de <c>aux.started</c>. No talla la música, però l'atenua del tot mentre sonen.
/// </summary>
internal static class Retard
{
    private const int Rate = 48000;
    private const int Beeps = 5;
    private const double FirstBeepSeconds = 1.0;
    private const double BeepEverySeconds = 2.0;
    private const double BeepSeconds = 0.1;
    private const double FileSeconds = FirstBeepSeconds + (Beeps - 1) * BeepEverySeconds + BeepSeconds + 0.5;

    /// <summary>Diferència màxima acceptada entre l'hora prevista i la de debò.</summary>
    private const double ToleranceMs = 10;

    /// <summary>Un xiulet comença quan el so passa d'això després d'almenys 200 ms de silenci (el xiulet fa 0,5).</summary>
    private const float Threshold = 0.02f;

    /// <param name="auxiliary">Pel reproductor auxiliar (com els senyals horaris) en lloc de la cua: no talla la música.</param>
    public static async Task<int> RunAsync(string? dataDir, string? input, bool auxiliary = false)
    {
        // 1. El motor
        string? runtimePath = FindRuntimeFile(dataDir);
        if (runtimePath is null)
        {
            Console.Error.WriteLine("No es troba dades\\engine.json: el motor no funciona, o cal dir-li la carpeta amb --dades.");
            return 2;
        }
        var runtime = JsonNode.Parse(File.ReadAllText(runtimePath))!;
        int port = runtime["port"]!.GetValue<int>();
        string token = runtime["token"]!.GetValue<string>();

        using var engine = new EngineClient();
        try
        {
            await engine.ConnectAsync(port, token);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            Console.Error.WriteLine($"No s'ha pogut connectar amb el motor (port {port}): {ex.Message}");
            return 2;
        }

        var output = (await engine.CommandAsync("output.get"))["output"]!;
        var current = output["current"];
        if (current is null)
        {
            Console.Error.WriteLine("El motor no té cap sortida oberta: no es pot mesurar.");
            return 2;
        }
        string outputName = current["name"]!.GetValue<string>();
        string? outputId = current["id"]?.GetValue<string>()
            ?? DeviceList.GetAll().FirstOrDefault(d => d.Kind == DeviceKind.Output && d.Name == outputName)?.Id;
        double latencyMs = output["latencyMs"]?.GetValue<double>() ?? double.NaN;
        bool measured = output["latencyMeasured"]?.GetValue<bool>() ?? false;

        // 2. Què s'enregistra
        string? deviceId;
        bool loopback = input is null;
        if (loopback)
        {
            deviceId = outputId;
            if (deviceId is null)
            {
                Console.Error.WriteLine($"No es troba la sortida «{outputName}» a Windows.");
                return 2;
            }
        }
        else
        {
            var inputs = DeviceList.GetAll().Where(d => d.Kind == DeviceKind.Input && d.Status == DeviceStatus.Active).ToList();
            var found = inputs.Where(d => d.Id == input || d.Name.Contains(input!, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count != 1)
            {
                Console.Error.WriteLine(found.Count == 0 ? $"No hi ha cap entrada activa que es digui «{input}»." : $"Hi ha més d'una entrada que es diu «{input}».");
                Console.Error.WriteLine("Entrades actives:");
                foreach (var d in inputs) Console.Error.WriteLine($"  {d.Name}");
                return 2;
            }
            deviceId = found[0].Id;
            Console.WriteLine($"Entrada: {found[0].Name} (amb un cable des de la sortida)");
        }

        Console.WriteLine($"Sortida del motor: {outputName}");
        Console.WriteLine($"Latència que fa servir el motor: {latencyMs:0.0} ms ({(measured ? "mesurada" : "suposada")})");

        string wav = Path.Combine(Path.GetTempPath(), "OnaBagesRetard", "xiulets.wav");
        WriteBeeps(wav);

        // 3. Enregistrar i fer sonar el fitxer
        using var capture = Capture.Open(deviceId, loopback, seconds: FileSeconds + 15);
        capture.Start();
        await Task.Delay(500);

        if (auxiliary)
        {
            // Pel reproductor auxiliar 1, com els senyals horaris (fase 2, pas 5). La música no s'atura: s'atenua del tot
            // (−60 dB) perquè no es confongui amb els xiulets, i torna a pujar quan s'acaben.
            Console.WriteLine("Fent sonar els xiulets pel reproductor auxiliar 1, per sobre de la música (atenuada)...");
            await engine.CommandAsync("aux.play", new JsonObject { ["path"] = wav, ["player"] = 1, ["duckingDb"] = -60 });
        }
        else
        {
            Console.WriteLine("Fent sonar els xiulets (tall sec)...");
            await engine.CommandAsync("queue.insert", new JsonObject
            {
                ["items"] = new JsonArray(new JsonObject { ["path"] = wav }),
                ["position"] = "next",
            });
            await engine.CommandAsync("player.next", new JsonObject { ["fadeMs"] = 0 });
        }

        var started = await engine.WaitForEventAsync(e =>
            e["ev"]?.GetValue<string>() == (auxiliary ? "aux.started" : "player.trackStarted") &&
            string.Equals(e["item"]?["path"]?.GetValue<string>(), wav, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10));
        if (started is null)
        {
            capture.Stop();
            Console.Error.WriteLine("El motor no ha començat el fitxer dels xiulets en 10 s.");
            return 1;
        }
        if (started["heardAt"]?.GetValue<string>() is not { } heardText)
        {
            capture.Stop();
            Console.Error.WriteLine("El motor no diu quan se sent la pista (heardAt): és d'una versió anterior al pas 4b?");
            return 1;
        }
        var heardAt = DateTimeOffset.Parse(heardText, CultureInfo.InvariantCulture);

        var until = heardAt + TimeSpan.FromSeconds(FileSeconds);
        while (DateTimeOffset.Now < until) await Task.Delay(100);
        await Task.Delay(300);
        capture.Stop();

        // 4. Comparar
        Console.WriteLine();
        Console.WriteLine($"El motor diu que el fitxer comença a sonar a les {heardAt.LocalDateTime:HH:mm:ss.fff}.");
        Console.WriteLine(loopback
            ? "Enregistrat: el que surt per la sortida (loopback, sense cable)."
            : "Enregistrat: l'entrada (amb cable).");
        Console.WriteLine();
        Console.WriteLine("Xiulet  Previst        Enregistrat     Diferència");
        var diffs = new List<double>();
        for (int k = 0; k < Beeps; k++)
        {
            var expected = heardAt + TimeSpan.FromSeconds(FirstBeepSeconds + k * BeepEverySeconds);
            var onset = capture.FindOnset(expected - TimeSpan.FromMilliseconds(300), expected + TimeSpan.FromMilliseconds(300), Threshold);
            if (onset is not { } heard)
            {
                Console.WriteLine($"  {k + 1}     {expected.LocalDateTime:HH:mm:ss.fff}   no s'ha trobat");
                continue;
            }
            double diff = (heard - expected).TotalMilliseconds;
            diffs.Add(diff);
            Console.WriteLine($"  {k + 1}     {expected.LocalDateTime:HH:mm:ss.fff}   {heard.LocalDateTime:HH:mm:ss.fff}    {diff,+7:+0.0;-0.0;0.0} ms");
        }

        Console.WriteLine();
        if (capture.Overflowed) Console.WriteLine("Avís: l'enregistrament s'ha omplert abans d'acabar.");
        if (diffs.Count == 0)
        {
            Console.WriteLine(loopback
                ? "RESULTAT: no s'ha trobat cap xiulet. El volum de la sortida és a zero, o el motor no emet per on es pensa?"
                : "RESULTAT: no s'ha trobat cap xiulet. Comprova el cable, que l'entrada sigui la bona i que tingui volum.");
            return 1;
        }

        double average = diffs.Average();
        double worst = diffs.Max(Math.Abs);
        Console.WriteLine($"Diferència mitjana: {average:+0.0;-0.0;0.0} ms (positiva: sona més tard del que diu el motor)");
        Console.WriteLine($"Diferència màxima:  {worst:0.0} ms");
        bool ok = diffs.Count == Beeps && worst <= ToleranceMs;
        Console.WriteLine(ok
            ? $"RESULTAT: CORRECTE (tots els xiulets a menys de {ToleranceMs:0} ms de l'hora prevista)."
            : $"RESULTAT: HI HA PROBLEMES ({(diffs.Count < Beeps ? "falten xiulets" : $"més de {ToleranceMs:0} ms de diferència")}).");
        if (loopback)
            Console.WriteLine("Nota: sense cable no hi compta el convertidor de la targeta (normalment 1 o 2 ms). Per mesurar-ho tot: --entrada.");
        return ok ? 0 : 1;
    }

    /// <summary>La carpeta indicada, o «dades» a la carpeta actual o a alguna de les de sobre (com el repositori).</summary>
    private static string? FindRuntimeFile(string? dataDir)
    {
        if (dataDir is not null)
        {
            string file = Path.Combine(Path.GetFullPath(dataDir), "engine.json");
            return File.Exists(file) ? file : null;
        }
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string file = Path.Combine(dir.FullName, "dades", "engine.json");
                if (File.Exists(file)) return file;
            }
        }
        return null;
    }

    /// <summary>WAV de 48 kHz, estèreo: 1 s de silenci i 5 xiulets de 1.000 Hz i 100 ms, un cada 2 s. Comencen secs.</summary>
    private static void WriteBeeps(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int total = (int)(FileSeconds * Rate);
        var samples = new float[total * 2];
        for (int k = 0; k < Beeps; k++)
        {
            int start = (int)Math.Round((FirstBeepSeconds + k * BeepEverySeconds) * Rate);
            int length = (int)(BeepSeconds * Rate);
            for (int i = 0; i < length; i++)
            {
                float v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / Rate);
                samples[(start + i) * 2] = v;
                samples[(start + i) * 2 + 1] = v;
            }
        }
        using var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// <summary>
    /// Enregistrament amb l'hora de cada paquet (la que dona Windows, en unitats de 100 ns del mateix rellotge que
    /// <see cref="Stopwatch"/>). Es queda només el primer canal, en una memòria reservada d'entrada.
    /// </summary>
    private sealed class Capture : IDisposable
    {
        private readonly MMDevice _device;
        private readonly WasapiRecorder _recorder;
        private readonly int _rate;
        private readonly int _channels;
        private readonly float[] _samples;
        private readonly int[] _packetStart = new int[100_000];
        private readonly long[] _packetQpc = new long[100_000];
        private int _count;
        private int _packets;

        // Per passar del rellotge de Windows (QPC) a l'hora del rellotge: una parella presa alhora
        private readonly long _anchorTicks = Stopwatch.GetTimestamp();
        private readonly DateTimeOffset _anchorTime = DateTimeOffset.Now;

        public bool Overflowed { get; private set; }

        private Capture(MMDevice device, bool loopback, double seconds)
        {
            _device = device;
            WaveFormat mix;
            using (var client = device.CreateAudioClient()) mix = client.MixFormat;
            _rate = mix.SampleRate;
            _channels = mix.Channels;
            var format = _channels > 2 && mix.BitsPerSample == 32 ? mix : WaveFormat.CreateIeeeFloatWaveFormat(_rate, _channels);

            var builder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithFormat(format);
            if (loopback) builder = builder.WithLoopbackCapture();
            _recorder = builder.Build();
            _recorder.DataAvailable += OnData;
            _samples = new float[(int)(seconds * _rate)];
        }

        public static Capture Open(string deviceId, bool loopback, double seconds)
        {
            using var enumerator = new MMDeviceEnumerator();
            return new Capture(enumerator.GetDevice(deviceId), loopback, seconds);
        }

        public void Start() => _recorder.StartRecording();
        public void Stop() => _recorder.StopRecording();

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        {
            var data = MemoryMarshal.Cast<byte, float>(buffer);
            int frames = data.Length / _channels;
            if (_packets >= _packetStart.Length || _count + frames > _samples.Length)
            {
                Overflowed = true;
                return;
            }
            _packetStart[_packets] = _count;
            _packetQpc[_packets] = qpcPosition;
            _packets++;
            bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
            for (int i = 0; i < frames; i++) _samples[_count + i] = silent ? 0 : data[i * _channels];
            _count += frames;
        }

        /// <summary>L'hora del rellotge en què es va capturar la mostra <paramref name="index"/>.</summary>
        private DateTimeOffset TimeOf(int index)
        {
            int p = 0;
            while (p + 1 < _packets && _packetStart[p + 1] <= index) p++;
            double seconds = _packetQpc[p] * 1e-7 + (double)(index - _packetStart[p]) / _rate;
            double anchor = (double)_anchorTicks / Stopwatch.Frequency;
            return _anchorTime + TimeSpan.FromSeconds(seconds - anchor);
        }

        /// <summary>
        /// La primera mostra entre <paramref name="from"/> i <paramref name="to"/> que passa del llindar després
        /// d'almenys 200 ms de silenci, o null.
        /// </summary>
        public DateTimeOffset? FindOnset(DateTimeOffset from, DateTimeOffset to, float threshold)
        {
            int quiet = 0;
            for (int i = 0; i < _count; i++)
            {
                float a = Math.Abs(_samples[i]);
                if (a > threshold && quiet >= _rate / 5)
                {
                    var t = TimeOf(i);
                    if (t >= from && t <= to) return t;
                }
                quiet = a < threshold / 4 ? quiet + 1 : 0;
            }
            return null;
        }

        public void Dispose()
        {
            _recorder.Dispose();
            _device.Dispose();
        }
    }

    /// <summary>Client mínim del protocol del motor (docs/protocol.md): ordres amb id i esdeveniments.</summary>
    private sealed class EngineClient : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly System.Threading.Channels.Channel<JsonObject> _inbox = System.Threading.Channels.Channel.CreateUnbounded<JsonObject>();
        private int _nextId;

        public async Task ConnectAsync(int port, string token)
        {
            await _socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?token={Uri.EscapeDataString(token)}"), CancellationToken.None);
            _ = Task.Run(ReceiveLoop);
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;
                    if (JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())) is JsonObject obj) _inbox.Writer.TryWrite(obj);
                    message.SetLength(0);
                }
            }
            catch (WebSocketException) { }
            _inbox.Writer.TryComplete();
        }

        /// <summary>Envia una ordre i n'espera la resposta. Si el motor diu que no, llança una excepció amb el seu missatge.</summary>
        public async Task<JsonObject> CommandAsync(string cmd, JsonObject? args = null)
        {
            int id = ++_nextId;
            var msg = new JsonObject { ["v"] = 1, ["id"] = id, ["cmd"] = cmd };
            if (args is not null)
                foreach (var (key, value) in args.ToList()) { args.Remove(key); msg[key] = value; }
            await _socket.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

            var reply = await WaitForEventAsync(m => m["id"]?.GetValue<int>() == id, TimeSpan.FromSeconds(10))
                ?? throw new InvalidOperationException($"El motor no ha respost a {cmd}.");
            if (reply["ok"]?.GetValue<bool>() != true)
                throw new InvalidOperationException($"El motor ha dit que no a {cmd}: {reply["error"]?.ToJsonString()}");
            return reply["result"] as JsonObject ?? new JsonObject();
        }

        /// <summary>Espera un missatge que compleixi la condició (els altres es descarten), o null si passa el temps.</summary>
        public async Task<JsonObject?> WaitForEventAsync(Func<JsonObject, bool> match, TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            try
            {
                while (true)
                {
                    var msg = await _inbox.Reader.ReadAsync(cancel.Token);
                    if (match(msg)) return msg;
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (System.Threading.Channels.ChannelClosedException) { return null; }
        }

        public void Dispose() => _socket.Dispose();
    }
}
