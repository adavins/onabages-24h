using OnaBages.Audio;

namespace OnaBages24.Engine.Streaming;

/// <summary>Quin títol envia un emissor al servidor (D-202).</summary>
public enum StreamTitleMode
{
    /// <summary>El de l'emissió: el mateix text que <c>NowPlaying.txt</c> (la pista que sona, el títol fix…).</summary>
    Emission,
    /// <summary>Sempre el mateix text (p. ex. el nom del programa).</summary>
    Fixed,
    /// <summary>Cap.</summary>
    None,
}

/// <summary>
/// Un emissor per internet (fase 5, pas 13; D-202): què envia (una entrada de Windows), a quin servidor i amb quina
/// qualitat. Substitueix un BUTT. La contrasenya del servidor no hi és mai: es desa xifrada a part
/// (<see cref="StreamOutputStore"/>).
/// </summary>
public sealed record StreamOutput
{
    /// <summary>Identificador fix («e1», «e2»…): el fan servir els esdeveniments que l'engeguen (pas 13c).</summary>
    public string Id { get; init; } = "";

    /// <summary>Com es diu a la pantalla (p. ex. «Ona Bages» o «Ona Bages Directe»).</summary>
    public string Name { get; init; } = "";

    /// <summary>Si no, no emet mai (es conserva la configuració).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Sempre en marxa (l'emissió de l'emissora). Si no, només quan el programa un esdeveniment o s'engega a mà (pas 13c).</summary>
    public bool Continuous { get; init; } = true;

    // --- Servidor (les mateixes dades que al BUTT) ---
    public StreamServerType ServerType { get; init; } = StreamServerType.Icecast;
    /// <summary>Nom o adreça del servidor, sense «http://».</summary>
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8000;
    /// <summary>Només Icecast: usuari (gairebé sempre «source»).</summary>
    public string User { get; init; } = "source";
    /// <summary>Només Icecast: punt de muntatge, amb la barra (p. ex. «/onabages.mp3»).</summary>
    public string Mount { get; init; } = "/stream";
    /// <summary>Només Shoutcast v2: identificador del stream (sid).</summary>
    public int StreamId { get; init; } = 1;

    // --- Qualitat (MP3; per defecte, la de l'emissora: 192 kbps estèreo 48 kHz) ---
    public int BitrateKbps { get; init; } = 192;
    public int SampleRate { get; init; } = 48000;
    public bool Mono { get; init; }

    // --- Font: una entrada de Windows, per identificador i, si no hi és, pel nom (com la sortida, D-038) ---
    public string? InputDeviceId { get; init; }
    public string? InputDeviceName { get; init; }

    // --- Títols ---
    public StreamTitleMode TitleMode { get; init; } = StreamTitleMode.Emission;
    /// <summary>El text amb <see cref="StreamTitleMode.Fixed"/>.</summary>
    public string FixedTitle { get; init; } = "";

    // --- El que veuen els oients (nom de l'emissora, gènere i web; no s'anuncia als directoris públics) ---
    public string PublicName { get; init; } = "";
    public string Genre { get; init; } = "";
    public string Url { get; init; } = "";

    public const int MaxTitleLength = 200;

    /// <summary>
    /// Què falta o no va bé perquè pugui emetre (null si es pot provar). <paramref name="hasPassword"/>: si té contrasenya
    /// (tots els servidors en demanen).
    /// </summary>
    public string? Missing(bool hasPassword)
    {
        if (ServerProblem() is { } server) return server;
        if (!hasPassword) return "Falta la contrasenya del servidor.";
        if (string.IsNullOrWhiteSpace(InputDeviceId) && string.IsNullOrWhiteSpace(InputDeviceName)) return "Falta triar l'entrada que s'emet.";
        if (TitleMode == StreamTitleMode.Fixed && FixedTitle.Length == 0) return "Falta el títol fix.";
        return null;
    }

    /// <summary>Què no va bé de les dades del servidor i de la qualitat (null si res): el mínim per provar la connexió.</summary>
    public string? ServerProblem()
    {
        if (Host.Length == 0) return "Falta l'adreça del servidor.";
        if (Host.Contains('/') || Host.Contains(' ') || Host.Contains(':')) return $"L'adreça del servidor ha de ser només el nom (p. ex. «servidor.exemple.cat»), sense «http://», port ni camí: «{Host}».";
        if (Port is < 1 or > 65535) return "El port ha de ser un nombre entre 1 i 65535.";
        if (ServerType == StreamServerType.Shoutcast && Port == 65535) return "Amb Shoutcast, l'emissió va al port següent: el port no pot ser 65535.";
        if (ServerType == StreamServerType.Icecast && (Mount.Length < 2 || !Mount.StartsWith('/') || Mount.Contains(' ')))
            return "El punt de muntatge ha de començar per «/» i no pot tenir espais (p. ex. «/onabages.mp3»).";
        if (ServerType == StreamServerType.Icecast && User.Length == 0) return "Falta l'usuari del servidor (gairebé sempre «source»).";
        if (ServerType == StreamServerType.Shoutcast && StreamId < 1) return "L'identificador del stream (sid) ha de ser 1 o més.";
        if (!Streamer.Bitrates.Contains(BitrateKbps)) return $"La qualitat ha de ser una d'aquestes: {string.Join(", ", Streamer.Bitrates)} kbps.";
        if (!Streamer.SampleRates.Contains(SampleRate)) return $"La freqüència ha de ser {string.Join(" o ", Streamer.SampleRates)} Hz.";
        return null;
    }

    /// <summary>Les dades de connexió per a l'emissor, amb la contrasenya.</summary>
    public StreamServer ToServer(string password) => new()
    {
        Type = ServerType,
        Host = Host,
        Port = Port,
        Password = password,
        User = User,
        Mount = Mount,
        StreamId = StreamId,
    };

    public StreamFormat ToFormat() => new(BitrateKbps, SampleRate, Mono ? 1 : 2);

    /// <summary>El que veuen els oients. Sense nom públic, el de l'emissor.</summary>
    public StreamInfo ToInfo() => new()
    {
        Name = PublicName.Length > 0 ? PublicName : Name,
        Genre = Genre,
        Url = Url,
    };

    /// <summary>
    /// L'adreça tal com l'escriu la gent («http://servidor:8000/») → només el nom. El port i el camí no es treuen: si n'hi
    /// ha, <see cref="ServerProblem"/> ho diu (no s'endevina quin port volia).
    /// </summary>
    public static string CleanHost(string host)
    {
        host = host.Trim();
        foreach (var scheme in new[] { "http://", "https://" })
            if (host.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) host = host[scheme.Length..];
        return host.TrimEnd('/');
    }

    /// <summary>El text d'un títol en una sola línia (sense salts ni espais repetits).</summary>
    public static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
