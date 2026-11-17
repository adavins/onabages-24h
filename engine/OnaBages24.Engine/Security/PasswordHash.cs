using System.Security.Cryptography;
using System.Text;

namespace OnaBages24.Engine.Security;

/// <summary>
/// Les contrasenyes dels usuaris (D-155): només se'n desa l'empremta (PBKDF2-SHA256 amb una sal aleatòria per usuari), que
/// no es pot desfer. Funciona igual en qualsevol ordinador i després de restaurar una còpia. Tot amb el que porta .NET.
/// </summary>
public static class PasswordHash
{
    /// <summary>Iteracions per a les contrasenyes noves (la recomanació d'OWASP per a PBKDF2-SHA256). Unes dècimes de segon.</summary>
    public const int StandardIterations = 600_000;
    /// <summary>La llargada mínima: una de més curta es pot endevinar provant.</summary>
    public const int MinLength = 8;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Què li falta a una contrasenya nova (null si és bona).</summary>
    public static string? Problem(string password) =>
        password.Length < MinLength ? $"La contrasenya ha de tenir com a mínim {MinLength} caràcters." : null;

    /// <summary>L'empremta, la sal i les iteracions d'una contrasenya nova.</summary>
    public static (string Hash, string Salt, int Iterations) Create(string password, int iterations = StandardIterations)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), iterations);
    }

    /// <summary>Si la contrasenya és la d'aquesta empremta (comparació de temps constant).</summary>
    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        try
        {
            byte[] expected = Convert.FromBase64String(hash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt), iterations,
                HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Una contrasenya provisional a l'atzar (per restablir la de l'administrador): fàcil de copiar, sense lletres que es confonguin.</summary>
    public static string Provisional()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var text = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            if (i is 4 or 8) text.Append('-');
            text.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        }
        return text.ToString();
    }
}
