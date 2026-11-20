using System.Text.Json.Nodes;
using OnaBages.Audio;
using OnaBages24.Data;
using OnaBages24.Engine.Security;

namespace OnaBages24.Engine.Streaming;

/// <summary>Un emissor tal com és a la base de dades: la configuració i la contrasenya xifrada (o null si no en té).</summary>
public sealed record SavedStreamOutput(StreamOutput Output, string? ProtectedPassword)
{
    /// <summary>La contrasenya desxifrada, o null (no en té, o és d'una còpia d'un altre ordinador: <see cref="PasswordUnreadable"/>).</summary>
    public string? Password => ProtectedPassword is null ? null : StreamOutputStore.Unprotect(ProtectedPassword);

    /// <summary>En té una però no es pot desxifrar (una còpia d'un altre ordinador o d'un altre usuari de Windows).</summary>
    public bool PasswordUnreadable => ProtectedPassword is not null && Password is null;
}

/// <summary>
/// Els emissors per internet (D-202), desats a la configuració (clau <c>emissors</c>) com una llista JSON. Les
/// contrasenyes, xifrades amb DPAPI (<see cref="Dpapi"/>), com la del correu. És l'únic lloc que els llegeix i els escriu.
/// </summary>
public static class StreamOutputStore
{
    public const string SettingKey = "emissors";
    private const string Purpose = "emissio";

    public static string Protect(string password) => Dpapi.Protect(password, Purpose);
    public static string? Unprotect(string protectedText) => Dpapi.Unprotect(protectedText, Purpose);

    /// <summary>Els emissors desats, en l'ordre en què es van crear. Si no n'hi ha (o el text no s'entén), cap.</summary>
    public static IReadOnlyList<SavedStreamOutput> Read(Library? library)
    {
        var list = new List<SavedStreamOutput>();
        try
        {
            if (library?.GetSetting(SettingKey) is not { } json || JsonNode.Parse(json) is not JsonArray array) return list;
            foreach (var node in array)
            {
                if (node is not JsonObject o || Text(o["id"]) is not { Length: > 0 } id) continue;
                var output = new StreamOutput
                {
                    Id = id,
                    Name = Text(o["nom"]) ?? id,
                    Enabled = Bool(o["actiu"], true),
                    Continuous = Bool(o["continu"], true),
                    ServerType = Text(o["servidor"]) == "shoutcast" ? StreamServerType.Shoutcast : StreamServerType.Icecast,
                    Host = Text(o["adreca"]) ?? "",
                    Port = Int(o["port"], 8000),
                    User = Text(o["usuari"]) ?? "source",
                    Mount = Text(o["muntatge"]) ?? "/stream",
                    StreamId = Int(o["sid"], 1),
                    BitrateKbps = Int(o["kbps"], 192),
                    SampleRate = Int(o["hz"], 48000),
                    Mono = Bool(o["mono"], false),
                    InputDeviceId = Text(o["entrada_id"]),
                    InputDeviceName = Text(o["entrada_nom"]),
                    TitleMode = Text(o["titols"]) switch { "fix" => StreamTitleMode.Fixed, "cap" => StreamTitleMode.None, _ => StreamTitleMode.Emission },
                    FixedTitle = Text(o["titol_fix"]) ?? "",
                    PublicName = Text(o["nom_public"]) ?? "",
                    Genre = Text(o["genere"]) ?? "",
                    Url = Text(o["web"]) ?? "",
                };
                list.Add(new SavedStreamOutput(output, Text(o["contrasenya"]) is { Length: > 0 } secret ? secret : null));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return list;
    }

    /// <summary>Desa tota la llista (substitueix la que hi havia).</summary>
    public static void Write(Library library, IEnumerable<SavedStreamOutput> outputs)
    {
        var array = new JsonArray();
        foreach (var (o, secret) in outputs)
        {
            array.Add(new JsonObject
            {
                ["id"] = o.Id,
                ["nom"] = o.Name,
                ["actiu"] = o.Enabled,
                ["continu"] = o.Continuous,
                ["servidor"] = o.ServerType == StreamServerType.Shoutcast ? "shoutcast" : "icecast",
                ["adreca"] = o.Host,
                ["port"] = o.Port,
                ["usuari"] = o.User,
                ["muntatge"] = o.Mount,
                ["sid"] = o.StreamId,
                ["kbps"] = o.BitrateKbps,
                ["hz"] = o.SampleRate,
                ["mono"] = o.Mono,
                ["entrada_id"] = o.InputDeviceId,
                ["entrada_nom"] = o.InputDeviceName,
                ["titols"] = o.TitleMode switch { StreamTitleMode.Fixed => "fix", StreamTitleMode.None => "cap", _ => "emissio" },
                ["titol_fix"] = o.FixedTitle,
                ["nom_public"] = o.PublicName,
                ["genere"] = o.Genre,
                ["web"] = o.Url,
                ["contrasenya"] = secret,
            });
        }
        library.SetSetting(SettingKey, array.ToJsonString());
    }

    /// <summary>L'identificador d'un emissor nou: «e» i el número següent al més alt.</summary>
    public static string NextId(IEnumerable<StreamOutput> existing)
    {
        int max = 0;
        foreach (var o in existing)
            if (o.Id.StartsWith('e') && int.TryParse(o.Id.AsSpan(1), out int n) && n > max) max = n;
        return $"e{max + 1}";
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    private static bool Bool(JsonNode? node, bool fallback) => node is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
    private static int Int(JsonNode? node, int fallback) => node is JsonValue v && v.TryGetValue(out int i) ? i : fallback;
}
