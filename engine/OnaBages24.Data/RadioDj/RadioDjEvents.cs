using System.Globalization;
using System.Text.Json;

namespace OnaBages24.Data.RadioDj;

/// <summary>Opcions de la migració dels esdeveniments.</summary>
public sealed record RadioDjEventsOptions
{
    /// <summary>
    /// Tots els esdeveniments s'importen desactivats (no es disparen): per a un ordinador on no hi ha els fitxers dels
    /// programes, perquè no faci talls a les hores en punt amb fitxers que no hi són.
    /// </summary>
    public bool AllDisabled { get; init; }
}

/// <summary>Resultat de la migració dels esdeveniments, per a l'informe.</summary>
public sealed class RadioDjEventsReport
{
    /// <summary>Franges de RadioDJ amb contingut (les buides no es migren).</summary>
    public int Slots { get; set; }
    public int Created { get; set; }
    /// <summary>Esdeveniments d'una importació anterior que s'han substituït.</summary>
    public int Replaced { get; set; }
    public Dictionary<EventMode, int> ByMode { get; } = [];
    /// <summary>Esdeveniments que agrupen diverses franges (dies diferents amb el mateix contingut).</summary>
    public List<string> Groups { get; } = [];
    /// <summary>Hores amb continguts diferents segons el dia (per revisar-les: no s'agrupen).</summary>
    public List<string> NearlySame { get; } = [];
    /// <summary>Durades màximes calculades amb les franges desactivades que segueixen un TALLA.</summary>
    public List<string> Durations { get; } = [];
    /// <summary>Pistes de RadioDJ que no són a la biblioteca (l'acció no s'importa).</summary>
    public SortedDictionary<long, string> MissingTracks { get; } = [];
    /// <summary>Pistes que sí que són a la biblioteca però el fitxer no hi és en aquest ordinador.</summary>
    public SortedSet<string> MissingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Coses que no s'han pogut migrar o que cal revisar.</summary>
    public List<string> Warnings { get; } = [];
    public bool AllDisabled { get; set; }

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Esdeveniments: {Created} creats a partir de {Slots} franges de RadioDJ amb contingut" +
                (Replaced > 0 ? $" ({Replaced} d'una importació anterior substituïts)" : ""),
            "Per mode: " + string.Join(", ", ByMode.OrderByDescending(m => m.Value).Select(m => $"{Events.ModeText(m.Key)} {m.Value}")),
        };
        if (AllDisabled) lines.Add("Tots desactivats (--esdeveniments-desactivats): no es dispararan fins que s'activin.");
        if (Groups.Count > 0) lines.Add("Agrupats (mateix contingut en dies diferents):\n  " + string.Join("\n  ", Groups));
        if (NearlySame.Count > 0) lines.Add("Hores amb contingut diferent segons el dia (no s'agrupen; per revisar):\n  " + string.Join("\n  ", NearlySame));
        if (Durations.Count > 0) lines.Add("Durada màxima dels TALLA (franges desactivades que els segueixen):\n  " + string.Join("\n  ", Durations));
        if (MissingTracks.Count > 0)
            lines.Add($"Pistes de RadioDJ que no són a la biblioteca ({MissingTracks.Count}; l'acció no s'importa):\n  " +
                string.Join("\n  ", MissingTracks.Select(t => $"{t.Key}  {t.Value}")));
        if (MissingFiles.Count > 0)
            lines.Add($"Pistes dels esdeveniments amb el fitxer que falta en aquest ordinador ({MissingFiles.Count}):\n  " + string.Join("\n  ", MissingFiles));
        if (Warnings.Count > 0) lines.Add("A revisar:\n  " + string.Join("\n  ", Warnings));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Migració dels esdeveniments de RadioDJ (docs/planificador.md, secció 6; D-066). La biblioteca s'ha d'haver importat
