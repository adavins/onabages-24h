using OnaBages24.Data;

namespace OnaBages24.Engine.LibraryTools;

/// <summary>Una pista en un canvi d'arrel: la ruta nova, si el fitxer hi és i si xoca amb una altra pista.</summary>
/// <param name="Conflict">La ruta nova ja és d'una altra pista (o d'una altra pista del mateix canvi): no es tocarà.</param>
public sealed record RerootItem(Track Track, string NewPath, bool Exists, bool Conflict);

/// <summary>Què faria un canvi d'arrel (fase 3, pas 7d; D-092). No canvia res.</summary>
public sealed record RerootPlan(string From, string To, IReadOnlyList<RerootItem> Items)
{
    public int Found => Items.Count(i => !i.Conflict && i.Exists);
    public int NotFound => Items.Count(i => !i.Conflict && !i.Exists);
    public int Conflicts => Items.Count(i => i.Conflict);
}

/// <summary>Resultat d'aplicar un canvi d'arrel.</summary>
/// <param name="Changed">Pistes amb la ruta nova.</param>
/// <param name="Found">D'aquestes, quantes tenen el fitxer.</param>
/// <param name="Skipped">Les que no s'han tocat (xoquen amb una altra pista, o el fitxer no hi és i només es volien les que el troben).</param>
/// <param name="Folders">Carpetes vigilades que s'han passat a l'arrel nova.</param>
public sealed record RerootResult(int Changed, int Found, int Skipped, int Folders);

/// <summary>
/// Canvi d'arrel de les rutes (fase 3, pas 7d; D-025, D-092): canviar el principi de les rutes de moltes pistes alhora,
/// p. ex. de <c>G:\.shortcut-targets-by-id\0B7l…\</c> (com es veu Google Drive a l'ordinador de RadioDJ) a
/// <c>I:\Mi unidad\</c> (com es veu en aquest). Els fitxers no es toquen mai: només les rutes de la biblioteca.
/// </summary>
public static class PathReroot
{
    /// <summary>La carpeta amb una «\» al final (perquè «O:\musica» no coincideixi amb «O:\musica2»).</summary>
    public static string Folder(string path) => path.TrimEnd('\\', '/') + "\\";

    /// <summary>Les carpetes on hi ha pistes amb el fitxer que falta, amb quantes, les que més primer (per triar l'arrel).</summary>
    public static IReadOnlyList<(string Folder, int Count)> MissingFolders(Library library, int limit) =>
        library.MissingPaths()
            .GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Folder, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

    /// <summary>Què passaria canviant l'arrel <paramref name="from"/> per <paramref name="to"/>. Mira si cada fitxer hi és.</summary>
    public static RerootPlan Plan(Library library, string from, string to)
    {
        string prefix = Folder(from), target = Folder(to);
        var items = new List<RerootItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in library.TracksInFolder(prefix))
        {
            string newPath = target + track.Path[prefix.Length..];
            bool conflict = !used.Add(newPath) || (library.FindByPath(newPath) is { } other && other.Id != track.Id);
            items.Add(new RerootItem(track, newPath, File.Exists(newPath), conflict));
        }
        return new RerootPlan(prefix, target, items);
    }

    /// <summary>
    /// Aplica el canvi d'arrel, tot en una transacció. Amb <paramref name="onlyFound"/>, només les pistes que trobaran el fitxer
    /// (les altres es queden com eren). Les carpetes vigilades dins de l'arrel antiga passen a la nova, si la carpeta nova existeix.
    /// </summary>
    public static RerootResult Apply(Library library, RerootPlan plan, bool onlyFound)
    {
        var changes = new List<Library.PathChange>();
        foreach (var item in plan.Items)
        {
            if (item.Conflict || (onlyFound && !item.Exists)) continue;
            if (string.Equals(item.Track.Path, item.NewPath, StringComparison.Ordinal)) continue;
            long? size = null;
            DateTimeOffset? modified = null;
            if (item.Exists)
            {
                try
                {
                    var info = new FileInfo(item.NewPath);
                    size = info.Length;
                    modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            changes.Add(new Library.PathChange(item.Track.Id, item.NewPath, item.Exists, size, modified));
        }
        library.ChangePaths(changes);

        int folders = 0;
        foreach (var watched in library.WatchedFolders())
        {
            string folder = Folder(watched.Folder!);
            if (!folder.StartsWith(plan.From, StringComparison.OrdinalIgnoreCase)) continue;
            string moved = (plan.To + folder[plan.From.Length..]).TrimEnd('\\');
            if (!Directory.Exists(moved)) continue;
            library.SetFolderSettings(watched.SubcategoryId, moved, watched.Renewable, watched.DefaultType, watched.DefaultTags, watched.DefaultVariableDuration);
            folders++;
        }
        return new RerootResult(changes.Count, changes.Count(c => c.Exists), plan.Items.Count - changes.Count, folders);
    }
}
