using System.Text.Json;
using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

// Dades de l'emissora, informe de programació i certificat d'emissió (fase 4, pas 4a; planificador.md 10; D-138).
public sealed partial class CommandRouter
{
    /// <summary>Quan canvien les dades de l'emissora (<c>station.changed</c>).</summary>
    public event Action? StationChanged;

    /// <summary>Clau de la configuració amb les dades de l'emissora: <c>{ "name", "frequency", "logo" }</c>.</summary>
    public const string StationSettingKey = "emissora";

    /// <summary>Mida màxima del logotip (com a adreça <c>data:</c>), perquè la configuració no es faci gran.</summary>
    public const int MaxLogoLength = 1_500_000;

    private static readonly string[] LogoPrefixes = ["data:image/png;base64,", "data:image/jpeg;base64,", "data:image/svg+xml;base64,"];

    // --- Dades de l'emissora ---

    private JsonObject StationGet()
    {
        var library = LibraryOrError();
        JsonObject? saved = null;
        if (library.GetSetting(StationSettingKey) is { } json)
        {
            try { saved = JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { saved = null; }
        }
        return new JsonObject
        {
            ["station"] = new JsonObject
            {
                ["name"] = ReadString(saved?["name"]) ?? "",
                ["frequency"] = ReadString(saved?["frequency"]) ?? "",
                ["logo"] = ReadString(saved?["logo"]),
            },
        };
    }

    /// <summary>
    /// Desa les dades de l'emissora: <c>name</c>, <c>frequency</c> (textos, com a molt 100 caràcters) i <c>logo</c> (una
    /// imatge PNG, JPEG o SVG com a adreça <c>data:</c>, o null per treure-la). Els camps que no s'envien no canvien.
    /// </summary>
    private JsonObject StationSet(JsonObject msg)
    {
        var library = LibraryOrError();
        var current = (JsonObject)StationGet()["station"]!;
        string Text(string field)
        {
            if (!msg.ContainsKey(field)) return ReadString(current[field]) ?? "";
            string value = (ReadString(msg[field]) ?? "").Trim();
            if (value.Length > 100) throw new BadParamsException($"El camp '{field}' pot tenir com a molt 100 caràcters.");
            return value;
        }
        string name = Text("name");
        string frequency = Text("frequency");
        string? logo = ReadString(current["logo"]);
        if (msg.ContainsKey("logo"))
        {
            logo = ReadString(msg["logo"]);
            if (logo is not null)
            {
                if (!LogoPrefixes.Any(p => logo.StartsWith(p, StringComparison.Ordinal)))
                    throw new BadParamsException("El logotip ha de ser una imatge PNG, JPEG o SVG.");
                if (logo.Length > MaxLogoLength)
                    throw new BadParamsException("El logotip és massa gran (com a molt, uns 1.000 kB). Fes-ne una versió més petita.");
            }
        }
        library.SetSetting(StationSettingKey, new JsonObject { ["name"] = name, ["frequency"] = frequency, ["logo"] = logo }.ToJsonString());
        StationChanged?.Invoke();
        return StationGet();
    }

    // --- Informes ---

    /// <summary>L'inici d'un dia local, com a instant.</summary>
    private static DateTimeOffset LocalDayStart(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private static string LocalText(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("O");

    /// <summary>
    /// De qui és l'informe (<c>campaignId</c> o <c>trackId</c>) i el període (<c>from</c>, <c>to</c>: dies locals, tots dos
    /// inclosos; per defecte, les dates de la campanya o de la pista). Com a molt 400 dies.
    /// </summary>
    private (CalendarSubject Subject, Campaign? Campaign, Track? Track, DateOnly From, DateOnly To, JsonObject Json) ReportSubject(JsonObject msg)
    {
        Campaign? campaign = null;
        Track? track = null;
        JsonObject json;
        DateOnly? validFrom, validUntil;
        if (msg["campaignId"] is not null)
        {
            long id = RequiredId(msg, "campaignId");
            campaign = CampaignsOrError().Get(id) ?? throw new BadParamsException($"No existeix la campanya {id}.");
            json = CampaignJson(campaign, Today(), _library);
            json["kind"] = "campaign";
            (validFrom, validUntil) = (campaign.StartDate, campaign.EndDate);
        }
        else
        {
            track = TrackOrError(LibraryOrError(), RequiredId(msg, "trackId"));
            json = QueueJson.Track(track);
            json["validFrom"] = track.ValidFrom?.ToString("yyyy-MM-dd");
            json["validUntil"] = track.ValidUntil?.ToString("yyyy-MM-dd");
            json["kind"] = "track";
            (validFrom, validUntil) = (track.ValidFrom, track.ValidUntil);
        }
        var from = OptionalDate(msg, "from") ?? validFrom
            ?? throw new BadParamsException("Falta 'from': la pista no té data d'inici.");
        var to = OptionalDate(msg, "to") ?? validUntil
            ?? throw new BadParamsException("Falta 'to': la pista no té data de fi.");
        if (to < from) throw new BadParamsException("El període s'acaba abans de començar.");
        if (to.DayNumber - from.DayNumber > 400) throw new BadParamsException("El període pot ser com a molt de 400 dies.");
        var subject = campaign is not null ? CalendarSubject.ForCampaign(campaign.Id) : CalendarSubject.ForTrack(track!.Id);
        return (subject, campaign, track, from, to, json);
    }

    /// <summary>
    /// Informe de programació (fase 4, pas 4a): <c>campaignId</c> o <c>trackId</c>, i <c>from</c>/<c>to</c>. Els passis
    /// previstos (amb la falca de cadascun), quants en té cada falca i la graella setmanal amb les franges programades.
    /// </summary>
    private JsonObject ReportsProgramming(JsonObject msg)
    {
        var library = LibraryOrError();
        var events = EventsOrError().List();
        var (subject, campaign, track, from, to, json) = ReportSubject(msg);
        var start = LocalDayStart(from);
        var end = LocalDayStart(to.AddDays(1));
        var passes = campaign is not null
            ? ScheduleReports.CampaignPasses(campaign, CampaignsOrError(), events, library.GetTrack, start, end, CampaignClock(), TimeZoneInfo.Local)
            : ScheduleReports.TrackPasses(track!, events, from, to, TimeZoneInfo.Local);

        // Les falques (o la pista): quants passis previstos en té cadascuna
        var spotIds = campaign is not null ? campaign.Spots.Select(s => s.TrackId).ToList() : [track!.Id];
        var tracks = spotIds.Distinct().ToDictionary(id => id, id => library.GetTrack(id));
        JsonObject SpotJson(long id, int count)
        {
            var t = tracks.GetValueOrDefault(id) ?? library.GetTrack(id);
            return new JsonObject
            {
                ["trackId"] = id,
                ["artist"] = t?.Artist,
                ["title"] = t?.Title ?? $"Pista {id}",
                ["durationMs"] = t is null ? null : t.EndMs is { } e ? e - (t.StartMs ?? 0) : t.DurationMs,
                ["count"] = count,
            };
        }

        // La graella setmanal: les hores on està programada (només esdeveniments actius)
        var rows = TrackCalendar.Build(events, subject, Today())
            .Select(r => (r.Time, Days: r.Days.Select(c => c is { Marked: true, Event.Enabled: true }).ToArray()))
            .Where(r => r.Days.Any(d => d))
            .ToList();

        return new JsonObject
        {
            ["subject"] = json,
            ["from"] = from.ToString("yyyy-MM-dd"),
            ["to"] = to.ToString("yyyy-MM-dd"),
            ["total"] = passes.Count,
            ["empty"] = passes.Count(p => p.TrackId is null),
            ["bySpot"] = new JsonArray(spotIds.Select(id => (JsonNode)SpotJson(id, passes.Count(p => p.TrackId == id))).ToArray()),
            ["grid"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["time"] = r.Time.ToString("HH:mm:ss"),
                ["days"] = new JsonArray(r.Days.Select(d => (JsonNode)d).ToArray()),
            }).ToArray()),
            ["passes"] = new JsonArray(passes.Select(p => (JsonNode)new JsonObject
            {
                ["at"] = LocalText(p.At),
                ["eventName"] = p.EventName,
                ["trackId"] = p.TrackId,
                ["title"] = p.TrackId is { } id ? (tracks.GetValueOrDefault(id) ?? library.GetTrack(id))?.Title : null,
            }).ToArray()),
        };
    }

    /// <summary>
    /// Certificat d'emissió (fase 4, pas 4a): <c>campaignId</c> o <c>trackId</c>, i <c>from</c>/<c>to</c>. Cada emissió
    /// real, de l'historial (no del pla): hora d'inici, artista, títol, quant va sonar i si va sonar sencera. El nombre de
    /// passis és el de la llista. D'una campanya, les emissions de qualsevol de les seves falques.
    /// </summary>
    private JsonObject ReportsCertificate(JsonObject msg)
    {
        var library = LibraryOrError();
        var (_, campaign, track, from, to, json) = ReportSubject(msg);
        var emissions = library.HistoryFor(track?.Id, campaign?.Id, LocalDayStart(from), LocalDayStart(to.AddDays(1)));
        var bySpot = emissions
            .GroupBy(e => (e.TrackId, e.Artist, e.Title))
            .Select(g => (JsonNode)new JsonObject
            {
                ["trackId"] = g.Key.TrackId,
                ["artist"] = g.Key.Artist,
                ["title"] = g.Key.Title,
                ["count"] = g.Count(),
            })
            .ToArray();
        return new JsonObject
        {
            ["subject"] = json,
            ["from"] = from.ToString("yyyy-MM-dd"),
            ["to"] = to.ToString("yyyy-MM-dd"),
            ["total"] = emissions.Count,
            ["bySpot"] = new JsonArray(bySpot),
            ["emissions"] = new JsonArray(emissions.Select(e => (JsonNode)new JsonObject
            {
                ["at"] = LocalText(e.Started),
                ["trackId"] = e.TrackId,
                ["artist"] = e.Artist,
                ["title"] = e.Title,
                ["playedMs"] = e.PlayedMs,
                ["complete"] = e.Complete,
            }).ToArray()),
        };
    }
}