/// abans: les pistes es busquen pel seu id de RadioDJ.
///
/// - Taula <c>events</c>: <c>type</c> 1 = franja setmanal (<c>day</c> = <c>&amp;1</c>…<c>&amp;6</c>, <c>&amp;0</c> = diumenge);
///   2 = amb dies i hores (passa a «cada hora»); 4 = el que arrenca el servidor d'streaming (no es migra).
/// - <c>data</c>: ordres separades per salts de línia. El mode es dedueix de les ordres: <c>Play!</c> = TALLA; pistes sense
///   <c>Play!</c> = ENCUA; només senyals pel reproductor auxiliar = SUPERPOSA; si no, SISTEMA.
///   <c>Clear Playlist!</c> = «Esborra la cua». Es treuen <c>Assisted Enable!</c>/<c>Disable!</c> i <c>Stop!</c>.
/// - Les pistes es carreguen amb <c>…|Top</c> (al principi de la cua): l'ordre escrit és l'invers del real.
/// - Les franges del mateix contingut a la mateixa hora s'agrupen en un esdeveniment amb els seus dies.
/// - Durada màxima d'un TALLA: les franges desactivades i buides que el segueixen el mateix dia (D-028).
/// - Es pot tornar a executar: els esdeveniments d'una importació anterior (marcats a les notes) se substitueixen.
/// </summary>
public static class RadioDjEvents
{
    /// <summary>Principi de les notes dels esdeveniments importats: així es reconeixen en tornar a importar.</summary>
    public const string NotesMarker = "Importat de RadioDJ";

    private const string ClearPlaylist = "Clear Playlist!";
    private const string Play = "Play!";

    /// <summary>Una franja de RadioDJ ja interpretada.</summary>
    private sealed record Slot(long RadioDjId, int Type, TimeOnly Time, int DaysMask, int? HoursMask, bool Enabled, string Data);

    /// <summary>El contingut d'una franja convertit: el que es compara per agrupar.</summary>
    private sealed record Content(EventMode Mode, bool ClearQueue, IReadOnlyList<EventAction> Actions, string Name, string Key);

