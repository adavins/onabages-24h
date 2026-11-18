using System.Text.Json;
using OnaBages24.Data;

namespace OnaBages24.Engine.Protocol;

/// <summary>Com es mostra una acció d'un esdeveniment a la interfície (fase 2, pas 8a).</summary>
/// <param name="Label">Què és: el títol de la pista, «Rotació OnaBages»…</param>
/// <param name="Detail">Una línia més petita: tipus de pista i durada, reproductor auxiliar…</param>
/// <param name="Missing">La pista no existeix a la biblioteca o el fitxer falta: sonarà la següent (o res, si és un auxiliar).</param>
/// <param name="DurationMs">Quant sona (de l'inici al final de la pista), si se sap.</param>
/// <param name="ValidFrom">Data d'inici de la pista, si en té (abans no sona; D-110).</param>
/// <param name="ValidUntil">Data de fi de la pista, si en té (després no sona; D-110).</param>
public sealed record ActionInfo(string Label, string? Detail, bool Missing, long? DurationMs, DateOnly? ValidFrom = null, DateOnly? ValidUntil = null);

/// <summary>
/// Descriu les accions dels esdeveniments per a la interfície, perquè no hagi de buscar cada pista a la biblioteca
/// (docs/protocol.md, 4.7). Només llegeix (a través de la biblioteca i les rotacions).
/// </summary>
public sealed class ActionDescriber(Library? library, Rotations? rotations, Campaigns? campaigns = null)
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
                EventActionType.Stream => StreamInfo(p),
                EventActionType.Input => InputInfo(p),
                EventActionType.Rotation => new ActionInfo(RotationName(Long(p, "rotacioId")), "Rotació de l'AutoDJ", false, null),
                EventActionType.Sweepers => new ActionInfo(
                    Long(p, "subcategoriaId") is { } sub ? library?.SubcategoryName(sub) ?? $"Subcategoria {sub}" : "Sense subcategoria",
                    "Categoria de sweepers (trepitjador)", false, null),
                EventActionType.Bag => new ActionInfo(BagLabel(Long(p, "bossaId"), Long(p, "subcategoriaId")), "Canvia la bossa", false, null),
                EventActionType.Campaign => CampaignInfo(Long(p, "campanyaId")),
                EventActionType.Title => new ActionInfo(
                    String(p, "text") is { Length: > 0 } text ? $"Títol per a BUTT «{text}»" : "Títol per a BUTT sense text",
                    Long(p, "durada_seg") is { } seconds ? Duration(seconds * 1000) : "sense durada", false, null),
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

    /// <summary>
    /// Una acció CAMPANYA (fase 4, pas 3c): «Campanya «Ferreteria»», amb les falques i l'estat. Si no existeix o no sona (esborrany,
    /// acabada o sense falques), es marca com a `Missing`: l'editor ho mostra com una pista que no sonarà.
    /// </summary>
    private ActionInfo CampaignInfo(long? id)
    {
        if (id is null) return new ActionInfo("Campanya sense indicar", null, true, null);
        var c = campaigns?.Get(id.Value);
        if (c is null) return new ActionInfo($"Campanya {id} (no existeix)", null, true, null);
        var today = DateOnly.FromDateTime(DateTime.Now);
        string spots = c.Spots.Count == 1 ? "1 falca" : $"{c.Spots.Count} falques";
        static string Day(DateOnly d) => d.ToString("d/M/yyyy", System.Globalization.CultureInfo.InvariantCulture);
        string dates = $"del {Day(c.StartDate)} al {Day(c.EndDate)}";
        var (state, silent) = c.EffectiveState(today) switch
        {
            CampaignState.Draft => ("esborrany: no sona", true),
            CampaignState.Finished => ("acabada: no sona", true),
            _ => (c.Spots.Count == 0 ? "sense falques: no sona" : "activa", c.Spots.Count == 0),
        };
        // Quant ocupa: la falca més llarga (per a les hores previstes del contingut; quina sona ho decideix el pla)
        long? duration = c.Spots.Select(s => library?.GetTrack(s.TrackId))
            .Select(t => t is null ? null : t.EndMs is { } end ? end - (t.StartMs ?? 0) : t.DurationMs)
            .Max();
        return new ActionInfo($"Campanya «{c.Name}»", $"{spots} · {dates} · {state}", silent, duration);
    }

    /// <summary>«Bossa «Generalista A» → Music / Adult 6».</summary>
    private string BagLabel(long? bagId, long? subcategoryId)
    {
        string bag = bagId is { } b ? rotations?.GetBag(b)?.Name is { } name ? $"Bossa «{name}»" : $"Bossa {b} (no existeix)" : "Bossa sense indicar";
        string sub = subcategoryId is { } s ? library?.SubcategoryName(s) ?? $"subcategoria {s} (no existeix)" : "cap subcategoria";
        return $"{bag} → {sub}";
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
        return new ActionInfo(label, string.Join(" · ", parts), missing, duration, track.ValidFrom, track.ValidUntil);
    }

    /// <summary>
    /// Un STREAM: el de la biblioteca (`songId`, D-108; l'adreça és la ruta de la pista) o, als esdeveniments d'abans,
    /// l'adreça escrita (`url`). La durada és la de l'acció (un stream no en té). El detall diu també les opcions (D-178):
    /// «Stream · 1 h 30 min · comença pel directe · s'acaba si arriba en silenci 8 s · retard 3 s · reserva «…»».
    /// </summary>
    private ActionInfo StreamInfo(JsonElement p)
    {
        long? seconds = Long(p, "durada_seg");
        var parts = new List<string> { "Stream", seconds is { } s ? Duration(s * 1000) : "sense durada" };
        parts.Add(Bool(p, "directe") == false ? "amb la ràfega del servidor" : "comença pel directe");
        if (Long(p, "silenci_seg") is { } silence and > 0) parts.Add($"s'acaba si arriba en silenci {silence} s");
        if (Long(p, "retard_seg") is { } delay and > 0) parts.Add($"retard de connexió {delay} s");
        if (Long(p, "fade_ms") is { } fade && fade != Playback.QueueItem.StreamFadeMs) parts.Add($"fos final {fade / 1000.0:0.#} s");
        if (Bool(p, "reconnecta") == true)
            parts.Add(String(p, "tornada") == "canco" ? "es reconnecta si cau (torna en acabar la cançó)" : "es reconnecta si cau (torna de seguida)");
        if (Bool(p, "titol") == true) parts.Add("el seu títol va a BUTT");
        if (Long(p, "reserva_songId") is { } reserveId)
        {
            var reserve = library?.GetTrack(reserveId);
            parts.Add(reserve is null ? $"reserva {reserveId} (no existeix)" : $"reserva «{Scheduling.ValidityWarnings.TrackName(reserve)}»");
        }
        if (Long(p, "songId") is { } id)
        {
            var track = library?.GetTrack(id);
            if (track is null) return new ActionInfo($"Stream {id} (no existeix)", string.Join(" · ", parts), true, seconds * 1000);
            string title = string.IsNullOrWhiteSpace(track.Title) ? track.Path : track.Title;
            string label = string.IsNullOrWhiteSpace(track.Artist) ? title : $"{track.Artist} - {title}";
            parts.Insert(2, track.Path);
            return new ActionInfo(label, string.Join(" · ", parts), false, seconds * 1000);
        }
        return new ActionInfo(String(p, "url") ?? "Stream sense adreça", string.Join(" · ", parts), String(p, "url") is null, seconds * 1000);
    }

    /// <summary>
    /// Una acció ENTRADA (D-185): «Entrada «Línia 2»», amb el detall com el del stream: «Entrada · 1 h 00 min · s'acaba si
    /// arriba en silenci 8 s · es reconnecta si cau (torna de seguida)».
    /// </summary>
    private ActionInfo InputInfo(JsonElement p)
    {
        long? seconds = Long(p, "durada_seg");
        var parts = new List<string> { "Entrada", seconds is { } s ? Duration(s * 1000) : "sense durada" };
        if (Long(p, "silenci_seg") is { } silence and > 0) parts.Add($"s'acaba si arriba en silenci {silence} s");
        if (Long(p, "fade_ms") is { } fade && fade != Playback.QueueItem.StreamFadeMs) parts.Add($"fos final {fade / 1000.0:0.#} s");
        if (Bool(p, "reconnecta") == true)
            parts.Add(String(p, "tornada") == "canco" ? "es reconnecta si cau (torna en acabar la cançó)" : "es reconnecta si cau (torna de seguida)");
        if (Long(p, "reserva_songId") is { } reserveId)
        {
            var reserve = library?.GetTrack(reserveId);
            parts.Add(reserve is null ? $"reserva {reserveId} (no existeix)" : $"reserva «{Scheduling.ValidityWarnings.TrackName(reserve)}»");
        }
        string? name = String(p, "dispositiu");
        return new ActionInfo(name is null ? "Entrada sense triar" : $"Entrada «{name}»", string.Join(" · ", parts), name is null && String(p, "dispositiuId") is null,
            seconds * 1000);
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

    private static bool? Bool(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
