using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Protocol;

/// <summary>Conversió de la cua, les pistes i la sortida al format del protocol (docs/protocol.md, 4.2 a 4.4).</summary>
public static class QueueJson
{
    public static JsonObject Item(QueueItem item) => new()
    {
        ["id"] = item.Id.ToString(),
        ["trackId"] = item.TrackId,
        ["path"] = item.Path,
        ["artist"] = item.Artist,
        ["title"] = item.Title,
        ["type"] = Type(item.Type),
        ["origin"] = Origin(item.Origin),
        ["eventName"] = item.EventName,
    };

    public static JsonObject Queue(QueuePlayer player) => new()
    {
        ["current"] = player.Current is { } current ? Item(current) : null,
        ["upcoming"] = new JsonArray(player.Upcoming.Select(i => (JsonNode)Item(i)).ToArray()),
    };

    public static JsonObject Track(Track track) => new()
    {
        ["id"] = track.Id,
        ["path"] = track.Path,
        ["artist"] = track.Artist,
        ["title"] = track.Title,
        ["type"] = Type(track.Type),
        ["durationMs"] = track.DurationMs,
        ["cueSource"] = track.CueSource switch
        {
            CueSource.RadioDj => "radiodj",
            CueSource.Analyzer => "analyzer",
            CueSource.Manual => "manual",
            _ => null,
        },
        ["fileState"] = track.FileState switch
        {
            FileState.Missing => "missing",
            FileState.Retired => "retired",
            _ => "ok",
        },
    };

    public static JsonObject Output(OutputManager outputs) => new()
    {
        ["configured"] = new JsonObject { ["id"] = outputs.Configured.Id, ["name"] = outputs.Configured.Name },
        ["current"] = outputs.Current is { } device ? DeviceJson.Device(device) : null,
        ["emergency"] = outputs.IsEmergency,
        ["problem"] = outputs.Problem,
    };

    /// <summary>Estat de l'AutoDJ (docs/protocol.md, 4.5).</summary>
    public static JsonObject AutoDj(AutoDj autoDj) => new()
    {
        ["enabled"] = autoDj.Enabled,
        ["rotation"] = autoDj.Rotation is { } r ? new JsonObject { ["id"] = r.Id, ["name"] = r.Name } : null,
        ["nextStep"] = autoDj.NextStep,
        ["problem"] = autoDj.Problem,
    };

    /// <summary>Una rotació amb els seus passos (docs/protocol.md, 4.5).</summary>
    public static JsonObject Rotation(Rotation rotation) => new()
    {
        ["id"] = rotation.Id,
        ["name"] = rotation.Name,
        ["steps"] = new JsonArray(rotation.Steps.Select(s => (JsonNode)new JsonObject
        {
            ["category"] = s.CategoryName,
            ["subcategory"] = s.SubcategoryName,
            ["tag"] = s.TagName,
            ["selection"] = s.Selection == RotationSelection.Random ? "random" : "leastRecent",
            ["repeatRule"] = s.RepeatRule,
            ["sweeper"] = s.Sweeper,
        }).ToArray()),
    };

    /// <summary>Tipus de pista: <c>music</c>, <c>sweeper</c>, <c>stationId</c>, <c>promo</c>, <c>commercial</c>, <c>program</c>, <c>news</c>, <c>timeAnnouncement</c>, <c>timeSignal</c>, <c>stream</c>, <c>other</c>.</summary>
    public static string Type(TrackType type)
    {
        string name = type.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>D'on ve un element de la cua (D-034): <c>autodj</c>, <c>manual</c> o <c>event</c>.</summary>
    public static string Origin(QueueOrigin origin) => origin switch
    {
        QueueOrigin.AutoDj => "autodj",
        QueueOrigin.Event => "event",
        _ => "manual",
    };
}
