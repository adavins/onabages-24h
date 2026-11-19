using OnaBages24.Engine;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Ordre <c>atura</c> (D-191): les parts que es poden provar sense un motor de veritat.</summary>
public sealed class StopCommandTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ReadsThePortTokenAndPidOfTheRunningEngine()
    {
        string path = _temp.File("engine.json");
        File.WriteAllText(path, """{ "schemaVersion": 1, "pid": 1234, "port": 47824, "token": "abc", "startedAt": "2026-11-19T10:00:00Z" }""");

        Assert.Equal(new StopCommand.RuntimeInfo(1234, 47824, "abc"), StopCommand.ReadRuntimeFile(path));
    }

    [Theory]
    [InlineData(null)] // no hi és
    [InlineData("no és JSON")]
    [InlineData("""{ "pid": 1234, "port": 47824 }""")] // sense testimoni
    [InlineData("""{ "pid": 0, "port": 47824, "token": "abc" }""")]
    [InlineData("""{ "pid": 1234, "port": 70000, "token": "abc" }""")]
    [InlineData("""{ "pid": "1234", "port": 47824, "token": "abc" }""")]
    public void AMissingOrBadRuntimeFileMeansNoEngine(string? content)
    {
        string path = _temp.File("engine.json");
        if (content is not null) File.WriteAllText(path, content);

        Assert.Null(StopCommand.ReadRuntimeFile(path));
    }

    [Theory]
    [InlineData(@"D:\OnaBages24\OnaBages24.exe", true)] // la versió portable
    [InlineData(@"d:\onabages24\OnaBages24.exe", true)] // Windows no distingeix majúscules
    [InlineData(@"D:\apps\onabages-24h\app\node_modules\electron\dist\electron.exe", false)] // una altra carpeta
    [InlineData(@"D:\OnaBages24\resources\engine\OnaBages24.Engine.exe", false)] // el motor no: rep l'ordre d'aturada
    [InlineData(@"D:\OnaBages24-nova\OnaBages24.exe", false)] // una altra còpia, amb un nom que comença igual
    [InlineData(null, false)] // no se'n sap el programa (d'un altre usuari)
    public void OnlyClosesTheInterfaceOfThisCopy(string? executable, bool expected)
    {
        Assert.Equal(expected, StopCommand.IsInterfaceProgram(executable, @"D:\OnaBages24\"));
        Assert.Equal(expected, StopCommand.IsInterfaceProgram(executable, @"D:\OnaBages24"));
    }

    [Fact]
    public void InDevelopmentTheInterfaceIsElectronInsideTheRepository()
    {
        Assert.True(StopCommand.IsInterfaceProgram(@"D:\apps\onabages-24h\app\node_modules\electron\dist\electron.exe", @"D:\apps\onabages-24h"));
    }
}
