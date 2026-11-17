using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine.Playback;

namespace OnaBages24.Engine.Protocol;

// Agenda de bosses (D-170; docs/protocol.md, 4.16): canvia soles les subcategories d'unes bosses el dia 1 de cada mes.
public sealed partial class CommandRouter
{
    private BagAgendaRunner? _agendaRunner;

    /// <summary>S'ha creat, canviat o esborrat una agenda (les interfícies tornen a demanar la llista).</summary>
    public event Action? AgendasChanged;

    /// <summary>Fins a quants mesos es poden previsualitzar.</summary>
    private const int MaxPreviewMonths = 36;

    private BagAgendaRunner AgendasOrError() => _agendaRunner ?? throw new BadParamsException("El motor no té agendes de bosses.");

    /// <summary><c>agendas.list</c>: totes les agendes, amb què toca aquest mes.</summary>
    private JsonObject AgendasList()
    {
        var runner = AgendasOrError();
        var today = runner.Today();
        return new JsonObject
        {
            ["month"] = BagAgenda.MonthText(today),
            ["agendas"] = new JsonArray(runner.Agendas.List().Select(a => (JsonNode)AgendaJson(a, today)).ToArray()),
        };
    }

    /// <summary>
    /// <c>agendas.save</c>: <c>name</c>, <c>startMonth</c> («2026-12»), <c>active</c>, <c>bagIds</c> i <c>subcategoryIds</c>
    /// (en ordre); <c>agendaId</c> per substituir-ne una. Si és activa, les bosses passen de seguida a la subcategoria
    /// d'aquest mes.
    /// </summary>
    private JsonObject AgendasSave(JsonObject msg)
    {
        var runner = AgendasOrError();
        long? id = msg["agendaId"] is null ? null : RequiredId(msg, "agendaId");
        string name = RequiredName(msg);
        var start = BagAgenda.ParseMonth(ReadString(msg["startMonth"])) ?? throw new BadParamsException("Falta 'startMonth' (un mes, «2026-12»).");
        bool active = msg["active"] is JsonValue v && v.TryGetValue(out bool b) ? b : throw new BadParamsException("Falta 'active' (true o false).");
        var (bagIds, subcategoryIds) = ReadAgendaLists(msg);
        if (id is { } existing && runner.Agendas.Get(existing) is null) throw new BadParamsException($"No existeix l'agenda {existing}.");
        long saved = runner.Agendas.Save(id, name, start, active, bagIds, subcategoryIds);
        AgendasChanged?.Invoke();
        int changed = runner.ApplyDue(); // les bosses avisen soles (bags.changed)
        return new JsonObject { ["agenda"] = AgendaJson(runner.Agendas.Get(saved)!, runner.Today()), ["changedBags"] = changed };
    }

    /// <summary><c>agendas.delete</c>: <c>agendaId</c>. Les bosses es queden amb la subcategoria que tenen.</summary>
    private JsonObject AgendasDelete(JsonObject msg)
    {
        var runner = AgendasOrError();
        long id = RequiredId(msg, "agendaId");
        if (runner.Agendas.Get(id) is null) throw new BadParamsException($"No existeix l'agenda {id}.");
        runner.Agendas.Delete(id);
        AgendasChanged?.Invoke();
        return new JsonObject { ["deleted"] = id };
    }

