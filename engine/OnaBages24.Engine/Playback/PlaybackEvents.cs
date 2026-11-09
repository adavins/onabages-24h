using System.Text.Json.Nodes;
using OnaBages24.Engine.Protocol;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Fa saber què passa a l'emissió: ho escriu al registre i ho envia a la interfície (docs/protocol.md, 5):
/// <c>player.trackStarted</c>, <c>player.trackEnded</c>, <c>player.trackFailed</c>, <c>queue.changed</c>, <c>output.changed</c>
/// i <c>autodj.changed</c>.
/// </summary>
public sealed class PlaybackEvents
{
    public PlaybackEvents(QueuePlayer queue, OutputManager outputs, ILogger log, Action<JsonObject> broadcast, AutoDj? autoDj = null)
    {
        if (autoDj is not null)
        {
            autoDj.Changed += () =>
            {
                if (autoDj.Problem is { } problem) log.LogWarning("AutoDJ: {Problem}", problem);
                else log.LogInformation("AutoDJ {State}, rotació {Rotation}", autoDj.Enabled ? "actiu" : "aturat", autoDj.Rotation?.Name ?? "cap");
                var ev = Messages.Event("autodj.changed");
                ev["autodj"] = QueueJson.AutoDj(autoDj);
                broadcast(ev);
            };
        }

        queue.Started += e =>
        {
            log.LogInformation("Sona: {Track} ({Origin})", e.Item, QueueJson.Origin(e.Item.Origin));
            var ev = Messages.Event("player.trackStarted");
            ev["item"] = QueueJson.Item(e.Item);
            ev["time"] = DateTimeOffset.UtcNow.ToString("O");
            broadcast(ev);
        };

        queue.Ended += e =>
        {
            var ev = Messages.Event("player.trackEnded");
            ev["item"] = QueueJson.Item(e.Item);
            ev["playedMs"] = (long)e.Played.TotalMilliseconds;
            ev["complete"] = e.Complete;
            broadcast(ev);
        };

        queue.Failed += e =>
        {
            log.LogWarning("No es pot fer sonar {Track}: {Message}. Se salta.", e.Item, e.Message);
            var ev = Messages.Event("player.trackFailed");
            ev["item"] = QueueJson.Item(e.Item);
            ev["message"] = e.Message;
            broadcast(ev);
        };

        queue.Changed += () =>
        {
            var ev = Messages.Event("queue.changed");
            ev["queue"] = QueueJson.Queue(queue);
            broadcast(ev);
        };

        outputs.Changed += () =>
        {
            if (outputs.Problem is { } problem) log.LogWarning("Sortida: {Problem}", problem);
            else if (outputs.Current is { } device) log.LogInformation("Sortida: {Device}", device.Name);
            var ev = Messages.Event("output.changed");
            ev["output"] = QueueJson.Output(outputs);
            broadcast(ev);
        };
    }
}