    public static RadioDjEventsReport Import(MySqlDump dump, Library library, Rotations rotations, Events events, RadioDjEventsOptions? options = null)
    {
        options ??= new RadioDjEventsOptions();
        var report = new RadioDjEventsReport { AllDisabled = options.AllDisabled };
        var context = new Context(dump, library, rotations, report);

        var slots = new List<Slot>();
        foreach (var row in dump.Rows("events"))
        {
            int type = (int)row.Long("type");
            string name = row.Text("name");
            if (type == 4)
            {
                report.Warnings.Add($"«{name}» (arrencar el servidor d'streaming) no es migra.");
                continue;
            }
            if (type is not (1 or 2))
            {
                report.Warnings.Add($"«{name}»: tipus d'esdeveniment {type} de RadioDJ desconegut; no es migra.");
                continue;
            }
            if (!TimeOnly.TryParse(row.Text("time"), CultureInfo.InvariantCulture, out var time))
            {
                report.Warnings.Add($"«{name}»: hora no vàlida ({row.Text("time")}); no es migra.");
                continue;
            }
            int days = DaysMask(row.Text("day"));
            int? hours = type == 2 ? HoursMask(row.Text("hours")) : null;
            slots.Add(new Slot(row.Long("ID"), type, time, days, hours,
                row.Text("enabled").Equals("True", StringComparison.OrdinalIgnoreCase), row.Text("data").Trim()));
        }

        // Els esdeveniments d'una importació anterior se substitueixen (els fets a mà no es toquen)
        foreach (var old in events.List().Where(e => e.Notes?.StartsWith(NotesMarker, StringComparison.Ordinal) == true))
        {
            events.Delete(old.Id);
            report.Replaced++;
        }

        var withContent = slots.Where(s => s.Data.Length > 0).ToList();
        report.Slots = withContent.Count;

        // Franges setmanals: s'agrupen per hora, contingut i si estan actives
        var weekly = withContent.Where(s => s.Type == 1)
            .Select(s => (Slot: s, Content: context.Convert(s)))
            .GroupBy(x => (x.Slot.Time, x.Slot.Enabled, x.Content.Key))
            .OrderBy(g => g.Key.Time).ThenBy(g => DaysOrder(g.Select(x => x.Slot.DaysMask).Aggregate(0, (a, b) => a | b)));

        foreach (var group in weekly)
        {
            var first = group.First();
            int days = group.Aggregate(0, (mask, x) => mask | x.Slot.DaysMask);
            if (days == 0)
            {
                report.Warnings.Add($"Franja {first.Slot.RadioDjId} ({first.Slot.Time:HH\\:mm}) sense cap dia; no es migra.");
                continue;
            }

            // Durada màxima: la més llarga de les franges del grup (normalment, totes iguals)
            int? maxDuration = null;
            if (first.Content.Mode == EventMode.Cut)
            {
                var durations = group.Select(x => FollowingDisabled(slots, x.Slot)).Distinct().ToList();
                maxDuration = durations.Max();
                if (maxDuration is { } d)
                    report.Durations.Add($"{first.Slot.Time:HH\\:mm} {DaysText(days)} «{first.Content.Name}»: {FormatDuration(d)}" +
                        (durations.Count > 1 ? " (no és igual tots els dies: es pren la més llarga)" : ""));
            }

            var actions = WithStreamDuration(first.Content.Actions, maxDuration, first.Slot, report);
            Save(events, report, options, new ScheduledEvent
            {
                Name = first.Content.Name,
                Mode = first.Content.Mode,
                StartTime = first.Slot.Time,
                DaysMask = days,
                ClearQueue = first.Content.ClearQueue,
                MaxDurationSeconds = maxDuration,
                Enabled = first.Slot.Enabled,
                Actions = actions,
                Notes = $"{NotesMarker} (franges {string.Join(", ", group.Select(x => x.Slot.RadioDjId).Order())}).",
            });
            if (group.Count() > 1)
                report.Groups.Add($"{first.Slot.Time:HH\\:mm} {DaysText(days)}: «{first.Content.Name}» ({group.Count()} franges)");
        }

        // Hores amb continguts diferents segons el dia (actives): per revisar
        foreach (var byTime in withContent.Where(s => s.Type == 1 && s.Enabled).GroupBy(s => s.Time).OrderBy(g => g.Key))
        {
            var variants = byTime.Select(s => (Slot: s, Content: context.Convert(s))).GroupBy(x => x.Content.Key).ToList();
            if (variants.Count < 2) continue;
            report.NearlySame.Add($"{byTime.Key:HH\\:mm}: " + string.Join(" · ", variants
                .OrderBy(v => DaysOrder(v.Aggregate(0, (m, x) => m | x.Slot.DaysMask)))
                .Select(v => $"{DaysText(v.Aggregate(0, (m, x) => m | x.Slot.DaysMask))} {Events.ModeText(v.First().Content.Mode)} «{v.First().Content.Name}»")));
        }

        // Dies i hores (tipus 2): «cada hora»
        foreach (var slot in withContent.Where(s => s.Type == 2))
        {
            var content = context.Convert(slot);
            Save(events, report, options, new ScheduledEvent
            {
                Name = content.Name,
                Mode = content.Mode,
                StartTime = new TimeOnly(0, slot.Time.Minute, slot.Time.Second),
                DaysMask = slot.DaysMask == 0 ? Weekdays.All : slot.DaysMask,
                Repeat = EventRepeat.EveryHour,
                HoursMask = slot.HoursMask,
                ClearQueue = content.ClearQueue,
                Enabled = slot.Enabled,
                Actions = content.Actions,
                Notes = $"{NotesMarker} (esdeveniment {slot.RadioDjId}, cada hora).",
            });
        }
        return report;
    }

    private static void Save(Events events, RadioDjEventsReport report, RadioDjEventsOptions options, ScheduledEvent ev)
    {
        if (options.AllDisabled) ev = ev with { Enabled = false };
        try
        {
            events.Save(ev);
            report.Created++;
            report.ByMode[ev.Mode] = report.ByMode.GetValueOrDefault(ev.Mode) + 1;
        }
        catch (Exception ex) when (ex is ArgumentException or DatabaseException)
        {
            report.Warnings.Add($"«{ev.Name}» ({ev.StartTime:HH\\:mm}): no s'ha pogut desar ({ex.Message}).");
        }
    }

