using System.Globalization;

namespace OnaBages24.Data.RadioDj;

/// <summary>Canvi d'arrel de les rutes (D-025): les que comencen per <see cref="From"/> passen a començar per <see cref="To"/>.</summary>
public sealed record RootChange(string From, string To);

/// <summary>Opcions de la importació.</summary>
public sealed record RadioDjImportOptions
{
    /// <summary>Canvis d'arrel, en ordre: s'aplica el primer que coincideixi.</summary>
    public IReadOnlyList<RootChange> RootChanges { get; init; } = [];

    /// <summary>Comprovar si cada fitxer existeix (si no, la pista queda com a «fitxer que falta»).</summary>
    public bool CheckFiles { get; init; } = true;
}

/// <summary>Resultat de la importació, per a l'informe.</summary>
public sealed class RadioDjImportReport
{
    public int Categories { get; set; }
    public int Subcategories { get; set; }
    public int Tags { get; set; }
    public int TracksAdded { get; set; }
    public int TracksUpdated { get; set; }
    public int FilesMissing { get; set; }
    /// <summary>Pistes que ja hi eren amb el fitxer i que ara, amb la ruta nova, ja no el troben.</summary>
    public int FilesLost { get; set; }
    public List<string> LostExamples { get; } = [];
    public int WithoutCuePoints { get; set; }
    public int WithDates { get; set; }
    public Dictionary<TrackType, int> ByType { get; } = [];
    /// <summary>Pistes que no s'han importat, amb el motiu.</summary>
    public List<string> Skipped { get; } = [];
    /// <summary>Fitxers que falten per carpeta (els tres primers nivells de la ruta), per saber quin canvi d'arrel cal.</summary>
    public Dictionary<string, int> MissingByFolder { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Rotacions importades: nom i nombre de passos.</summary>
    public List<string> Rotations { get; } = [];
    /// <summary>Coses de les rotacions que cal revisar (passos que no s'han pogut importar, tipus de selecció desconeguts…).</summary>
    public List<string> RotationWarnings { get; } = [];

    public override string ToString()
    {
        var lines = new List<string>
        {
            $"Categories: {Categories} · subcategories: {Subcategories} · etiquetes: {Tags}",
            $"Pistes noves: {TracksAdded} · actualitzades: {TracksUpdated} · no importades: {Skipped.Count}",
            $"Fitxers que falten: {FilesMissing} · sense punts d'encadenament: {WithoutCuePoints} · amb dates de vigència: {WithDates}",
            "Per tipus: " + string.Join(", ", ByType.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value}")),
        };
        if (FilesLost > 0)
            lines.Add($"ATENCIÓ: {FilesLost} pistes que abans tenien el fitxer ara no el troben. Potser falta un canvi d'arrel " +
                "(--arrel) o el que s'ha aplicat no és el bo. Si cal, es pot restaurar la còpia de seguretat d'abans d'importar. " +
                "Per exemple:\n  " + string.Join("\n  ", LostExamples));
        if (Rotations.Count > 0) lines.Add("Rotacions: " + string.Join(", ", Rotations));
        if (RotationWarnings.Count > 0) lines.Add("A revisar a les rotacions:\n  " + string.Join("\n  ", RotationWarnings));
        if (Skipped.Count > 0) lines.Add("No importades:\n  " + string.Join("\n  ", Skipped));
        if (MissingByFolder.Count > 0)
            lines.Add("Fitxers que falten, per carpeta:\n  " + string.Join("\n  ",
                MissingByFolder.OrderByDescending(f => f.Value).Select(f => $"{f.Value,5}  {f.Key}")));
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Importa la biblioteca de RadioDJ (categories, subcategories, gèneres, pistes i rotacions) des d'un backup <c>.sql</c>.
/// Es pot tornar a executar: les pistes ja importades (mateix id de RadioDJ) s'actualitzen en lloc de duplicar-se, i les
/// rotacions amb el mateix nom se substitueixen. Els fitxers d'àudio no es copien mai: només se'n desa la ruta.
/// </summary>
public static class RadioDjImporter
{
    /// <summary>Data que RadioDJ fa servir per dir «sense data».</summary>
    private const string NoDate = "2002-01-01";

    /// <summary>Gènere de RadioDJ que no es converteix en etiqueta (D-027).</summary>
    private const string NoGenre = "Other";

    /// <summary>Clau de la configuració on es recorden els canvis d'arrel de l'última importació (D-067).</summary>
    public const string RootChangesSettingKey = "importacio_arrels";

    /// <summary>Els canvis d'arrel recordats d'una importació anterior en aquesta base de dades (llista buida si no n'hi ha).</summary>
    public static IReadOnlyList<RootChange> RememberedRootChanges(Library library)
    {
        if (library.GetSetting(RootChangesSettingKey) is not { } json) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<RootChange>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Recorda els canvis d'arrel per a la pròxima importació (una llista buida els oblida).</summary>
    public static void RememberRootChanges(Library library, IReadOnlyList<RootChange> changes) =>
        library.SetSetting(RootChangesSettingKey, System.Text.Json.JsonSerializer.Serialize(changes));

    /// <param name="rotations">Si no és null, també s'importen les rotacions (taules <c>rotations</c> i <c>rotations_list</c>).</param>
    public static RadioDjImportReport ImportLibrary(MySqlDump dump, Library library, RadioDjImportOptions? options = null, Rotations? rotations = null)
    {
        options ??= new RadioDjImportOptions();
        var report = new RadioDjImportReport();

        // Categories i subcategories: id de RadioDJ → id nostre (i el nom de la categoria, per deduir el tipus)
        var categoryNames = new Dictionary<long, string>();
        foreach (var row in dump.Rows("category"))
        {
            categoryNames[row.Long("ID")] = row.Text("name");
            library.EnsureCategory(row.Text("name"));
            report.Categories++;
        }

        var subcategories = new Dictionary<long, (long Id, string Category)>();
        foreach (var row in dump.Rows("subcategory"))
        {
            string category = categoryNames.GetValueOrDefault(row.Long("parentid"), "Sense categoria");
            long id = library.EnsureSubcategory(library.EnsureCategory(category), row.Text("name"));
            subcategories[row.Long("ID")] = (id, category);
            report.Subcategories++;
        }

        // Gèneres → etiquetes (excepte «Other»)
        var genres = new Dictionary<long, string>();
        foreach (var row in dump.Rows("genre"))
        {
            string name = row.Text("name").Trim();
            if (name.Length == 0 || name.Equals(NoGenre, StringComparison.OrdinalIgnoreCase)) continue;
            genres[row.Long("ID")] = name;
            report.Tags++;
        }

        foreach (var row in dump.Rows("songs"))
        {
            long radioDjId = row.Long("ID");
            string originalPath = row.Text("path");
            try
            {
                var sub = subcategories.TryGetValue(row.Long("id_subcat"), out var s) ? s : ((long Id, string Category)?)null;
                var track = ToTrack(row, sub?.Id, sub?.Category ?? "", options);

                if (track.FileState == FileState.Missing)
                {
                    report.FilesMissing++;
                    string folder = string.Join('\\', track.Path.Split('\\').Take(3));
                    report.MissingByFolder[folder] = report.MissingByFolder.GetValueOrDefault(folder) + 1;
                }
                if (track.MixMs is null) report.WithoutCuePoints++;
                if (track.ValidFrom is not null || track.ValidUntil is not null) report.WithDates++;
                report.ByType[track.Type] = report.ByType.GetValueOrDefault(track.Type) + 1;

                long id;
                if (library.FindByRadioDjId(radioDjId) is { } existing)
                {
                    id = existing.Id;
                    // Abans tenia el fitxer i ara no: segurament falta un canvi d'arrel (o se n'ha posat un d'equivocat)
                    if (existing.FileState == FileState.Ok && track.FileState == FileState.Missing)
                    {
                        report.FilesLost++;
                        if (report.LostExamples.Count < 3) report.LostExamples.Add($"{existing.Path} → {track.Path}");
                    }
                    library.UpdateTrack(track with { Id = id });
                    report.TracksUpdated++;
                }
                else
                {
                    id = library.AddTrack(track);
                    report.TracksAdded++;
                }

                library.SetTags(id, genres.TryGetValue(row.Long("id_genre"), out string? tag) ? [tag] : []);
            }
            catch (DatabaseException ex)
            {
                report.Skipped.Add($"{radioDjId} {originalPath}: {ex.Message}");
            }
        }

        if (rotations is not null) ImportRotations(dump, library, rotations, subcategories, genres, categoryNames, report);
        return report;
    }

    /// <summary>
    /// Rotacions de RadioDJ (D-014, D-027): cada fila de <c>rotations_list</c> és un pas (<c>ord</c> = ordre). El gènere
    /// passa a ser una etiqueta. <c>selType</c>: 0 = la menys reproduïda recentment, 2 = la menys reproduïda (menys vegades),
    /// 5 = aleatòria; qualsevol altre valor es tracta com 0 i s'anota a l'informe. <c>sweeper</c> = 1: trepitjador a l'inici.
    /// Es mantenen en l'ordre dels ids de RadioDJ.
    /// </summary>
    private static void ImportRotations(MySqlDump dump, Library library, Rotations rotations,
        Dictionary<long, (long Id, string Category)> subcategories, Dictionary<long, string> genres,
        Dictionary<long, string> categoryNames, RadioDjImportReport report)
    {
        var steps = dump.Rows("rotations_list").GroupBy(r => r.Long("pID")).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var row in dump.Rows("rotations").OrderBy(r => r.Long("ID")))
        {
            long radioDjId = row.Long("ID");
            string name = row.Text("name");
            var list = new List<RotationStep>();
            foreach (var step in steps.GetValueOrDefault(radioDjId, []).OrderBy(s => s.Long("ord")).ThenBy(s => s.Long("ID")))
            {
                string where = $"«{name}», pas {step.Long("ord") + 1}";
                long? categoryId = categoryNames.TryGetValue(step.Long("catID"), out string? category) ? library.EnsureCategory(category) : null;
                long? subcategoryId = subcategories.TryGetValue(step.Long("subID"), out var sub) ? sub.Id : null;
                if (categoryId is null && subcategoryId is null)
                {
                    report.RotationWarnings.Add($"{where}: la categoria {step.Long("catID")} no existeix; el pas no s'importa");
                    continue;
                }
                long? tagId = null;
                long genre = step.Long("genID");
                if (genre != 0 && genres.TryGetValue(genre, out string? tag)) tagId = library.EnsureTag(tag);

                long selType = step.Long("selType");
                if (selType is not (0 or 2 or 5))
                    report.RotationWarnings.Add($"{where}: tipus de selecció {selType} de RadioDJ desconegut; es fa servir «la menys reproduïda recentment»");

                list.Add(new RotationStep
                {
                    CategoryId = categoryId,
                    SubcategoryId = subcategoryId,
                    TagId = tagId,
                    Selection = selType switch
                    {
                        5 => RotationSelection.Random,
                        2 => RotationSelection.LeastPlayed,
                        _ => RotationSelection.LeastRecent,
                    },
                    RepeatRule = step.Text("repeatRule").Equals("True", StringComparison.OrdinalIgnoreCase),
                    Sweeper = step.Long("sweeper") == 1,
                });
            }
            rotations.Save(name, list, radioDjId);
            report.Rotations.Add($"{name} ({list.Count} passos)");
        }
    }

    /// <summary>Converteix una fila de la taula <c>songs</c> de RadioDJ en una pista.</summary>
    internal static Track ToTrack(DumpRow row, long? subcategoryId, string category, RadioDjImportOptions options)
    {
        string path = ApplyRootChanges(row.Text("path"), options.RootChanges);
        int songType = (int)row.Long("song_type");
        bool isStream = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        var (fileState, size, modified) = (FileState.Ok, (long?)null, (DateTimeOffset?)null);
        if (options.CheckFiles && !isStream)
        {
            var info = new FileInfo(path);
            if (info.Exists) (size, modified) = (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            else fileState = FileState.Missing;
        }

        double durationSeconds = row.Double("duration");
        var cues = ParseCueTimes(row.Text("cue_times"), durationSeconds);
        string year = row.Text("year");

        return new Track
        {
            Path = path,
            Type = isStream ? TrackType.Stream : TypeFor(category, songType, path),
            VariableDuration = songType == 7,
            SubcategoryId = subcategoryId,
            Artist = row.Text("artist"),
            Title = row.Text("title"),
            Album = row.Text("album"),
            Year = year == "1900" ? "" : year,
            DurationMs = durationSeconds > 0 ? (long)Math.Round(durationSeconds * 1000) : null,
            StartMs = cues?.Start,
            MixMs = cues?.Mix,
            EndMs = cues?.End,
            CueSource = cues is null ? null : CueSource.RadioDj,
            ValidFrom = ParseDate(row["start_date"]),
            ValidUntil = ParseDate(row["end_date"]),
            Enabled = row.Long("enabled") == 1,
            FileState = fileState,
            Priority = row.Double("weight", 50),
            PlayCount = row.Long("count_played"),
            LastPlayed = ParseLocalInstant(row["date_played"]),
            FileSize = size,
            FileModified = modified,
            RadioDjId = row.Long("ID"),
        };
    }

    /// <summary>
    /// Tipus de pista a partir de la categoria de RadioDJ (D-023, D-026). A «Station IDs» hi ha també les
    /// locucions de l'hora (<c>hores\NN.mp3</c>) i els senyals horaris (<c>hores\SENYALS HORARIS.mp3</c>).
    /// </summary>
    internal static TrackType TypeFor(string category, int songType, string path)
    {
        string folder = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        if (folder.Equals("hores", StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return name.Length == 2 && name.All(char.IsDigit) ? TrackType.TimeAnnouncement : TrackType.TimeSignal;
        }

        return category.ToLowerInvariant() switch
        {
            "music" => TrackType.Music,
            "sweepers" => TrackType.Sweeper,
            "station ids" or "jingles" => TrackType.StationId,
            "promos" => TrackType.Promo,
            "commercials" => TrackType.Commercial,
            "radio shows" or "interviews" or "capsules" => TrackType.Program,
            "news" => TrackType.News,
            "radio streams" => TrackType.Stream,
            _ => songType == 0 ? TrackType.Music : TrackType.Other,
        };
    }

    /// <summary>Aplica el primer canvi d'arrel que coincideixi (sense distingir majúscules, com Windows).</summary>
    public static string ApplyRootChanges(string path, IReadOnlyList<RootChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.From.Length > 0 && path.StartsWith(change.From, StringComparison.OrdinalIgnoreCase))
                return change.To + path[change.From.Length..];
        }
        return path;
    }

    /// <summary>
    /// Llegeix el camp <c>cue_times</c> de RadioDJ (p. ex. <c>&amp;sta=0.03&amp;xta=19.56&amp;end=19.64&amp;fin=0&amp;fou=0.08</c>),
    /// en segons. Retorna inici, mescla i final en mil·lisegons, o null si no hi ha punts vàlids.
    /// </summary>
    internal static (long Start, long Mix, long End)? ParseCueTimes(string cueTimes, double durationSeconds)
    {
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in cueTimes.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && double.TryParse(part[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values[part[..eq]] = v;
        }
        if (!values.ContainsKey("sta") && !values.ContainsKey("xta") && !values.ContainsKey("end")) return null;

        double start = values.GetValueOrDefault("sta", 0);
        double end = values.TryGetValue("end", out double e) && e > 0 ? e : durationSeconds;
        double mix = values.TryGetValue("xta", out double x) && x > 0 ? x : end;
        if (end <= 0 || start < 0 || start > mix || mix > end) return null; // punts incoherents: s'analitzaran de nou

        return ((long)Math.Round(start * 1000), (long)Math.Round(mix * 1000), (long)Math.Round(end * 1000));
    }

    private static DateOnly? ParseDate(string? text) =>
        text is null || text.StartsWith(NoDate, StringComparison.Ordinal) || text.Length < 10
            ? null
            : DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>RadioDJ desa les hores en hora local de l'ordinador; les passem a UTC.</summary>
    private static DateTimeOffset? ParseLocalInstant(string? text)
    {
        if (text is null || text.StartsWith(NoDate, StringComparison.Ordinal)) return null;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
    }
}
