using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Engine.Streaming;

namespace OnaBages24.Engine.Protocol;

// Emissió per internet (fase 5, pas 13a; D-202): la configuració dels emissors (amb les contrasenyes xifrades) i la prova
// de connexió amb un servidor.
public sealed partial class CommandRouter
{
    /// <summary>
    /// Prova la connexió amb un servidor (<c>streaming.test</c>): es connecta, s'identifica i es desconnecta. Retorna null si
    /// ha anat bé o l'error per a l'operador. Les proves en posen una de falsa.
    /// </summary>
    public Func<StreamServer, StreamFormat, StreamInfo, string?> TestStreamServer { get; set; } = (server, format, info) =>
    {
        try
        {
            using var connection = StreamConnection.Open(server, format, info);
            return null;
        }
        catch (StreamException ex) { return ex.Message; }
    };

    /// <summary>Es crida quan canvien els emissors desats (perquè el motor els torni a llegir; pas 13a-2).</summary>
    public event Action? StreamOutputsChanged;

    /// <summary>Els emissors en marxa (per dir-ne l'estat a <c>streaming.list</c>). Null a les proves que no en necessiten.</summary>
    public StreamOutputs? Streams { get; set; }

    /// <summary>L'estat dels emissors (<c>streaming.list</c> i l'esdeveniment <c>streaming.changed</c>).</summary>
    public static JsonArray StreamStatusJson(IEnumerable<StreamOutputStatus> status) =>
        new(status.Select(s => (JsonNode?)StreamStatusJson(s)).ToArray());

    private static JsonObject StreamStatusJson(StreamOutputStatus s) => new()
    {
        ["id"] = s.Id,
        ["name"] = s.Name,
        ["state"] = s.State switch
        {
            StreamOutputState.Off => "off",
            StreamOutputState.Incomplete => "incomplete",
            StreamOutputState.Connecting => "connecting",
            StreamOutputState.Streaming => "streaming",
            StreamOutputState.Reconnecting => "reconnecting",
            _ => "failed",
        },
        ["error"] = s.Error,
        ["connectedAt"] = s.ConnectedAt?.ToString("O"),
        ["downSince"] = s.DownSince?.ToString("O"),
        ["reconnects"] = s.Reconnects,
        ["title"] = s.Title,
        ["inputActive"] = s.InputActive,
        ["bytesDropped"] = s.BytesDropped,
        ["source"] = s.Source switch { SourceMode.Input => "input", SourceMode.Mix => "mix", _ => null },
        ["mixSince"] = s.MixSince?.ToString("O"),
        ["mixReason"] = s.MixReason,
    };

    /// <summary><c>streaming.list</c>: els emissors desats (sense contrasenyes) i les qualitats que es poden triar.</summary>
    private JsonObject StreamingList()
    {
        var saved = StreamOutputStore.Read(LibraryOrError());
        var status = Streams?.Status ?? [];
        return new JsonObject
        {
            ["outputs"] = new JsonArray(saved.Select(s =>
            {
                var json = StreamOutputJson(s);
                // L'estat d'ara (si un emissor s'acaba de desar, encara no en té: al cap de mig segon, streaming.changed)
                if (status.FirstOrDefault(x => x.Id == s.Output.Id) is { } st) json["status"] = StreamStatusJson(st);
                return (JsonNode?)json;
            }).ToArray()),
            ["bitrates"] = new JsonArray(Streamer.Bitrates.Select(b => (JsonNode?)b).ToArray()),
            ["sampleRates"] = new JsonArray(Streamer.SampleRates.Select(r => (JsonNode?)r).ToArray()),
        };
    }

    private static JsonObject StreamOutputJson(SavedStreamOutput saved)
    {
        var o = saved.Output;
        bool unreadable = saved.PasswordUnreadable;
        return new JsonObject
        {
            ["id"] = o.Id,
            ["name"] = o.Name,
            ["enabled"] = o.Enabled,
            ["continuous"] = o.Continuous,
            ["serverType"] = o.ServerType == StreamServerType.Shoutcast ? "shoutcast" : "icecast",
            ["host"] = o.Host,
            ["port"] = o.Port,
            ["user"] = o.User,
            ["mount"] = o.Mount,
            ["streamId"] = o.StreamId,
            ["bitrateKbps"] = o.BitrateKbps,
            ["sampleRate"] = o.SampleRate,
            ["mono"] = o.Mono,
            ["inputDeviceId"] = o.InputDeviceId,
            ["inputDeviceName"] = o.InputDeviceName,
            ["fallbackToMix"] = o.FallbackToMix,
            ["silenceSeconds"] = o.SilenceSeconds,
            ["returnSeconds"] = o.ReturnSeconds,
            ["titleMode"] = o.TitleMode switch { StreamTitleMode.Fixed => "fixed", StreamTitleMode.None => "none", _ => "emission" },
            ["fixedTitle"] = o.FixedTitle,
            ["publicName"] = o.PublicName,
            ["genre"] = o.Genre,
            ["url"] = o.Url,
            ["hasPassword"] = saved.ProtectedPassword is not null,
            // Una còpia d'un altre ordinador: la contrasenya no es pot llegir i s'ha de tornar a escriure
            ["problem"] = unreadable
                ? "No es pot llegir la contrasenya desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure."
                : o.Enabled ? o.Missing(saved.ProtectedPassword is not null) : null,
        };
    }

