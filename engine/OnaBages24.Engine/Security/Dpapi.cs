using System.Security.Cryptography;
using System.Text;

namespace OnaBages24.Engine.Security;

/// <summary>
/// Contrasenyes que el motor ha de poder tornar a llegir (la del correu, D-155; les dels servidors d'streaming, D-202),
/// xifrades amb DPAPI de Windows: només les pot desxifrar el mateix usuari al mateix ordinador. Una còpia de la base de
/// dades no les deixa llegir. Cada ús té el seu <c>purpose</c>, perquè una no es pugui fer passar per una altra.
/// </summary>
public static class Dpapi
{
    /// <summary>El text xifrat, en Base64.</summary>
    public static string Protect(string text, string purpose) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Entropy(purpose), DataProtectionScope.CurrentUser));

    /// <summary>El text, o null si no es pot desxifrar (una còpia d'un altre ordinador o d'un altre usuari).</summary>
    public static string? Unprotect(string protectedText, string purpose)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy(purpose), DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("OnaBages24." + purpose);
}
