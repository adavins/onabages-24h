using System.Net;
using System.Net.Sockets;
using System.Text;
using OnaBages.Audio;

namespace OnaBages24.Tests;

/// <summary>
/// Escoltar streams de ràdio per internet (fase 2, pas 7a; D-175). Sense internet: un servidor fals local
/// (<see cref="FakeRadio"/>) fa de Shoutcast o d'Icecast i envia un MP3 generat aquí.
/// </summary>
public sealed class RadioStreamTests
{
    private const int Output = 48000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private const int BytesPerSecond = FakeRadio.BytesPerSecond;

    private static bool Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < limit)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static RadioStreamReader Listen(FakeRadio radio, string path = "/directe", TimeSpan? timeout = null) =>
        new(new Uri($"http://127.0.0.1:{radio.Port}{path}"), Output, TimeSpan.FromSeconds(20), timeout ?? TimeSpan.FromSeconds(3));

    [Fact]
    public void Un_Shoutcast_amb_ICY_200_i_titol_se_sent_a_48_kHz()
    {
        var mp3 = FakeRadio.Mp3(3);
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, "ICY 200 OK\r\nicy-name:Ràdio Prova\r\nicy-br:128\r\nicy-metaint:4096\r\ncontent-type:audio/mpeg\r\n\r\n");
            await FakeRadio.SendWithTitles(client, mp3, 4096, "Artista - Cançó amb accents");
        });
        using var reader = Listen(radio);

        Assert.True(Until(() => reader.Received >= TimeSpan.FromSeconds(2.5)), reader.Error);
        Assert.Equal(RadioStreamState.Receiving, reader.State);
        Assert.Equal("Ràdio Prova", reader.Info!.Name);
        Assert.Equal(128, reader.Info.BitrateKbps);
        Assert.Equal("Artista - Cançó amb accents", reader.Title);
        Assert.Equal("MP3 128 kbps, 44.100 Hz, estèreo", reader.Format);
        Assert.InRange(reader.MaxPeakDb, -8, 0); // 0,5 = −6 dB: els títols no s'han barrejat amb l'àudio
        Assert.True(reader.Buffer.Count > Output); // a 48 kHz
        Assert.StartsWith("GET /directe HTTP/1.0", radio.Requests[0]);
        Assert.Contains("Icy-MetaData: 1", radio.Requests[0]);
        Assert.Contains($"User-Agent: {RadioStreamConnection.UserAgent}", radio.Requests[0]);
    }

    [Fact]
    public void Segueix_una_redireccio_i_accepta_HTTP_1_0()
    {
        var mp3 = FakeRadio.Mp3(1);
        using var radio = new FakeRadio(async (client, request) =>
        {
            if (request.StartsWith("GET /vell"))
            {
                await FakeRadio.Send(client, "HTTP/1.1 302 Found\r\nLocation: /nou.mp3\r\n\r\n");
                return;
            }
            await FakeRadio.Send(client, "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\nServer: Icecast 2.4.4\r\n\r\n");
            await client.GetStream().WriteAsync(mp3);
            await Task.Delay(3000);
        });
        using var reader = Listen(radio, "/vell");

        Assert.True(Until(() => reader.Received >= TimeSpan.FromSeconds(0.8)), reader.Error);
        Assert.Equal("/nou.mp3", reader.Info!.Url.AbsolutePath);
        Assert.Equal("Icecast 2.4.4", reader.Info.Server);
        Assert.Null(reader.Title);
    }

    [Theory]
    [InlineData("HTTP/1.0 404 Not Found\r\n\r\n", "no existeix")]
    [InlineData("HTTP/1.0 503 Service Unavailable\r\n\r\n", "no té lloc per a més oients")]
    // Shoutcast DNAS 2 sense cap emissió connectada (resposta real d'«Ona Bages directe», 17-11-2026)
    [InlineData("HTTP/1.0 401 Unauthorized\r\nicy-notice1:<BR>Shoutcast DNAS/posix(linux x64) v2.6.1.777<BR>\r\nicy-notice2:The resource requested is currently unavailable<BR>\r\nConnection:close\r\n\r\n", "no emet ara: el servidor no té cap emissió connectada")]
    [InlineData("HTTP/1.0 401 Unauthorized\r\n\r\n", "no deixa escoltar")]
    [InlineData("HTTP/1.0 200 OK\r\nContent-Type: text/html\r\n\r\n<html>", "pàgina web")]
    [InlineData("HTTP/1.0 200 OK\r\nContent-Type: audio/aacp\r\n\r\n", "AAC")]
    [InlineData("SOURCE OK\r\n\r\n", "no ha respost com un stream")]
    public void Els_errors_del_servidor_es_diuen_en_catala(string response, string expected)
    {
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, response);
            await Task.Delay(1000);
        });
        using var reader = Listen(radio);

        Assert.True(Until(() => reader.State == RadioStreamState.Failed));
        Assert.Contains(expected, reader.Error);
    }

    [Fact]
    public void Si_no_hi_ha_servidor_diu_que_rebutja_la_connexio()
    {
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }
        using var reader = new RadioStreamReader(new Uri($"http://127.0.0.1:{port}/"), Output, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));

        Assert.True(Until(() => reader.State == RadioStreamState.Failed));
        Assert.Contains("ha rebutjat la connexió", reader.Error);
    }

    [Fact]
    public void Si_el_servidor_tanca_el_stream_es_diu()
    {
        var mp3 = FakeRadio.Mp3(1);
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, "ICY 200 OK\r\ncontent-type:audio/mpeg\r\n\r\n");
            await client.GetStream().WriteAsync(mp3);
        });
        using var reader = Listen(radio);

        Assert.True(Until(() => reader.State == RadioStreamState.Failed));
        Assert.Equal("El servidor ha tancat el stream.", reader.Error);
        Assert.True(reader.Received >= TimeSpan.FromSeconds(0.9)); // el que havia arribat, hi és
    }

    [Fact]
    public void Si_deixen_d_arribar_dades_es_dona_per_tallat()
    {
        var mp3 = FakeRadio.Mp3(1);
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, "ICY 200 OK\r\ncontent-type:audio/mpeg\r\n\r\n");
            await client.GetStream().WriteAsync(mp3);
            await Task.Delay(5000); // connectat, però sense enviar res
        });
        using var reader = Listen(radio, timeout: TimeSpan.FromSeconds(1));

        Assert.True(Until(() => reader.State == RadioStreamState.Failed));
        Assert.Contains("s'ha tallat", reader.Error);
    }

    [Fact]
    public void Mesura_la_rafega_inicial_del_servidor()
    {
        // Com un Icecast: en connectar-s'hi, envia de cop els últims 3 s; després, al ritme del directe
        var mp3 = FakeRadio.Mp3(5);
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, "HTTP/1.0 200 OK\r\nContent-Type: audio/mpeg\r\n\r\n");
            var stream = client.GetStream();
            await stream.WriteAsync(mp3.AsMemory(0, 3 * BytesPerSecond));
            for (int at = 3 * BytesPerSecond; at < mp3.Length; at += BytesPerSecond / 10)
            {
                await Task.Delay(100);
                await stream.WriteAsync(mp3.AsMemory(at, Math.Min(BytesPerSecond / 10, mp3.Length - at)));
            }
            await Task.Delay(2000);
        });
        using var reader = Listen(radio);

        Assert.True(Until(() => reader.Received >= TimeSpan.FromSeconds(4.5)), reader.Error);
        Assert.InRange(reader.Burst.TotalSeconds, 2.4, 3.6);
        Assert.InRange(reader.Lead.TotalSeconds, 2.0, 4.0); // després, va sempre uns 3 s avançat
    }

    [Fact]
    public void Mesura_el_silenci()
    {
        var silent = FakeRadio.Mp3(3, level: 0);
        using var radio = new FakeRadio(async (client, request) =>
        {
            await FakeRadio.Send(client, "ICY 200 OK\r\ncontent-type:audio/mpeg\r\n\r\n");
            await client.GetStream().WriteAsync(silent);
            await Task.Delay(3000);
        });
        using var reader = Listen(radio);

        Assert.True(Until(() => reader.Received >= TimeSpan.FromSeconds(2.5)), reader.Error);
        Assert.True(reader.SilentFor >= TimeSpan.FromSeconds(2), $"{reader.SilentFor}");
        Assert.True(reader.MaxPeakDb < RadioStreamReader.DefaultSilenceDb);
    }

    [Fact]
    public void El_titol_es_llegeix_en_UTF_8_i_en_Latin_1()
    {
        Assert.Equal("Cançó", IcyMetadataStream.ParseTitle(Encoding.UTF8.GetBytes("StreamTitle='Cançó';StreamUrl='';\0\0")));
        Assert.Equal("Cançó", IcyMetadataStream.ParseTitle(Encoding.Latin1.GetBytes("StreamTitle='Cançó';")));
        Assert.Null(IcyMetadataStream.ParseTitle(Encoding.UTF8.GetBytes("StreamTitle='';")));
        Assert.Null(IcyMetadataStream.ParseTitle(Encoding.UTF8.GetBytes("StreamUrl='x';")));
    }
}