    /// <summary>
    /// L'emissor <paramref name="current"/> amb els camps de <paramref name="msg"/> que s'hi envien (els altres no canvien).
    /// Comprova el tipus de cada camp; si el conjunt té sentit ho diu <see cref="StreamOutput.Missing"/>.
    /// </summary>
    private static StreamOutput MergeStreamOutput(StreamOutput current, JsonObject msg)
    {
        string Text(string field, string value)
        {
            var node = msg[field];
            if (node is null) return value;
            return ReadString(node)?.Trim() ?? throw new BadParamsException($"El camp '{field}' ha de ser un text.");
        }
        string? OptionalText(string field, string? value)
        {
            if (!msg.ContainsKey(field)) return value;
            var node = msg[field];
            if (node is null) return null;
            string text = ReadString(node)?.Trim() ?? throw new BadParamsException($"El camp '{field}' ha de ser un text.");
            return text.Length > 0 ? text : null;
        }
        bool Flag(string field, bool value)
        {
            var node = msg[field];
            if (node is null) return value;
            return node is JsonValue v && v.TryGetValue(out bool b) ? b : throw new BadParamsException($"El camp '{field}' ha de ser true o false.");
        }
        int Number(string field, int value)
        {
            var node = msg[field];
            if (node is null) return value;
            return node is JsonValue v && v.TryGetValue(out int n) ? n : throw new BadParamsException($"El camp '{field}' ha de ser un nombre enter.");
        }

        var o = current with
        {
            Name = Text("name", current.Name),
            Enabled = Flag("enabled", current.Enabled),
            Continuous = Flag("continuous", current.Continuous),
            Host = StreamOutput.CleanHost(Text("host", current.Host)),
            Port = Number("port", current.Port),
            User = Text("user", current.User),
            Mount = Text("mount", current.Mount),
            StreamId = Number("streamId", current.StreamId),
            BitrateKbps = Number("bitrateKbps", current.BitrateKbps),
            SampleRate = Number("sampleRate", current.SampleRate),
            Mono = Flag("mono", current.Mono),
            InputDeviceId = OptionalText("inputDeviceId", current.InputDeviceId),
            InputDeviceName = OptionalText("inputDeviceName", current.InputDeviceName),
            FallbackToMix = Flag("fallbackToMix", current.FallbackToMix),
            SilenceSeconds = Number("silenceSeconds", current.SilenceSeconds),
            ReturnSeconds = Number("returnSeconds", current.ReturnSeconds),
            FixedTitle = StreamOutput.OneLine(Text("fixedTitle", current.FixedTitle)),
            PublicName = Text("publicName", current.PublicName),
            Genre = Text("genre", current.Genre),
            Url = Text("url", current.Url),
        };
        if (o.Mount.Length > 0 && !o.Mount.StartsWith('/')) o = o with { Mount = "/" + o.Mount };
        if (o.IsMixSource) o = o with { InputDeviceName = StreamOutput.MixInputName }; // el nom, sempre el mateix
        if (msg["serverType"] is { } typeNode)
            o = o with
            {
                ServerType = ReadString(typeNode) switch
                {
                    "icecast" => StreamServerType.Icecast,
                    "shoutcast" => StreamServerType.Shoutcast,
                    _ => throw new BadParamsException("El camp 'serverType' ha de ser 'icecast' o 'shoutcast'."),
                },
            };
        if (msg["titleMode"] is { } modeNode)
            o = o with
            {
                TitleMode = ReadString(modeNode) switch
                {
                    "emission" => StreamTitleMode.Emission,
                    "fixed" => StreamTitleMode.Fixed,
                    "none" => StreamTitleMode.None,
                    _ => throw new BadParamsException("El camp 'titleMode' ha de ser 'emission', 'fixed' o 'none'."),
                },
            };
        if (o.Name.Length == 0) throw new BadParamsException("Falta el nom de l'emissor.");
        if (o.FixedTitle.Length > StreamOutput.MaxTitleLength)
            throw new BadParamsException($"El títol fix pot tenir com a molt {StreamOutput.MaxTitleLength} caràcters.");
        return o;
    }

