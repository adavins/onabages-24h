using System.Reflection;

namespace OnaBages24.Engine;

/// <summary>Nom i versió del motor.</summary>
public static class EngineInfo
{
    public const string Name = "Ona Bages 24h — motor";

    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var v = typeof(EngineInfo).Assembly.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
