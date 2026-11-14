using System.Text.Json;
using OnaBages24.Data;

namespace OnaBages24.Engine.Scheduling;

/// <summary>
/// Calcula el pla de passis d'una campanya (fase 4, pas 3b; D-029, D-123): quina falca sona a cada emissió dels esdeveniments
/// que la fan sonar (acció CAMPANYA). Càlcul pur, sense base de dades ni rellotge.
///
/// - Es mira cada dia de calendari de la campanya (<see cref="DateOnly"/>), amb les recurrències, les excepcions i els dies
///   propis de l'acció, com el planificador. Només compten els esdeveniments actius, que no són plantilles i que fan sonar
///   pistes (ENCUA o TALLA).
/// - A cada passi, les falques vàlides són les que aquell dia són dins de les seves dates i dels seus dies, i la pista existeix,
///   està activada i és dins de les seves dates. Si no n'hi ha cap, el passi queda buit.
/// - <b>Alterna</b> i <b>Pes</b>: sona la falca que porta menys passis respecte al seu pes; si n'hi ha dues d'iguals, la que
///   porta menys passis a aquella hora del dia (així una no surt sempre al matí i l'altra al vespre), i després l'ordre de la
///   llista. <b>Seqüència</b>: la següent de la llista que sigui vàlida (A, B, C, A…).
/// - Els passis d'abans de <c>from</c> (ja sonats o passats) no es tornen a calcular, però compten per a l'equilibri.
/// </summary>
public static class CampaignPlanner
{
    /// <summary>Com a molt, quants dies endavant es calcula el pla (com els recomptes del calendari).</summary>
    public const int MaxDays = 400;

    /// <summary>Els esdeveniments que fan sonar aquesta campanya (alguna acció CAMPANYA amb el seu id), actius i que fan sonar pistes.</summary>
    public static IReadOnlyList<ScheduledEvent> EventsFor(long campaignId, IEnumerable<ScheduledEvent> events) =>
        events.Where(e => e.Enabled && !e.IsTemplate && e.Mode is EventMode.Queue or EventMode.Cut
                          && e.Actions.Any(a => IsCampaignAction(a, campaignId)))
              .ToList();

    /// <summary>Si l'acció és CAMPANYA d'aquesta campanya.</summary>
    public static bool IsCampaignAction(EventAction action, long campaignId) =>
        action.Type == EventActionType.Campaign && CampaignIdOf(action) == campaignId;

