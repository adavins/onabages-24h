using System.Globalization;
using System.Text;

namespace OnaBages24.Data;

/// <summary>Text per a les cerques (fase 3, pas 3a; D-079): sense accents ni majúscules.</summary>
public static class TextSearch
{
    /// <summary>
    /// «Cançó d'Àvia» → «canco d'avia». Es treuen els accents (les marques diacrítiques que queden en separar cada lletra
    /// de la seva marca: à → a + `), també la ç (c + trenet) i la l·l es manté. Null queda null.
    /// </summary>
    public static string? Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
