using System.Globalization;

namespace OnaBages24.Data;

/// <summary>
/// Com es desen les dates a la base de dades:
/// - instants (quan ha sonat una pista, quan s'ha modificat una fitxa): en UTC, «2026-11-09T14:00:00.000Z»;
/// - dates de calendari (vigència d'una pista): «2026-11-09», sense hora.
/// </summary>
public static class DbTime
{
    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private const string DateFormat = "yyyy-MM-dd";

    public static string ToText(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseInstant(string text) =>
        new(DateTime.SpecifyKind(DateTime.ParseExact(text, InstantFormat, CultureInfo.InvariantCulture), DateTimeKind.Utc));

    public static string ToText(DateOnly date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDate(string text) => DateOnly.ParseExact(text, DateFormat, CultureInfo.InvariantCulture);
}
