using System.Text.Json.Nodes;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Streams a la biblioteca (D-195): library.addStream i canviar-ne l'adreça amb library.relink.</summary>
public sealed class StreamTrackTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly CommandRouter _router;

    public StreamTrackTests()
    {
        _db = Database.Open(_temp.File("streams.db"));
        _library = new Library(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library);
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Reply(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private JsonNode Send(JsonObject msg)
    {
        var reply = Reply(msg);
        Assert.True(reply["ok"]!.GetValue<bool>(), reply.ToJsonString());
        return reply["result"]!;
    }

    private string Error(JsonObject msg)
    {
        var reply = Reply(msg);
        Assert.False(reply["ok"]!.GetValue<bool>());
        return reply["error"]!["message"]!.GetValue<string>();
    }

    [Fact]
    public void AStreamIsAddedAsATrackOfTypeStream()
    {
        long sub = _library.EnsureSubcategory(_library.EnsureCategory("Radio Streams"), "Butlletins");
        var track = Send(new() { ["cmd"] = "library.addStream", ["url"] = " http://servidor:8000/directe ", ["title"] = "La Xarxa", ["subcategoryId"] = sub })["track"]!;

        var saved = _library.GetTrack(track["id"]!.GetValue<long>())!;
        Assert.Equal((TrackType.Stream, "http://servidor:8000/directe", "La Xarxa", sub, FileState.Ok),
            (saved.Type, saved.Path, saved.Title, saved.SubcategoryId, saved.FileState));
        // I surt al cercador de streams de l'editor d'esdeveniments
        var found = Send(new() { ["cmd"] = "library.search", ["text"] = "", ["type"] = "stream" })["tracks"]!.AsArray();
        Assert.Single(found);
    }

    [Theory]
    [InlineData("servidor:8000/directe")]
    [InlineData("ftp://servidor/directe")]
    [InlineData("D:\\musica\\directe.mp3")]
    [InlineData("")]
    public void TheAddressMustBeHttpOrHttps(string url)
    {
        Assert.Contains("http://", Error(new() { ["cmd"] = "library.addStream", ["url"] = url, ["title"] = "La Xarxa" }));
    }

    [Fact]
    public void AStreamNeedsANameAndAnAddressThatIsNotAlreadyThere()
    {
        Assert.Contains("nom", Error(new() { ["cmd"] = "library.addStream", ["url"] = "https://servidor/directe", ["title"] = " " }));
        Send(new() { ["cmd"] = "library.addStream", ["url"] = "https://servidor/directe", ["title"] = "Ona Bages directe" });
        Assert.Contains("«Ona Bages directe»", Error(new() { ["cmd"] = "library.addStream", ["url"] = "https://servidor/directe", ["title"] = "Una altra" }));
    }

    [Fact]
    public void RelinkChangesTheAddressOfAStreamAndKeepsTheTrack()
    {
        long id = Send(new() { ["cmd"] = "library.addStream", ["url"] = "http://vell:8000/directe", ["title"] = "La Xarxa" })["track"]!["id"]!.GetValue<long>();

        Send(new() { ["cmd"] = "library.relink", ["trackId"] = id, ["path"] = "https://nou/directe" });

        var saved = _library.GetTrack(id)!;
        Assert.Equal(("https://nou/directe", "La Xarxa", TrackType.Stream), (saved.Path, saved.Title, saved.Type));
        Assert.Contains("http://", Error(new() { ["cmd"] = "library.relink", ["trackId"] = id, ["path"] = "D:\\musica\\x.mp3" }));
    }
}
