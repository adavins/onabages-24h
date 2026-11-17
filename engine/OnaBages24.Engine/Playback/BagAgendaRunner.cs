using Microsoft.Extensions.Logging;
using OnaBages24.Data;

namespace OnaBages24.Engine.Playback;

/// <summary>
/// Agenda de bosses (D-170): el dia 1 de cada mes (hora local) posa a cada bossa la subcategoria que li toca. Es mira en
/// arrencar i cada 30 s (EngineServer), i en desar una agenda. Un sol canvi per mes: si el motor estava aturat el dia 1,
/// es fa en arrencar; un canvi a mà a mig mes no es desfà fins al mes següent. El canvi es fa com a mà o amb l'acció
/// BOSSA (<see cref="AutoDj.SetBag"/>): les pistes de la cua es tornen a triar (D-097).
/// </summary>
public sealed class BagAgendaRunner(BagAgendas agendas, AutoDj autoDj, Rotations rotations, Func<DateTimeOffset> clock,
    TimeZoneInfo zone, ILogger? log = null)
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
                        log?.LogInformation("Agenda «{Agenda}»: la bossa «{Bag}» passa a la subcategoria {Subcategory}",
                            agenda.Name, bag.Name, subcategoryId);
                    }
                    else log?.LogWarning("Agenda «{Agenda}»: no s'ha pogut canviar la bossa «{Bag}»", agenda.Name, bag.Name);
                }
                // Anotat encara que una bossa no s'hagi pogut canviar: no es torna a provar cada 30 s
                agendas.MarkApplied(agenda.Id, today);
            }
            return changed;
        }
    }
}
