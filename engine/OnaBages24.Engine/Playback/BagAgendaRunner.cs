using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Agenda de bosses (D-170): el dia 1 de cada mes (hora local) posa a cada bossa la subcategoria que li toca. Es mira en
/// arrencar i cada 30 s (EngineServer), i en desar una agenda. Un sol canvi per mes: si el motor estava aturat el dia 1,
/// es fa en arrencar; un canvi a mà a mig mes no es desfà fins al mes següent. El canvi es fa com a mà o amb l'acció
/// BOSSA (<see cref="AutoDj.SetBag"/>): les pistes de la cua es tornen a triar (D-097). Cada bossa que canvia s'anota al
/// registre de canvis (usuari «Motor», ordre <c>agendas.apply</c>), com els canvis a mà.
/// </summary>
public sealed class BagAgendaRunner(BagAgendas agendas, AutoDj autoDj, Rotations rotations, Func<DateTimeOffset> clock,
    TimeZoneInfo zone, ILogger? log = null, UserStore? changes = null)
{
    private readonly object _sync = new();

    public BagAgendas Agendas => agendas;

    /// <summary>Avui, en hora local.</summary>
    public DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock(), zone).DateTime);

    /// <summary>Fa els canvis de les agendes que toquen. Retorna quantes bosses han canviat.</summary>
    public int ApplyDue()
    {
        lock (_sync) // el temporitzador i el «Desa» d'una agenda no s'han de trepitjar
        {
            var today = Today();
            int changed = 0;
            foreach (var agenda in agendas.List().Where(a => a.IsDue(today)))
            {
                foreach (var (bagId, subcategoryId) in agenda.Assignment(today))
                {
                    if (rotations.GetBag(bagId) is not { } bag || bag.SubcategoryId == subcategoryId) continue;
                    if (autoDj.SetBag(bagId, subcategoryId, $"agenda «{agenda.Name}»"))
                    {
                        changed++;
                        string to = rotations.GetBag(bagId)?.SubcategoryName ?? subcategoryId.ToString();
                        log?.LogInformation("Agenda «{Agenda}»: la bossa «{Bag}» passa de {From} a {To}",
                            agenda.Name, bag.Name, bag.SubcategoryName ?? "cap", to);
                        LogChange(agenda, bag, to);
                    }
                    else log?.LogWarning("Agenda «{Agenda}»: no s'ha pogut canviar la bossa «{Bag}»", agenda.Name, bag.Name);
                }
                // Anotat encara que una bossa no s'hagi pogut canviar: no es torna a provar cada 30 s
                agendas.MarkApplied(agenda.Id, today);
            }
            return changed;
        }
    }

    /// <summary>
    /// Al registre de canvis (D-164): «Agenda «Adult»: la bossa «Generalista A» passa d'Adult 1 a Adult 3», sense usuari
    /// (ho ha fet el motor). Si no es pot anotar, el canvi ja està fet: no es torna enrere.
    /// </summary>
    private void LogChange(BagAgenda agenda, Bag bag, string to)
    {
        if (changes is null) return;
        string from = bag.SubcategoryName ?? "cap";
        try
        {
            changes.AddChange(new ChangeEntry
            {
                At = clock(),
                UserName = "Motor",
                Command = "agendas.apply",
                Summary = $"Agenda «{agenda.Name}»: la bossa «{bag.Name}» passa {Of(Short(from))} a {Short(to)}",
                Detail = new JsonObject { ["agendaId"] = agenda.Id, ["bagId"] = bag.Id, ["from"] = from, ["to"] = to }.ToJsonString(),
            });
        }
        catch (Exception ex) when (ex is DatabaseException or Microsoft.Data.Sqlite.SqliteException)
        {
            log?.LogWarning("Agenda «{Agenda}»: no s'ha pogut anotar el canvi al registre: {Message}", agenda.Name, ex.Message);
        }

        // «Music / Adult 1» → «Adult 1»: sense la categoria (és la mateixa i allarga el text)
        static string Short(string name)
        {
            int i = name.IndexOf(" / ", StringComparison.Ordinal);
            return i >= 0 ? name[(i + 3)..] : name;
        }

        // «d'Adult 1», «de Pop»: apostrofat davant de vocal o hac
        static string Of(string name) =>
            name.Length > 0 && "aeiouàèéíòóúAEIOUÀÈÉÍÒÓÚhH".Contains(name[0]) ? $"d'{name}" : $"de {name}";
    }
}
