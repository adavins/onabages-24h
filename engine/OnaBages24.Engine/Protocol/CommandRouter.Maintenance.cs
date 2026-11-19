using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Scheduling;
using OnaBages24.Engine.Security;

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

    /// <summary>
    /// Aplica les propostes marcades (<c>keys</c>, les de <c>maintenance.scan</c>). Abans fa una còpia de seguretat
    /// («abans del manteniment»), i torna a analitzar: només es fan les que encara hi són (si mentrestant algú ha canviat
    /// alguna cosa, les altres es diuen a <c>skipped</c>). Es fan una a una: si una falla, s'atura i ho diu (la còpia permet
    /// desfer-ho tot). Cada canvi s'anota al registre de canvis.
    /// </summary>
    private JsonObject MaintenanceApply(JsonObject msg, Session? session)
    {
        if (msg["keys"] is not JsonArray array || array.Count == 0) throw new BadParamsException("Falta el camp 'keys' (les propostes que s'han d'aplicar).");
        var keys = array.Select(k => ReadString(k)).OfType<string>().Distinct().ToList();
        if (keys.Count == 0) throw new BadParamsException("Falta el camp 'keys' (les propostes que s'han d'aplicar).");

        // Primer la còpia: sense, no es toca res
        string backup;
        try { backup = BackupsOrError().CreateBeforeMaintenance(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new BadParamsException($"No s'ha pogut fer la còpia de seguretat; no s'ha canviat res: {ex.Message}");
        }

        var findings = ProgrammingMaintenance.Scan(MaintenanceInputs()).ToDictionary(f => f.Key);
        var done = new JsonArray();
        var skipped = new JsonArray();
        string? error = null;
        bool eventsChanged = false, libraryChanged = false;
        foreach (string key in keys)
        {
            if (!findings.TryGetValue(key, out var f))
            {
                skipped.Add((JsonNode)new JsonObject { ["key"] = key, ["reason"] = "Ja no hi és (ha canviat des de l'anàlisi)." });
                continue;
            }
            try
            {
                string summary = ApplyFinding(f);
                eventsChanged |= f.Subject is MaintenanceSubject.Event or MaintenanceSubject.Campaign || f.Action == MaintenanceAction.RemoveFromEvents;
                libraryChanged |= f.Subject is MaintenanceSubject.Track or MaintenanceSubject.Subcategory;
                done.Add((JsonNode)new JsonObject { ["key"] = key, ["text"] = summary });
                LogMaintenance(session, summary, f);
            }
            catch (Exception ex) when (ex is DatabaseException or BadParamsException or Microsoft.Data.Sqlite.SqliteException)
            {
                error = $"«{f.Name}»: {ex.Message}";
                break;
            }
        }
        if (eventsChanged) _scheduler?.Reload();
        if (libraryChanged) LibraryChanged?.Invoke(null);
        return new JsonObject { ["backup"] = backup, ["done"] = done, ["skipped"] = skipped, ["error"] = error };
    }

    /// <summary>Fa una proposta i en retorna el resum (per al registre i per a la pantalla).</summary>
    private string ApplyFinding(MaintenanceFinding f)
    {
        var events = EventsOrError();
        var library = LibraryOrError();
        ScheduledEvent Event() => events.Get(f.Id) ?? throw new BadParamsException($"Ja no existeix l'esdeveniment {f.Id}.");
        switch (f.Action)
        {
            case MaintenanceAction.DeleteEvent:
                events.Delete(f.Id);
                return $"Ha esborrat l'esdeveniment «{f.Name}»";
            case MaintenanceAction.RemoveActions:
            {
                var ev = Event();
                events.Save(ev with { Actions = ev.Actions.Where(a => !f.ActionIds.Contains(a.Id)).ToList() });
                return $"Ha tret {(f.ActionIds.Count == 1 ? "una acció trencada" : $"{f.ActionIds.Count} accions trencades")} de l'esdeveniment «{f.Name}»";
            }
            case MaintenanceAction.RemovePastExceptions:
            {
                var ev = Event();
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(CampaignClock(), TimeZoneInfo.Local).DateTime);
                events.Save(ev with { ExcludedDates = ev.ExcludedDates.Where(d => d >= today).ToList() });
                return $"Ha tret els dies d'excepció passats de l'esdeveniment «{f.Name}»";
            }
            case MaintenanceAction.RemoveFromEvents:
            {
                bool campaign = f.Subject == MaintenanceSubject.Campaign;
                ValidityRemove(new JsonObject { [campaign ? "campaignId" : "trackId"] = f.Id });
                return $"Ha tret {(campaign ? "la campanya" : "la pista")} «{f.Name}» dels esdeveniments";
            }
            case MaintenanceAction.DeleteTrack:
                library.DeleteTrack(f.Id);
                return $"Ha esborrat de la biblioteca la pista «{f.Name}»";
            case MaintenanceAction.DisableTrack:
                library.SetEnabled([f.Id], false);
                return $"Ha desactivat la pista «{f.Name}»";
            default:
                library.DeleteSubcategory(f.Id);
                return $"Ha esborrat la subcategoria «{f.Name}»";
        }
    }

    /// <summary>Una entrada al registre de canvis per a cada proposta aplicada, amb el motiu al detall.</summary>
    private void LogMaintenance(Session? session, string summary, MaintenanceFinding f)
    {
        if (_users is null) return;
        try
        {
            _users.AddChange(new ChangeEntry
            {
                At = DateTimeOffset.UtcNow,
                UserId = session?.UserId,
                UserName = session?.Name ?? "",
                Command = "maintenance.apply",
                Summary = $"Manteniment: {char.ToLowerInvariant(summary[0])}{summary[1..]}",
                Detail = new JsonObject { ["key"] = f.Key, ["case"] = CaseText(f.Case), ["reason"] = f.Reason }.ToJsonString(),
            });
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException) { }
    }

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