    /// <summary>Converteix les ordres de RadioDJ d'una franja en mode, «Esborra la cua», accions i nom.</summary>
    private sealed class Context(MySqlDump dump, Library library, Rotations rotations, RadioDjEventsReport report)
    {
        private readonly Dictionary<long, Track?> _tracks = [];
        private readonly Dictionary<long, string> _subcategoryNames = dump.Rows("subcategory").ToDictionary(r => r.Long("ID"), r => r.Text("name"));
        private readonly Dictionary<long, string> _categoryNames = dump.Rows("category").ToDictionary(r => r.Long("ID"), r => r.Text("name"));
        private readonly Dictionary<long, long> _subcategoryParents = dump.Rows("subcategory").ToDictionary(r => r.Long("ID"), r => r.Long("parentid"));
        private readonly Dictionary<long, Content> _cache = [];

        public Content Convert(Slot slot)
        {
            if (_cache.TryGetValue(slot.RadioDjId, out var cached)) return cached;
            string where = $"franja {slot.RadioDjId} ({slot.Time:HH\\:mm} {DaysText(slot.DaysMask)})";
            var commands = slot.Data.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            bool play = commands.Contains(Play);
            bool clear = commands.Contains(ClearPlaylist);
            var aux = new List<EventAction>();
            var tracksTop = new List<EventAction>();   // en l'ordre escrit (s'inverteixen al final)
            var tracksEnd = new List<EventAction>();
            var others = new List<EventAction>();
            var titles = new List<string>();      // del contingut, per al nom (en l'ordre escrit)
            var auxTitles = new List<string>();
            var rotationNames = new List<string>();

            foreach (string command in commands)
            {
                var parts = command.Split('|');
                switch (parts[0])
                {
                    case Play:
                    case ClearPlaylist:
                    case "Assisted Enable!":
                    case "Assisted Disable!":
                    case "Stop!":
                        break; // recollits al mode i a «Esborra la cua», o ja no calen (D-022)

                    case "Load Track By ID" when parts.Length >= 3 && long.TryParse(parts[2], out long radioDjId):
                    {
                        string label = parts.Length >= 4 ? parts[3] : radioDjId.ToString(CultureInfo.InvariantCulture);
                        bool top = parts.Length >= 5 && parts[4].Equals("Top", StringComparison.OrdinalIgnoreCase);
                        var action = TrackAction(radioDjId, label, where, titles);
                        if (action is not null) (top ? tracksTop : tracksEnd).Add(action);
                        break;
                    }

                    case "PluginAction" when parts.Length >= 4 && parts[1] == "Auxiliary Players" && parts[2] == "Play File From Database":
                    {
                        // 42;senyals horaris.mp3;1;Top → pista 42, reproductor 1
                        var args = parts[3].Split(';');
                        if (!long.TryParse(args[0], out long radioDjId))
                        {
                            report.Warnings.Add($"{where}: ordre del reproductor auxiliar sense pista ({command}).");
                            break;
                        }
                        int player = args.Length >= 3 && int.TryParse(args[2], out int p) && p is >= 1 and <= 3 ? p : 1;
                        if (Track(radioDjId, args.Length >= 2 ? args[1] : "") is { } track)
                        {
                            aux.Add(new EventAction { Type = EventActionType.Auxiliary, Params = Json(new { songId = track.Id, player }) });
                            auxTitles.Add(TitleOf(track));
                        }
                        break;
                    }

                    case "Load Rotation" when parts.Length >= 3:
                    {
                        string name = parts[2];
                        var rotation = rotations.List().FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                        if (rotation is null) report.Warnings.Add($"{where}: la rotació «{name}» no existeix; l'acció no s'importa.");
                        else
                        {
                            others.Add(new EventAction { Type = EventActionType.Rotation, Params = Json(new { rotacioId = rotation.Id }) });
                            rotationNames.Add(rotation.Name);
                        }
                        break;
                    }

                    case "Set Sweeper Category" when parts.Length >= 2 && long.TryParse(parts[1], out long subId):
                    {
                        if (!_subcategoryNames.TryGetValue(subId, out string? subName))
                        {
                            report.Warnings.Add($"{where}: la subcategoria {subId} de RadioDJ no existeix; l'acció SWEEPERS no s'importa.");
                            break;
                        }
                        string category = _categoryNames.GetValueOrDefault(_subcategoryParents.GetValueOrDefault(subId), "Sense categoria");
                        long id = library.EnsureSubcategory(library.EnsureCategory(category), subName);
                        others.Add(new EventAction { Type = EventActionType.Sweepers, Params = Json(new { subcategoriaId = id }) });
                        break;
                    }

                    default:
                        report.Warnings.Add($"{where}: ordre de RadioDJ que no es migra: {command}");
                        break;
                }
            }

            // Ordre real d'emissió: les carregades «al principi» (Top), al revés; les del final, darrere
            tracksTop.Reverse();
            var contentTracks = tracksTop.Concat(tracksEnd).ToList();
            var actions = aux.Concat(contentTracks).Concat(others).ToList();

            // El mode surt de les ordres de RadioDJ, no de les pistes trobades: un ENCUA amb pistes que falten continua sent un ENCUA
            bool loadsTracks = commands.Any(c => c.StartsWith("Load Track By ID", StringComparison.Ordinal));
            bool playsAux = commands.Any(c => c.StartsWith("PluginAction|Auxiliary Players", StringComparison.Ordinal));
            var mode = play ? EventMode.Cut
                : loadsTracks ? EventMode.Queue
                : playsAux ? EventMode.Overlay
                : EventMode.System;

            titles.Reverse(); // també en ordre d'emissió
            string eventName = Name(mode, titles.Distinct(StringComparer.CurrentCultureIgnoreCase).ToList(), auxTitles, rotationNames);
            // Per agrupar es comparen les ordres de RadioDJ (sense les que no es migren), no les accions convertides: si
            // en dues franges falten pistes de la biblioteca, no han de semblar iguals
            string key = string.Join("|", commands.Where(c => c is not ("Assisted Enable!" or "Assisted Disable!" or "Stop!")));
            return _cache[slot.RadioDjId] = new Content(mode, clear, actions, eventName, key);
        }

