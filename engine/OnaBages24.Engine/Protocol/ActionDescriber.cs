using System.Text.Json;
using OnaBages24.Data;

namespace OnaBages24.Engine.Protocol;

/// <summary>Com es mostra una acció d'un esdeveniment a la interfície (fase 2, pas 8a).</summary>
/// <param name="Label">Què és: el títol de la pista, «Rotació OnaBages»…</param>
/// <param name="Detail">Una línia més petita: tipus de pista i durada, reproductor auxiliar…</param>
/// <param name="Missing">La pista no existeix a la biblioteca o el fitxer falta: sonarà la següent (o res, si és un auxiliar).</param>
/// <param name="DurationMs">Quant sona (de l'inici al final de la pista), si se sap.</param>
public sealed record ActionInfo(string Label, string? Detail, bool Missing, long? DurationMs);

/// <summary>
/// Descriu les accions dels esdeveniments per a la interfície, perquè no hagi de buscar cada pista a la biblioteca
/// (docs/protocol.md, 4.7). Només llegeix (a través de la biblioteca i les rotacions).
/// </summary>
public sealed class ActionDescriber(Library? library, Rotations? rotations)
{
    private IReadOnlyList<Rotation>? _rotations;

    public ActionInfo Describe(EventAction action)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            var p = json.RootElement;
            return action.Type switch
            {
                EventActionType.Track => TrackInfo(Long(p, "songId"), null),
                EventActionType.Auxiliary => TrackInfo(Long(p, "songId"),
                    $"reproductor auxiliar {Long(p, "player") ?? 1}" + (Double(p, "duckingDb") is { } db ? $" · música a {db:0.#} dB" : "")),
                EventActionType.Stream => new ActionInfo(String(p, "url") ?? "Stream sense adreça",
                    "Stream" + (Long(p, "durada_seg") is { } s ? $" · {Duration(s * 1000)}" : " · sense durada") + " · sonarà a partir del pas 7",
                    false, Long(p, "durada_seg") * 1000),
                EventActionType.Rotation => new ActionInfo(RotationName(Long(p, "rotacioId")), "Rotació de l'AutoDJ", false, null),
                EventActionType.Sweepers => new ActionInfo(
                    Long(p, "subcategoriaId") is { } sub ? library?.SubcategoryName(sub) ?? $"Subcategoria {sub}" : "Sense subcategoria",
                    "Sweepers (fase 3)", false, null),
                EventActionType.AutoDj => new ActionInfo(
                    p.TryGetProperty("actiu", out var on) && on.ValueKind == JsonValueKind.False ? "Atura l'AutoDJ" : "Activa l'AutoDJ", null, false, null),
                _ => new ActionInfo(Events.ActionTypeText(action.Type), "encara no fa res", false, null),
            };
        }
        catch (JsonException)
        {
            return new ActionInfo(Events.ActionTypeText(action.Type), "paràmetres il·legibles", true, null);
        }
    }

    private ActionInfo TrackInfo(long? id, string? extra)
    {
        if (id is null) return new ActionInfo("Pista sense indicar", extra, true, null);
        var track = library?.GetTrack(id.Value);
        if (track is null) return new ActionInfo($"Pista {id} (no existeix)", extra, true, null);

        string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.Path) : track.Title;
        string label = string.IsNullOrWhiteSpace(track.Artist) ? title : $"{track.Artist} - {title}";
        long? duration = track.EndMs is { } end ? end - (track.StartMs ?? 0) : track.DurationMs;
        var parts = new List<string> { QueueJson.Type(track.Type) switch
        {
            "music" => "Música", "sweeper" => "Sweeper", "stationId" => "Identificatiu", "promo" => "Promo",
            "commercial" => "Falca", "program" => "Programa", "news" => "Informatiu", "timeAnnouncement" => "Locució de l'hora",
            "timeSignal" => "Senyal horari", "stream" => "Stream", _ => "Altres",
        } };
        if (duration is { } d) parts.Add(Duration(d));
        if (extra is not null) parts.Add(extra);
        bool missing = track.FileState == FileState.Missing;
        if (missing) parts.Add("el fitxer falta");
        return new ActionInfo(label, string.Join(" · ", parts), missing, duration);
    }

    private string RotationName(long? id)
    {
        _rotations ??= rotations?.List() ?? [];
        return id is { } r && _rotations.FirstOrDefault(x => x.Id == r) is { } rotation ? $"Rotació {rotation.Name}" : $"Rotació {id} (no existeix)";
    }

    private static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private static long? Long(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

    private static double? Double(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? String(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