    /// <summary>
    /// <c>streaming.save</c>: crea un emissor (sense <c>outputId</c>) o en canvia un (els camps que no s'envien no canvien).
    /// <c>password</c>: la nova (buida = cap); si no s'envia, es conserva. Un emissor actiu ha d'estar complet.
    /// </summary>
    private JsonObject StreamingSave(JsonObject msg)
    {
        var library = LibraryOrError();
        var all = StreamOutputStore.Read(library).ToList();
        string? id = ReadString(msg["outputId"]);
        int index = id is null ? -1 : all.FindIndex(s => s.Output.Id == id);
        if (id is not null && index < 0) throw new BadParamsException($"No existeix l'emissor «{id}».");

        var current = index >= 0 ? all[index] : new SavedStreamOutput(new StreamOutput { Id = StreamOutputStore.NextId(all.Select(s => s.Output)) }, null);
        var output = MergeStreamOutput(current.Output, msg);
        if (all.Any(s => s.Output.Id != output.Id && string.Equals(s.Output.Name, output.Name, StringComparison.OrdinalIgnoreCase)))
            throw new BadParamsException($"Ja hi ha un emissor que es diu «{output.Name}».");

        string? secret = current.ProtectedPassword;
        if (msg["password"] is { } passwordNode)
        {
            string password = ReadString(passwordNode) ?? throw new BadParamsException("El camp 'password' ha de ser un text.");
            secret = password.Length > 0 ? StreamOutputStore.Protect(password) : null;
        }
        // Actiu: ha d'estar complet (i la contrasenya s'ha de poder llegir). Inactiu: es pot desar a mitges
        if (output.Enabled)
        {
            if (secret is not null && StreamOutputStore.Unprotect(secret) is null)
                throw new BadParamsException("No es pot llegir la contrasenya desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure.");
            if (output.Missing(secret is not null) is { } missing) throw new BadParamsException(missing);
        }
        else if (output.ServerProblem() is { } problem && output.Host.Length > 0) throw new BadParamsException(problem);

        var saved = new SavedStreamOutput(output, secret);
        if (index >= 0) all[index] = saved;
        else all.Add(saved);
        StreamOutputStore.Write(library, all);
        StreamOutputsChanged?.Invoke();
        return new JsonObject { ["output"] = StreamOutputJson(saved) };
    }

    /// <summary><c>streaming.delete</c>: esborra un emissor (si emetia, s'atura).</summary>
    private JsonObject StreamingDelete(JsonObject msg)
    {
        var library = LibraryOrError();
        string id = ReadString(msg["outputId"]) ?? throw new BadParamsException("Falta 'outputId' (l'emissor).");
        var all = StreamOutputStore.Read(library).ToList();
        if (all.RemoveAll(s => s.Output.Id == id) == 0) throw new BadParamsException($"No existeix l'emissor «{id}».");
        StreamOutputStore.Write(library, all);
        StreamOutputsChanged?.Invoke();
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>
    /// <c>streaming.test</c>: prova la connexió amb el servidor amb les dades desades de l'emissor <c>outputId</c> (si n'hi ha) i
    /// els camps de <paramref name="msg"/> (el que hi ha escrit al formulari, encara que no s'hagi desat). Espera el
    /// servidor (com a molt uns 10 s). No envia so.
    /// </summary>
    private JsonObject StreamingTest(JsonObject msg)
    {
        var library = LibraryOrError();
        string? id = ReadString(msg["outputId"]);
        var current = id is null ? null : StreamOutputStore.Read(library).FirstOrDefault(s => s.Output.Id == id)
            ?? throw new BadParamsException($"No existeix l'emissor «{id}».");
        var output = MergeStreamOutput(current?.Output ?? new StreamOutput { Name = "Prova" }, msg);
        if (output.ServerProblem() is { } problem) throw new BadParamsException(problem);

        string? password;
        if (msg["password"] is { } passwordNode)
            password = ReadString(passwordNode) ?? throw new BadParamsException("El camp 'password' ha de ser un text.");
        else if (current?.ProtectedPassword is null) password = null;
        else password = current.Password
            ?? throw new BadParamsException("No es pot llegir la contrasenya desada (potser és d'una còpia d'un altre ordinador): torna-la a escriure.");
        if (string.IsNullOrEmpty(password)) throw new BadParamsException("Falta la contrasenya del servidor.");

        string? error = TestStreamServer(output.ToServer(password), output.ToFormat(), output.ToInfo());
        return new JsonObject { ["connected"] = error is null, ["error"] = error };
    }
}