        /// <summary>Una pista carregada: PISTA, o STREAM si és una adreça d'internet.</summary>
        private EventAction? TrackAction(long radioDjId, string label, string where, List<string> titles)
        {
            if (Track(radioDjId, label) is not { } track)
            {
                // Per al nom, encara que no sigui a la biblioteca (excepte la locució de l'hora: «13.mp3»)
                string file = Path.GetFileNameWithoutExtension(label);
                if (!(file.Length == 2 && file.All(char.IsAsciiDigit))) titles.Add(file);
                return null;
            }
            if (track.Type == TrackType.Stream || track.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                titles.Add(string.IsNullOrWhiteSpace(track.Title) ? "Stream" : track.Title);
                // La durada s'hi posa després, amb la durada màxima del TALLA (WithStreamDuration)
                return new EventAction { Type = EventActionType.Stream, Params = Json(new { url = track.Path }) };
            }
            if (track.Type != TrackType.TimeAnnouncement) titles.Add(TitleOf(track));
            return new EventAction { Type = EventActionType.Track, Params = Json(new { songId = track.Id }) };
        }

        private Track? Track(long radioDjId, string label)
        {
            if (!_tracks.TryGetValue(radioDjId, out var track))
            {
                _tracks[radioDjId] = track = library.FindByRadioDjId(radioDjId);
                if (track is null) report.MissingTracks[radioDjId] = label;
                else if (!track.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(track.Path))
                    report.MissingFiles.Add(track.Path); // es mira ara al disc: l'estat desat a la biblioteca pot ser antic
            }
            return track;
        }
    }

    /// <summary>Nom a partir del contingut (D-066): els noms de RadioDJ («DL 10:00h») no tenen sentit un cop agrupats els dies.</summary>
    private static string Name(EventMode mode, List<string> titles, List<string> auxTitles, List<string> rotationNames)
    {
        string signals = auxTitles.Count > 0 ? "Senyals horaris" : "";
        string content = titles.Count switch
        {
            0 => "",
            <= 3 => string.Join(" + ", titles),
            _ => string.Join(" + ", titles.Take(3)) + " …",
        };
        string name = (signals, content) switch
        {
            ("", "") => mode switch
            {
                EventMode.Cut => "Locució de l'hora",
                _ when rotationNames.Count > 0 => "Canvi de rotació",
                _ => "Esdeveniment de RadioDJ",
            },
            ("", _) => content,
            (_, "") => signals,
            _ => $"{signals} + {content}",
        };
        // La rotació distingeix franges que, si no, es dirien igual (p. ex. «INICI» amb Dance90 o Elektro90)
        if (rotationNames.Count > 0 && name != "Canvi de rotació") name += " · rotació " + string.Join(", ", rotationNames);
        else if (rotationNames.Count > 0) name += " " + string.Join(", ", rotationNames);
        return name.Length > 120 ? name[..119] + "…" : name;
    }

