using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OnaBages.Audio;

/// <summary>Error en connectar-se a un stream o en llegir-lo. El missatge és per a l'operador.</summary>
public sealed class RadioStreamException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>El que diu el servidor en connectar-s'hi.</summary>
/// <param name="Url">L'adreça final (després de les redireccions).</param>
/// <param name="ContentType">El tipus del contingut (p. ex. <c>audio/mpeg</c>), o null si no el diu.</param>
/// <param name="Name">El nom de l'emissora (<c>icy-name</c>), o null.</param>
/// <param name="BitrateKbps">El bitrate que anuncia (<c>icy-br</c>), o null.</param>
/// <param name="MetaInterval">Cada quants bytes d'àudio hi ha un bloc de títol (<c>icy-metaint</c>), o 0 si no n'envia.</param>
/// <param name="Server">El programa del servidor (p. ex. «Icecast 2.4.4» o «SHOUTcast»), o null.</param>
public sealed record RadioStreamInfo(Uri Url, string? ContentType, string? Name, int? BitrateKbps, int MetaInterval, string? Server)
{
    /// <summary>Si el contingut és AAC (encara no es pot fer sonar, D-175).</summary>
    public bool IsAac => ContentType is { } t && t.Contains("aac", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Connexió a un stream de ràdio per internet (Shoutcast, Icecast) per escoltar-lo (fase 2, pas 7a; D-175). Es fa amb una
/// connexió TCP pròpia i no amb <see cref="System.Net.Http.HttpClient"/>, perquè els Shoutcast antics responen
/// «ICY 200 OK», que no és HTTP i el client de .NET rebutja. Es demana amb HTTP/1.0 (sense trossos, «chunked»), es
/// segueixen les redireccions (fins a <see cref="MaxRedirects"/>) i s'accepta <c>https</c>.
/// </summary>
public static class RadioStreamConnection
{
    public const int MaxRedirects = 3;
    /// <summary>Com es presenta als servidors: un nom genèric, sense dades de l'emissora.</summary>
    public const string UserAgent = "OnaBages24h/1.0";

    /// <summary>
    /// Es connecta i llegeix la capçalera. Retorna el flux de l'àudio (sense els blocs de títol: <see cref="IcyMetadataStream"/>
    /// si n'hi ha) i el que diu el servidor. Qui el crida l'ha de tancar.
    /// </summary>
    /// <param name="timeout">Temps màxim per connectar-se i rebre la capçalera; també, després, per a cada lectura.</param>
    /// <exception cref="RadioStreamException">Si no es pot connectar, el servidor respon amb un error o no és un stream.</exception>
    public static (Stream Audio, RadioStreamInfo Info) Open(Uri url, TimeSpan timeout, Action<string>? onTitle = null, CancellationToken cancel = default)
    {
        for (int redirect = 0; ; redirect++)
        {
            if (url.Scheme is not ("http" or "https")) throw new RadioStreamException($"L'adreça ha de començar per http:// o https:// ({url}).");
            var (stream, status, headers) = Request(url, timeout, cancel);
            try
            {
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirect >= MaxRedirects) throw new RadioStreamException($"El servidor redirigeix massa vegades ({url}).");
                    if (!headers.TryGetValue("location", out var location) || !Uri.TryCreate(url, location, out var next))
                        throw new RadioStreamException($"El servidor redirigeix sense dir on ({url}).");
                    stream.Dispose();
                    url = next;
                    continue;
                }
                if (status != 200) throw new RadioStreamException(StatusText(status, url, headers));

                int metaInt = headers.TryGetValue("icy-metaint", out var mi) && int.TryParse(mi, out int m) && m > 0 ? m : 0;
                int? bitrate = headers.TryGetValue("icy-br", out var br) && int.TryParse(br.Split(',')[0], out int b) && b > 0 ? b : null;
                var info = new RadioStreamInfo(url, headers.GetValueOrDefault("content-type"), Blank(headers.GetValueOrDefault("icy-name")),
                    bitrate, metaInt, Blank(headers.GetValueOrDefault("server")));
                if (info.ContentType is { } type && type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    throw new RadioStreamException($"L'adreça no és un stream d'àudio: el servidor hi té una pàgina web ({type}). Comprova l'adreça (a vegades cal afegir-hi «/;» o el nom del punt de muntatge).");
                Stream audio = metaInt > 0 ? new IcyMetadataStream(stream, metaInt, onTitle) : stream;
                return (audio, info);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>Envia la petició i llegeix la línia d'estat i les capçaleres (en minúscules).</summary>
    private static (Stream Stream, int Status, Dictionary<string, string> Headers) Request(Uri url, TimeSpan timeout, CancellationToken cancel)
    {
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = (int)timeout.TotalMilliseconds, SendTimeout = (int)timeout.TotalMilliseconds };
        Stream stream;
        try
        {
            using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                connecting.CancelAfter(timeout);
                try
                {
                    client.ConnectAsync(url.Host, url.Port, connecting.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    throw new RadioStreamException($"El servidor no respon ({url.Host}:{url.Port}): ha passat el temps d'espera.");
                }
            }
            stream = client.GetStream();
            if (url.Scheme == "https")
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                ssl.AuthenticateAsClient(url.Host);
                stream = ssl;
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            client.Dispose();
            throw new RadioStreamException($"No s'ha pogut connectar a {url.Host}:{url.Port}: {Describe(ex)}.", ex);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        try
        {
            string path = string.IsNullOrEmpty(url.PathAndQuery) ? "/" : url.PathAndQuery;
            string request = $"GET {path} HTTP/1.0\r\nHost: {url.Authority}\r\nUser-Agent: {UserAgent}\r\nAccept: */*\r\nIcy-MetaData: 1\r\nConnection: close\r\n\r\n";
            stream.Write(Encoding.ASCII.GetBytes(request));
            stream.Flush();

            string statusLine = ReadLine(stream) ?? throw new RadioStreamException($"El servidor ha tancat la connexió sense respondre ({url}).");
            var parts = statusLine.Split(' ', 3);
            if (parts.Length < 2 || !(parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) || parts[0] == "ICY") || !int.TryParse(parts[1], out int status))
                throw new RadioStreamException($"El servidor no ha respost com un stream ({Shorten(statusLine)}).");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 100; i++)
            {
                string? line = ReadLine(stream);
                if (string.IsNullOrEmpty(line)) break;
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return (new OwnedStream(stream, client), status, headers);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            stream.Dispose();
            client.Dispose();
            throw new RadioStreamException($"El servidor no ha respost ({url}): {Describe(ex)}.", ex);
        }
        catch
        {
            stream.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <summary>Una línia de la capçalera (fins a \n, sense \r), o null si s'ha tancat. Byte a byte: no es llegeix àudio de més.</summary>
    private static string? ReadLine(Stream stream)
    {
        var bytes = new List<byte>(128);
        while (bytes.Count < 8192)
        {
            int b = stream.ReadByte();
            if (b < 0) return bytes.Count == 0 ? null : Text(bytes);
            if (b == '\n') return Text(bytes).TrimEnd('\r');
            bytes.Add((byte)b);
        }
        throw new RadioStreamException("El servidor ha enviat una capçalera massa llarga.");

        // UTF-8 si ho és (p. ex. «icy-name: Ràdio…»); si no, Latin-1 (servidors antics)
        static string Text(List<byte> b)
        {
            try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b.ToArray()); }
            catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b.ToArray()); }
        }
    }

    private static string StatusText(int status, Uri url, Dictionary<string, string> headers) => status switch
    {
        // Shoutcast (DNAS 2) respon 401 quan no hi ha cap emissió connectada, amb una nota «…currently unavailable»
        401 when headers.Any(h => h.Key.StartsWith("icy-notice", StringComparison.OrdinalIgnoreCase)
                                  && h.Value.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
            => $"El stream no emet ara: el servidor no té cap emissió connectada (error 401, {url}).",
        400 => $"El servidor no ha entès la petició (error 400, {url}).",
        401 or 403 => $"El servidor no deixa escoltar aquest stream (error {status}, {url}).",
        404 => $"Aquest stream no existeix al servidor (error 404, {url}). Comprova l'adreça o el punt de muntatge.",
        503 => $"El servidor no té lloc per a més oients o el stream no emet ara (error 503, {url}).",
        _ => $"El servidor ha respost amb l'error {status} ({url}).",
    };

    private static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "no es troba el servidor (comprova l'adreça o la connexió a internet)",
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "el servidor ha rebutjat la connexió (no emet o el port no és correcte)",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "el servidor no respon",
        IOException { InnerException: SocketException inner } => Describe(inner),
        System.Security.Authentication.AuthenticationException => "la connexió segura (https) ha fallat",
        _ => ex.Message,
    };

    private static string Shorten(string text) => text.Length > 60 ? text[..60] + "…" : text;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>El flux de la connexió, que també tanca el client TCP.</summary>
    private sealed class OwnedStream(Stream inner, TcpClient client) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Treu els blocs de títol que Shoutcast i Icecast posen entremig de l'àudio (cada <c>icy-metaint</c> bytes: un byte amb la
/// mida en blocs de 16 i el text, p. ex. <c>StreamTitle='Artista - Títol';</c>). Avisa quan canvia el títol.
/// </summary>
public sealed class IcyMetadataStream(Stream inner, int metaInterval, Action<string>? onTitle) : Stream
{
    private readonly int _interval = metaInterval;
    private int _untilMeta = metaInterval;
    private string? _lastTitle;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        if (_untilMeta == 0)
        {
            ReadMetadata();
            _untilMeta = _interval;
        }
        int read = inner.Read(buffer[..Math.Min(buffer.Length, _untilMeta)]);
        _untilMeta -= read;
        return read;
    }

    private void ReadMetadata()
    {
        int length = inner.ReadByte();
        if (length <= 0) return; // 0 = el títol no ha canviat; -1 = s'ha tancat (ho veurà la lectura següent)
        var block = new byte[length * 16];
        int got = 0;
        while (got < block.Length)
        {
            int n = inner.Read(block, got, block.Length - got);
            if (n == 0) return;
            got += n;
        }
        if (ParseTitle(block) is { } title && title != _lastTitle)
        {
            _lastTitle = title;
            onTitle?.Invoke(title);
        }
    }

    /// <summary>El títol d'un bloc (<c>StreamTitle='…';</c>), o null. Primer UTF-8; si no ho és, Latin-1 (servidors antics).</summary>
    public static string? ParseTitle(ReadOnlySpan<byte> block)
    {
        int end = block.IndexOf((byte)0);
        var bytes = end >= 0 ? block[..end] : block;
        string text;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        const string key = "StreamTitle='";
        int start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        int stop = text.IndexOf("';", start, StringComparison.Ordinal);
        string title = (stop >= 0 ? text[start..stop] : text[start..].TrimEnd('\'', ';')).Trim();
        return title.Length == 0 ? null : title;
    }
}
