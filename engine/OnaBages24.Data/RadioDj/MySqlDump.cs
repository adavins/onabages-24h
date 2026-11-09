using System.Text;

namespace OnaBages24.Data.RadioDj;

/// <summary>Una fila d'una taula del backup, amb els valors per nom de columna (null = NULL de MySQL).</summary>
public sealed class DumpRow(IReadOnlyDictionary<string, int> columns, string?[] values)
{
    public string? this[string column] =>
        columns.TryGetValue(column, out int i) && i < values.Length ? values[i] : null;

    public string Text(string column) => this[column] ?? "";

    public long Long(string column, long fallback = 0) =>
        long.TryParse(this[column], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) ? v : fallback;

    public double Double(string column, double fallback = 0) =>
        double.TryParse(this[column], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
}

/// <summary>
/// Llegeix un backup de MySQL (fitxer <c>.sql</c> fet amb mysqldump), sense necessitat d'instal·lar MySQL.
/// Només entén el que fa servir RadioDJ: <c>CREATE TABLE</c> (per saber el nom de les columnes) i
/// <c>INSERT INTO `taula` VALUES (…),(…);</c>. Es poden llegir diversos fitxers alhora: per exemple,
/// l'esquema en un i les dades en un altre (com a <c>docs/radiodj/</c>).
/// </summary>
public sealed class MySqlDump
{
    private readonly Dictionary<string, Dictionary<string, int>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string?[]>> _rows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Llegeix un o diversos fitxers (en UTF-8, com els fa RadioDJ).</summary>
    public static MySqlDump Load(params string[] paths)
    {
        var dump = new MySqlDump();
        foreach (string path in paths)
            dump.Parse(File.ReadAllText(path, Encoding.UTF8));
        return dump;
    }

    /// <summary>Llegeix el text d'un backup (per a les proves).</summary>
    public static MySqlDump FromText(string text)
    {
        var dump = new MySqlDump();
        dump.Parse(text);
        return dump;
    }

    /// <summary>Les files d'una taula. Si la taula no hi és, cap.</summary>
    /// <exception cref="InvalidDataException">Si hi ha dades però no l'estructura de la taula.</exception>
    public IReadOnlyList<DumpRow> Rows(string table)
    {
        if (!_rows.TryGetValue(table, out var rows)) return [];
        if (!_columns.TryGetValue(table, out var columns))
            throw new InvalidDataException($"Hi ha dades de la taula «{table}», però no la seva estructura (CREATE TABLE).");
        return rows.Select(values => new DumpRow(columns, values)).ToList();
    }

    private void Parse(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (Matches(text, i, "CREATE TABLE `")) i = ParseCreate(text, i + "CREATE TABLE `".Length);
            else if (Matches(text, i, "INSERT INTO `")) i = ParseInsert(text, i + "INSERT INTO `".Length);
            else if (Matches(text, i, "--")) i = SkipLine(text, i);
            else i++;
        }
    }

    /// <summary>Llegeix els noms de columna d'un CREATE TABLE (les línies que comencen per un nom entre accents greus).</summary>
    private int ParseCreate(string text, int i)
    {
        int nameEnd = text.IndexOf('`', i);
        string table = text[i..nameEnd];
        int bodyEnd = text.IndexOf(';', nameEnd);
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in text[(nameEnd + 1)..bodyEnd].Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('`')) continue; // PRIMARY KEY, KEY, ENGINE…
            string column = line[1..line.IndexOf('`', 1)];
            columns[column] = columns.Count;
        }
        _columns[table] = columns;
        return bodyEnd + 1;
    }

    private int ParseInsert(string text, int i)
    {
        int nameEnd = text.IndexOf('`', i);
        string table = text[i..nameEnd];
        if (!_rows.TryGetValue(table, out var rows)) _rows[table] = rows = [];

        i = text.IndexOf("VALUES", nameEnd, StringComparison.Ordinal) + "VALUES".Length;
        var values = new List<string?>();
        var value = new StringBuilder();
        bool inRow = false;
        while (i < text.Length)
        {
            char c = text[i];
            if (!inRow)
            {
                if (c == ';') return i + 1;
                if (c == '(') { inRow = true; values.Clear(); }
                i++;
                continue;
            }

            if (c == '\'')
            {
                // Text entre cometes, amb les seqüències d'escapament de MySQL
                value.Clear();
                i++;
                while (text[i] != '\'')
                {
                    if (text[i] == '\\')
                    {
                        char next = text[i + 1];
                        value.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '0' => '\0', 'Z' => '\x1A', _ => next });
                        i += 2;
                    }
                    else value.Append(text[i++]);
                }
                values.Add(value.ToString());
                i++;
            }
            else if (c == ',' || char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == ')')
            {
                rows.Add(values.ToArray());
                inRow = false;
                i++;
            }
            else
            {
                // Número o NULL, fins a la coma o el parèntesi
                int start = i;
                while (text[i] != ',' && text[i] != ')') i++;
                string raw = text[start..i].Trim();
                values.Add(raw.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : raw);
            }
        }
        return i;
    }

    private static bool Matches(string text, int i, string token) =>
        string.CompareOrdinal(text, i, token, 0, token.Length) == 0;

    private static int SkipLine(string text, int i)
    {
        int end = text.IndexOf('\n', i);
        return end < 0 ? text.Length : end + 1;
    }
}