    private static string TitleOf(Track track) =>
        !string.IsNullOrWhiteSpace(track.Title) ? track.Title.Trim() : Path.GetFileNameWithoutExtension(track.Path);

    /// <summary>Els STREAM porten la durada: la màxima del TALLA (si no se sap, queda per posar a mà, amb avís).</summary>
    private static List<EventAction> WithStreamDuration(IReadOnlyList<EventAction> actions, int? seconds, Slot slot, RadioDjEventsReport report)
    {
        var result = new List<EventAction>();
        foreach (var action in actions)
        {
            if (action.Type != EventActionType.Stream)
            {
                result.Add(action);
                continue;
            }
            using var json = JsonDocument.Parse(action.Params);
            string url = json.RootElement.GetProperty("url").GetString() ?? "";
            if (seconds is { } s)
                result.Add(action with { Params = Json(new { url, durada_seg = s }) });
            else
            {
                report.Warnings.Add($"Stream de les {slot.Time:HH\\:mm} ({DaysText(slot.DaysMask)}): no se'n sap la durada; cal posar-la a mà.");
                result.Add(action);
            }
        }
        return result;
    }

    /// <summary>
    /// Durada màxima d'un TALLA (D-028): fins a la primera franja del mateix dia que no sigui buida i desactivada.
    /// Null si just després ja n'hi ha una altra.
    /// </summary>
    private static int? FollowingDisabled(List<Slot> slots, Slot cut)
    {
        var sameDay = slots.Where(s => s.Type == 1 && s.DaysMask == cut.DaysMask && s.Time > cut.Time).OrderBy(s => s.Time).ToList();
        if (sameDay.Count == 0 || sameDay[0].Data.Length > 0 || sameDay[0].Enabled) return null;
        var next = sameDay.FirstOrDefault(s => s.Data.Length > 0 || s.Enabled);
        var end = next?.Time.ToTimeSpan() ?? TimeSpan.FromDays(1); // si no n'hi ha cap més, fins a mitjanit
        return (int)(end - cut.Time.ToTimeSpan()).TotalSeconds;
    }

    /// <summary><c>&amp;1</c>…<c>&amp;6</c> = dilluns–dissabte, <c>&amp;0</c> = diumenge (es poden ajuntar: <c>&amp;1&amp;2</c>).</summary>
    private static int DaysMask(string text)
    {
        int mask = 0;
        foreach (string part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int d) && d is >= 0 and <= 6)
                mask |= Weekdays.Bit(d == 0 ? DayOfWeek.Sunday : (DayOfWeek)d);
        }
        return mask;
    }

    /// <summary><c>&amp;0</c>…<c>&amp;23</c> = hores; totes = null.</summary>
    private static int? HoursMask(string text)
    {
        int mask = 0;
        foreach (string part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, out int h) && h is >= 0 and <= 23) mask |= 1 << h;
        }
        return mask is 0 or 0xFF_FFFF ? null : mask;
    }

    private static readonly string[] DayNames = ["dl", "dt", "dc", "dj", "dv", "ds", "dg"];

    /// <summary>Dies en text curt: «dl-dv», «dl, dc, dv», «cada dia».</summary>
    public static string DaysText(int mask)
    {
        if (mask == Weekdays.All) return "cada dia";
        var days = Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).ToList();
        if (days.Count == 0) return "cap dia";
        bool consecutive = days.Count >= 3 && days[^1] - days[0] == days.Count - 1;
        return consecutive ? $"{DayNames[days[0]]}-{DayNames[days[^1]]}" : string.Join(", ", days.Select(d => DayNames[d]));
    }

    /// <summary>Per ordenar els grups d'una mateixa hora: primer el que comença abans a la setmana.</summary>
    private static int DaysOrder(int mask) => mask == 0 ? 7 : System.Numerics.BitOperations.TrailingZeroCount(mask);

    private static string FormatDuration(int seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min";
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
