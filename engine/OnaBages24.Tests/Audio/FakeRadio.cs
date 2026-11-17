using System.Net;
using System.Net.Sockets;
using System.Text;
using OnaBages.Audio;

namespace OnaBages24.Tests;

/// <summary>
/// Un servidor de ràdio fals a 127.0.0.1: per a cada connexió, llegeix la petició (fins a la línia buida), l'apunta a
/// <see cref="Requests"/> i crida <c>serve</c>. En acabar <c>serve</c>, tanca la connexió.
/// </summary>
internal sealed class FakeRadio : IDisposable
{
    private const int Rate = 44100;

    /// <summary>Bytes per segon d'un MP3 de 128 kbps.</summary>
    public const int BytesPerSecond = 128_000 / 8;

    /// <summary>Un MP3 de 128 kbps, estèreo: un to de 440 Hz (amplitud <paramref name="level"/>; 0 = silenci).</summary>
    public static byte[] Mp3(double seconds, float level = 0.5f)
    {
        using var encoder = new LameEncoder(Rate, 128);
        using var data = new MemoryStream();
        int total = (int)(seconds * Rate);
        var block = new float[Rate / 10 * 2];
        for (int start = 0; start < total; start += block.Length / 2)
        {
            for (int i = 0; i < block.Length / 2; i++)
                block[i * 2] = block[i * 2 + 1] = (float)(level * Math.Sin(2 * Math.PI * 440 * (start + i) / Rate));
            data.Write(encoder.Encode(block, block.Length));
        }
        data.Write(encoder.Flush());
        return data.ToArray();
    }


    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public List<string> Requests { get; } = [];

    public FakeRadio(Func<TcpClient, string, Task> serve)
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            string request = await ReadRequest(client.GetStream());
                            lock (Requests) Requests.Add(request);
                            await serve(client, request);
                        }
                        catch (Exception) { /* el lector ha tancat: és normal */ }
                    }
                });
            }
        });
    }

    public int Port { get; }

    private static async Task<string> ReadRequest(NetworkStream stream)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n") && await stream.ReadAsync(one) > 0) text.Append((char)one[0]);
        return text.ToString();
    }

    public static Task Send(TcpClient client, string text) => client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    /// <summary>L'àudio amb un bloc de títol cada <paramref name="metaInt"/> bytes (el primer, amb el títol; els altres, buits).</summary>
    public static async Task SendWithTitles(TcpClient client, byte[] audio, int metaInt, string title)
    {
        var stream = client.GetStream();
        var meta = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
        int blocks = (meta.Length + 15) / 16;
        var block = new byte[1 + blocks * 16];
        block[0] = (byte)blocks;
        meta.CopyTo(block, 1);
        bool first = true;
        for (int at = 0; at < audio.Length; at += metaInt)
        {
            int n = Math.Min(metaInt, audio.Length - at);
            await stream.WriteAsync(audio.AsMemory(at, n));
            if (n < metaInt) break;
            await stream.WriteAsync(first ? block : new byte[1]);
            first = false;
        }
        await Task.Delay(3000);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
