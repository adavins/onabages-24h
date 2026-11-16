namespace OnaBages.Audio;

/// <summary>
/// Els errors de Windows més habituals en llegir un fitxer d'àudio, en català (fase 5, pas 3; D-158). Els textos tècnics
/// («Media Foundation error 0x8003001E») no diuen res a qui els llegeix; aquí es diu què ha passat i es deixa el codi
/// entre parèntesis, per si cal buscar-lo.
/// </summary>
public static class AudioErrors
{
    // Els codis (HRESULT): els errors de Windows (Win32) porten 0x8007 al davant
    private static readonly Dictionary<uint, string> Known = new()
    {
        [0x8003001E] = "error de lectura del disc",
        [0x8007001E] = "error de lectura del disc",
        [0x80070017] = "error de dades del disc",
        [0x8007045D] = "error del dispositiu en llegir",
        [0x80070015] = "la unitat no està disponible (disc desconnectat o apagat?)",
        [0x8007048F] = "el dispositiu no està connectat",
        [0x80070002] = "el fitxer no hi és",
        [0x80070003] = "la carpeta no hi és",
        [0x8007000F] = "la unitat no hi és",
        [0x80070005] = "no hi ha permís per llegir-lo",
        [0x80070020] = "un altre programa el té obert",
        [0x80070021] = "un altre programa el té bloquejat",
        [0x80070035] = "no es troba el camí de xarxa",
        [0x80070043] = "no es troba la carpeta compartida a la xarxa",
        [0x80070040] = "s'ha perdut la connexió de xarxa",
        [0x8007003B] = "error inesperat de la xarxa",
        [0x80070079] = "el disc o la xarxa han trigat massa a respondre",
        [0x800704CF] = "no es pot accedir a la xarxa",
        [0xC00D36C4] = "format no compatible",
    };

    /// <summary>
    /// «error de lectura del disc (0x8003001E)», si l'error (o el que porta a dins) és un dels coneguts; si no, null.
    /// </summary>
    public static string? Describe(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            uint code = unchecked((uint)e.HResult);
            if (Known.TryGetValue(code, out var text)) return $"{text} (0x{code:X8})";
        }
        return null;
    }

    /// <summary>L'error de disc o de xarxa és d'aquests: el fitxer pot estar bé (no és el format).</summary>
    public static bool IsAccessProblem(Exception? ex) => Describe(ex) is { } text && !text.StartsWith("format", StringComparison.Ordinal);

    /// <summary>El text en català si se'l coneix; si no, el missatge original.</summary>
    public static string Message(Exception ex) => Describe(ex) ?? ex.Message;
}
