using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;

namespace OnaBages24.Engine.Protocol;

// Manteniment de la programació (D-194): el que s'ha anat acumulant i ja no sonarà, amb què es proposa fer-ne.
// L'anàlisi (maintenance.scan) només llegeix; aplicar-ho és maintenance.apply.
public sealed partial class CommandRouter
{
    /// <summary>Tot el que necessita l'anàlisi, llegit ara de la base de dades.</summary>
    private MaintenanceInputs MaintenanceInputs()
    {
        var library = LibraryOrError();
        var events = EventsOrError().List();
        var now = CampaignClock();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);

        // Les pistes, una sola vegada cadascuna (un esdeveniment que es repeteix les demana moltes vegades)
        var tracks = new Dictionary<long, Track?>();
        Track? GetTrack(long id)
        {
            if (!tracks.TryGetValue(id, out var track)) tracks[id] = track = library.GetTrack(id);
            return track;
        }

        var (categories, _) = library.SearchFilters();
        var subcategories = categories.SelectMany(c => c.Subcategories.Select(s => (Sub: s, Category: c.Name))).ToList();
        var unusedEmpty = subcategories
            .Where(x => x.Sub.Count == 0 && SubcategoryDeleteReasons(x.Sub.Id).Count == 0)
            .Select(x => (x.Sub.Id, $"{x.Category} / {x.Sub.Name}"))
            .ToList();

        var twoYearsAgo = now.AddYears(-2);
        return new MaintenanceInputs
        {
            Events = events,
            GetTrack = GetTrack,
            Campaigns = _campaigns?.List() ?? [],
            RotationIds = (_rotations?.List().Select(r => r.Id) ?? []).ToHashSet(),
            BagIds = (_rotations?.Bags().Select(b => b.Id) ?? []).ToHashSet(),
            SubcategoryIds = subcategories.Select(x => x.Sub.Id).ToHashSet(),
            ExpiredTracks = library.Search(new TrackQuery { Validity = TrackValidity.Expired, Today = today, Limit = 100_000 }),
            PlayedRecently = id => library.PlaysSince(id, twoYearsAgo) > 0,
            UnusedEmptySubcategories = unusedEmpty,
            Now = now,
            Zone = TimeZoneInfo.Local,
        };
    }

    private static string CaseText(MaintenanceCase kind) => kind switch
    {
        MaintenanceCase.EndedEvent => "ESDEVENIMENT_ACABAT",
        MaintenanceCase.DisabledEvent => "ESDEVENIMENT_DESACTIVAT",
        MaintenanceCase.DuplicateEvent => "ESDEVENIMENT_DUPLICAT",
        MaintenanceCase.NeverWins => "ESDEVENIMENT_NO_GUANYA",
        MaintenanceCase.DeadEvent => "ESDEVENIMENT_SENSE_EFECTE",
        MaintenanceCase.BrokenActions => "ACCIONS_TRENCADES",
        MaintenanceCase.PastExceptions => "EXCEPCIONS_PASSADES",
        MaintenanceCase.ExpiredInEvents => "ACABADA_ALS_ESDEVENIMENTS",
        MaintenanceCase.ExpiredTrack => "PISTA_CADUCADA",
        _ => "SUBCATEGORIA_BUIDA",
    };

    private static string ActionText(MaintenanceAction action) => action switch
    {
        MaintenanceAction.DeleteEvent => "ESBORRA_ESDEVENIMENT",
        MaintenanceAction.RemoveActions => "TREU_ACCIONS",
        MaintenanceAction.RemovePastExceptions => "TREU_EXCEPCIONS",
        MaintenanceAction.RemoveFromEvents => "TREU_DELS_ESDEVENIMENTS",
        MaintenanceAction.DeleteTrack => "ESBORRA_PISTA",
        MaintenanceAction.DisableTrack => "DESACTIVA_PISTA",
        _ => "ESBORRA_SUBCATEGORIA",
    };

    private static string SubjectText(MaintenanceSubject subject) => subject switch
    {
        MaintenanceSubject.Event => "event",
        MaintenanceSubject.Track => "track",
        MaintenanceSubject.Campaign => "campaign",
        _ => "subcategory",
    };

    /// <summary>L'anàlisi: les propostes, en l'ordre de la pantalla. No canvia res.</summary>
    private JsonObject MaintenanceScan()
    {
        var findings = ProgrammingMaintenance.Scan(MaintenanceInputs());
        return new JsonObject
        {
            ["findings"] = new JsonArray(findings.Select(f => (JsonNode)new JsonObject
            {
                ["key"] = f.Key,
                ["case"] = CaseText(f.Case),
                ["subject"] = SubjectText(f.Subject),
                ["id"] = f.Id,
                ["name"] = f.Name,
                ["reason"] = f.Reason,
                ["action"] = ActionText(f.Action),
            }).ToArray()),
        };
    }
}