    /// <summary>L'id de campanya d'una acció CAMPANYA (<c>{"campanyaId": 3}</c>), o null.</summary>
    public static long? CampaignIdOf(EventAction action)
    {
        try
        {
            using var json = JsonDocument.Parse(action.Params);
            return json.RootElement.TryGetProperty("campanyaId", out var v) && v.TryGetInt64(out long id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Les falques que poden sonar aquest dia (en l'ordre de la campanya).</summary>
    public static IReadOnlyList<CampaignSpot> ValidSpots(Campaign campaign, DateOnly date, Func<long, Track?> getTrack) =>
        campaign.Spots.Where(s =>
            date >= (s.StartDate ?? campaign.StartDate) && date <= (s.EndDate ?? campaign.EndDate)
            && Weekdays.Contains(s.DaysMask, date.DayOfWeek)
            && getTrack(s.TrackId) is { Enabled: true } track && track.ValidOn(date)).ToList();

    /// <summary>
    /// El pla a partir de <paramref name="from"/> (inclòs) fins al final de la campanya (com a molt <see cref="MaxDays"/> dies),
    /// en ordre d'hora. <paramref name="past"/>: el que ja ha sonat (historial), per a l'equilibri.
    /// </summary>
    public static IReadOnlyList<CampaignPass> Compute(Campaign campaign, IEnumerable<ScheduledEvent> events, Func<long, Track?> getTrack,
        IReadOnlyList<CampaignPass> past, DateTimeOffset from, TimeZoneInfo zone)
    {
        var mine = EventsFor(campaign.Id, events);
        if (mine.Count == 0 || campaign.Spots.Count == 0) return [];

        // Les emissions, de tots els esdeveniments, en ordre d'hora
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var first = campaign.StartDate > today ? campaign.StartDate : today.AddDays(-1); // un dia de marge pel canvi de zona
        var last = campaign.EndDate < today.AddDays(MaxDays) ? campaign.EndDate : today.AddDays(MaxDays);
        var occurrences = new List<Occurrence>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            if (date < campaign.StartDate) continue;
            foreach (var ev in mine)
            {
                if (!Recurrences.OccursOn(ev, date)) continue;
                if (!Recurrences.ActionsFor(ev, date).Any(a => IsCampaignAction(a, campaign.Id))) continue; // dies propis de l'acció
                foreach (var time in Recurrences.TimesOfDay(ev))
                {
                    var o = Recurrences.At(ev, date, time, zone);
                    if (!o.SkippedClockChange && o.At >= from) occurrences.Add(o);
                }
            }
        }
        occurrences.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Event.Id.CompareTo(b.Event.Id));

        var balance = new Balance(campaign, zone);
        foreach (var p in past) balance.Add(p);

        var plan = new List<CampaignPass>(occurrences.Count);
        var validByDay = new Dictionary<DateOnly, IReadOnlyList<CampaignSpot>>();
        foreach (var o in occurrences)
        {
            if (!validByDay.TryGetValue(o.Date, out var valid)) validByDay[o.Date] = valid = ValidSpots(campaign, o.Date, getTrack);
            var pass = new CampaignPass(o.Event.Id, o.At, balance.Pick(valid, o.LocalTime.Hour));
            balance.Add(pass);
            plan.Add(pass);
        }
        return plan;
    }

    /// <summary>Quants passis porta cada falca (en total i per hora del dia) i quina va ser l'última (per a la seqüència).</summary>
    private sealed class Balance(Campaign campaign, TimeZoneInfo zone)
    {
        private readonly Dictionary<long, int> _total = [];
        private readonly Dictionary<(long Track, int Hour), int> _byHour = [];
        private int _lastIndex = -1;

        public void Add(CampaignPass pass)
        {
            if (pass.TrackId is not { } id) return;
            _total[id] = _total.GetValueOrDefault(id) + 1;
            int hour = TimeZoneInfo.ConvertTime(pass.At, zone).Hour;
            _byHour[(id, hour)] = _byHour.GetValueOrDefault((id, hour)) + 1;
            int index = IndexOf(id);
            if (index >= 0) _lastIndex = index;
        }

        public long? Pick(IReadOnlyList<CampaignSpot> valid, int hour)
        {
            if (valid.Count == 0) return null;
            if (campaign.Selection == CampaignSelection.Sequence)
            {
                // La següent de la llista després de l'última que ha sonat, saltant les que avui no són vàlides
                int n = campaign.Spots.Count;
                for (int step = 1; step <= n; step++)
                {
                    var spot = campaign.Spots[(_lastIndex + step + n) % n];
                    if (valid.Any(v => v.TrackId == spot.TrackId)) return spot.TrackId;
                }
                return valid[0].TrackId;
            }
            double Weight(CampaignSpot s) => campaign.Selection == CampaignSelection.Weight ? s.Weight : 1;
            return valid
                .Select((s, order) => (s, order))
                .OrderBy(x => (_total.GetValueOrDefault(x.s.TrackId) + 1) / Weight(x.s))
                .ThenBy(x => (_byHour.GetValueOrDefault((x.s.TrackId, hour)) + 1) / Weight(x.s))
                .ThenBy(x => IndexOf(x.s.TrackId))
                .First().s.TrackId;
        }

        private int IndexOf(long trackId)
        {
            for (int i = 0; i < campaign.Spots.Count; i++)
                if (campaign.Spots[i].TrackId == trackId) return i;
            return -1;
        }
    }
}

/// <summary>
/// Els plans de les campanyes al motor (fase 4, pas 3b): els recalcula quan canvien les campanyes, els esdeveniments o la
/// biblioteca, i tria la falca de cada passi quan sona. Es pot cridar des de qualsevol fil.
/// </summary>
public sealed class CampaignPlans(Campaigns campaigns, Events events, Library library, TimeZoneInfo? zone = null, Func<DateTimeOffset>? clock = null)
{
    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _sync = new();

    /// <summary>
    /// Torna a calcular els passis futurs de totes les campanyes. Les que no són actives no en tenen (s'esborren els futurs).
    /// Retorna quants passis futurs hi ha en total.
    /// </summary>
    public int RecalculateAll()
    {
        lock (_sync)
        {
            var now = _clock();
            var all = events.List();
            int total = 0;
            foreach (var campaign in campaigns.List()) total += Recalculate(campaign, all, now);
            return total;
        }
    }

    private int Recalculate(Campaign campaign, IReadOnlyList<ScheduledEvent> all, DateTimeOffset now)
    {
        IReadOnlyList<CampaignPass> plan = [];
        if (campaign.State == CampaignState.Active)
        {
            // L'equilibri compta el que ha sonat de debò (historial), no el pla d'abans: un passi omès no compta (pas 3d-2)
            var past = campaigns.Played(campaign.Id, to: now);
            plan = CampaignPlanner.Compute(campaign, all, library.GetTrack, past, now, _zone);
        }
        campaigns.ReplacePlanFrom(campaign.Id, now, plan);
        return plan.Count;
    }

    /// <summary>
    /// La falca que ha de sonar en aquesta emissió (fase 4, pas 3b): la del pla; si no la pot fer sonar (no existeix, s'ha
    /// desactivat, és fora de dates o el fitxer falta), una altra de vàlida de la campanya, i ho diu a <c>Note</c>. Si no en
    /// pot fer sonar cap, <c>Track</c> és null i <c>Note</c> diu per què.
    /// </summary>
    public (Track? Track, string? Note) Choose(long campaignId, Occurrence occurrence)
    {
        var campaign = campaigns.Get(campaignId);
        if (campaign is null) return (null, $"La campanya {campaignId} no existeix: no ha sonat cap falca.");
        string name = $"«{campaign.Name}»";
        var date = occurrence.Date;
        switch (campaign.EffectiveState(date))
        {
            case CampaignState.Draft: return (null, $"La campanya {name} és un esborrany: no ha sonat cap falca.");
            case CampaignState.Finished: return (null, $"La campanya {name} està acabada: no ha sonat cap falca.");
        }
        if (date < campaign.StartDate || date > campaign.EndDate)
            return (null, $"La campanya {name} és fora de les seves dates: no ha sonat cap falca.");

        var planned = campaigns.GetPass(campaignId, occurrence.Event.Id, occurrence.At);
        if (planned is null)
        {
            // El pla no hi és (p. ex. l'esdeveniment s'ha canviat fa un moment): es torna a calcular
            RecalculateAll();
            planned = campaigns.GetPass(campaignId, occurrence.Event.Id, occurrence.At);
        }

        var valid = CampaignPlanner.ValidSpots(campaign, date, library.GetTrack);
        if (planned?.TrackId is { } plannedId && valid.Any(v => v.TrackId == plannedId) && Playable(plannedId) is { } track) return (track, null);

        // Una altra falca vàlida de la campanya, en ordre
        foreach (var spot in valid)
        {
            if (spot.TrackId == planned?.TrackId || Playable(spot.TrackId) is not { } other) continue;
            string why = planned?.TrackId is { } missing ? $"la prevista ({Describe(missing, date)}) no es pot fer sonar" : "el pla no en tenia cap";
            return (other, $"Campanya {name}: sona «{other.Title}» perquè {why}.");
        }
        return (null, valid.Count == 0
            ? $"La campanya {name} no té cap falca vàlida aquest dia: el passi queda buit."
            : $"La campanya {name} no té cap falca que es pugui fer sonar (falten els fitxers): el passi queda buit.");
    }

    /// <summary>La pista, si es pot fer sonar ara: existeix, està activada i el fitxer no falta.</summary>
    private Track? Playable(long trackId) =>
        library.GetTrack(trackId) is { Enabled: true } track && track.FileState != FileState.Missing ? track : null;

    private string Describe(long trackId, DateOnly date) => library.GetTrack(trackId) is { } t
        ? $"«{t.Title}»{(t.FileState == FileState.Missing ? ", el fitxer falta" : !t.Enabled ? ", desactivada" : !t.ValidOn(date) ? ", fora de les seves dates" : ", ja no és vàlida aquest dia")}"
        : $"la pista {trackId}, que ja no existeix";
}