    /// <summary>
    /// <c>agendas.preview</c>: què tocaria cada mes amb aquesta agenda, encara que no estigui desada. <c>startMonth</c>,
    /// <c>bagIds</c>, <c>subcategoryIds</c>; opcionals <c>from</c> (el primer mes; per defecte, aquest) i <c>months</c>
    /// (per defecte 12; com a molt 36).
    /// </summary>
    private JsonObject AgendasPreview(JsonObject msg)
    {
        var runner = AgendasOrError();
        var start = BagAgenda.ParseMonth(ReadString(msg["startMonth"])) ?? throw new BadParamsException("Falta 'startMonth' (un mes, «2026-12»).");
        var (bagIds, subcategoryIds) = ReadAgendaLists(msg);
        var from = msg["from"] is null ? BagAgenda.FirstOfMonth(runner.Today())
            : BagAgenda.ParseMonth(ReadString(msg["from"])) ?? throw new BadParamsException("El camp 'from' ha de ser un mes («2026-12»).");
        int months = msg["months"] is null ? 12 : ReadInt(msg["months"]) is { } n and >= 1 and <= MaxPreviewMonths ? n
            : throw new BadParamsException($"El camp 'months' ha de ser un número de l'1 al {MaxPreviewMonths}.");
        var agenda = new BagAgenda(0, "", start, true, bagIds, subcategoryIds, null);
        var bagNames = (_rotations?.Bags() ?? []).ToDictionary(x => x.Id, x => x.Name);
        var list = new JsonArray();
        for (int i = 0; i < months; i++)
        {
            var month = from.AddMonths(i);
            list.Add(new JsonObject
            {
                ["month"] = BagAgenda.MonthText(month),
                ["bags"] = AssignmentJson(agenda.Assignment(month), bagNames),
            });
        }
        return new JsonObject { ["months"] = list };
    }

    /// <summary>Les bosses i les subcategories d'una agenda, comprovades (que existeixin i que l'agenda sigui vàlida).</summary>
    private (List<long> BagIds, List<long> SubcategoryIds) ReadAgendaLists(JsonObject msg)
    {
        static List<long> Ids(JsonNode? node, string field) => node is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue(out long id) ? id : throw new BadParamsException($"El camp '{field}' ha de ser una llista de números.")).ToList()
            : throw new BadParamsException($"Falta '{field}' (una llista de números).");
        var bagIds = Ids(msg["bagIds"], "bagIds");
        var subcategoryIds = Ids(msg["subcategoryIds"], "subcategoryIds");
        if (BagAgenda.Problem("-", bagIds, subcategoryIds) is { } problem) throw new BadParamsException(problem);
        var rotations = RotationsOrError();
        foreach (long bag in bagIds)
            if (rotations.GetBag(bag) is null) throw new BadParamsException($"No existeix la bossa {bag}.");
        var library = LibraryOrError();
        foreach (long sub in subcategoryIds)
            if (library.SubcategoryName(sub) is null) throw new BadParamsException($"No existeix la subcategoria {sub}.");
        return (bagIds, subcategoryIds);
    }

    private JsonObject AgendaJson(BagAgenda agenda, DateOnly today)
    {
        var bags = (_rotations?.Bags() ?? []).ToDictionary(x => x.Id, x => x.Name);
        return new JsonObject
        {
            ["id"] = agenda.Id,
            ["name"] = agenda.Name,
            ["startMonth"] = BagAgenda.MonthText(agenda.StartMonth),
            ["active"] = agenda.Active,
            ["appliedMonth"] = agenda.AppliedMonth is { } applied ? BagAgenda.MonthText(applied) : null,
            ["bags"] = new JsonArray(agenda.BagIds.Select(id => (JsonNode)new JsonObject { ["id"] = id, ["name"] = bags.GetValueOrDefault(id) }).ToArray()),
            ["subcategories"] = new JsonArray(agenda.SubcategoryIds.Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id, ["name"] = _library?.SubcategoryName(id),
            }).ToArray()),
            // El que toca aquest mes i el mes que ve (buit si encara no ha arribat el mes d'inici)
            ["current"] = AssignmentJson(agenda.Assignment(today), bags),
            ["next"] = AssignmentJson(agenda.Assignment(BagAgenda.FirstOfMonth(today).AddMonths(1)), bags),
        };
    }

    private JsonArray AssignmentJson(IReadOnlyList<(long BagId, long SubcategoryId)> assignment, Dictionary<long, string> bagNames) =>
        new(assignment.Select(a => (JsonNode)new JsonObject
        {
            ["bagId"] = a.BagId,
            ["bag"] = bagNames.GetValueOrDefault(a.BagId),
            ["subcategoryId"] = a.SubcategoryId,
            ["subcategory"] = _library?.SubcategoryName(a.SubcategoryId),
        }).ToArray());
}
