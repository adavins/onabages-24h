using System.Text.Json.Nodes;
using NAudio.Wave;
using OnaBages24.Data;
using OnaBages24.Engine;
using OnaBages24.Engine.LibraryTools;
using OnaBages24.Engine.Protocol;
using OnaBages24.Tests.Data;

namespace OnaBages24.Tests.Engine;

/// <summary>Editor senzill de punts (fase 3, pas 7e; D-093): forma d'ona, punts a mà i «Recalcula».</summary>
public sealed class CueEditorTests : IDisposable
{
    private const int Rate = 44100;
    private readonly TempFolder _temp = new();
    private readonly Database _db;
    private readonly Library _library;
    private readonly CommandRouter _router;
    private readonly long _track;

    public CueEditorTests()
    {
        _db = Database.Open(_temp.File("punts.db"));
        _library = new Library(_db);
        _router = new CommandRouter(new EngineState(), () => { }, _library);
        // 1 s de silenci, 3 s de to a 0,5 (−6 dB) i 1 s de silenci
        string path = _temp.File("pista.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)))
        {
            var samples = new float[5 * Rate * 2];
            for (int f = Rate; f < 4 * Rate; f++)
                samples[f * 2] = samples[f * 2 + 1] = 0.5f * (float)Math.Sin(2 * Math.PI * 440 * f / Rate);
            writer.WriteSamples(samples, 0, samples.Length);
        }
        _track = _library.AddTrack(new Track { Path = path, Title = "Pista", DurationMs = 5000, StartMs = 1000, MixMs = 4000, EndMs = 4000, CueSource = CueSource.Analyzer });
    }

    public void Dispose()
    {
        _db.Dispose();
        _temp.Dispose();
    }

    private JsonObject Send(JsonObject msg)
    {
        msg["v"] = 1;
        msg["id"] = 1;
        return _router.Handle(msg.ToJsonString());
    }

    private static JsonNode Result(JsonObject r) => r["result"] ?? throw new Xunit.Sdk.XunitException(r["error"]!.ToJsonString());

    [Fact]
    public void L_escala_de_la_forma_d_ona_es_en_dB()
    {
        Assert.Equal(255, CueWaveform.ToByte(1f));
        Assert.Equal(0, CueWaveform.ToByte(0.001f)); // −60 dB
        Assert.Equal(0, CueWaveform.ToByte(0f));
        Assert.InRange(CueWaveform.ToByte(0.5f), 227, 231); // −6 dB
    }

    [Fact]
    public void La_forma_d_ona_d_un_tros_mostra_el_silenci_i_el_so()
    {
        var w = Result(Send(new() { ["cmd"] = "library.waveform", ["trackId"] = _track, ["buckets"] = 50 }));
        var peaks = w["peaks"]!.AsArray().Select(p => p!.GetValue<int>()).ToArray();
        Assert.Equal(50, peaks.Length);                // 100 ms per tros
        Assert.Equal(0, peaks[5]);                     // 0,5 s: silenci
        Assert.InRange(peaks[25], 225, 232);           // 2,5 s: el to (−6 dB)
        Assert.Equal(0, peaks[47]);                    // 4,7 s: silenci

        // Només un tros: de 0,5 s a 1,5 s (el so comença a la meitat)
        var part = Result(Send(new() { ["cmd"] = "library.waveform", ["trackId"] = _track, ["fromMs"] = 500, ["toMs"] = 1500, ["buckets"] = 10 }));
        var p2 = part["peaks"]!.AsArray().Select(p => p!.GetValue<int>()).ToArray();
        Assert.Equal(0, p2[3]);
        Assert.True(p2[7] > 200);
    }

    [Fact]
    public void Si_el_fitxer_ja_no_hi_es_la_forma_d_ona_ho_diu_en_catala()
    {
        // L'estat desat diu que hi és, però s'acaba d'esborrar (D-112)
        long gone = _library.AddTrack(new Track { Path = _temp.File("esborrat.wav"), Title = "Esborrada", DurationMs = 5000 });
        var error = Send(new() { ["cmd"] = "library.waveform", ["trackId"] = gone })["error"]!;
        Assert.Equal("bad_params", error["code"]!.GetValue<string>());
        Assert.StartsWith("El fitxer no hi és", error["message"]!.GetValue<string>());
    }

    [Fact]
    public void Els_punts_a_ma_queden_manuals_i_s_han_de_poder_fer()
    {
        var track = Result(Send(new() { ["cmd"] = "library.setCues", ["trackId"] = _track, ["startMs"] = 900, ["mixMs"] = 3500, ["endMs"] = 4100 }))["track"]!;
        Assert.Equal("manual", track["cueSource"]!.GetValue<string>());
        Assert.Equal(3500, _library.GetTrack(_track)!.MixMs);

        string Error(long s, long m, long e) => Send(new() { ["cmd"] = "library.setCues", ["trackId"] = _track, ["startMs"] = s, ["mixMs"] = m, ["endMs"] = e })["error"]!["code"]!.GetValue<string>();
        Assert.Equal("bad_params", Error(2000, 1000, 4000));  // mescla abans de l'inici
        Assert.Equal("bad_params", Error(1000, 4500, 4000));  // final abans de la mescla
        Assert.Equal("bad_params", Error(1000, 4000, 6000));  // més enllà de la durada
        Assert.Equal("bad_params", Error(-1, 4000, 4000));
        Assert.Equal(3500, _library.GetTrack(_track)!.MixMs); // els errors no canvien res
    }

    [Fact]
    public void Recalcula_substitueix_els_punts_a_ma_pels_de_l_analitzador()
    {
        Send(new() { ["cmd"] = "library.setCues", ["trackId"] = _track, ["startMs"] = 0, ["mixMs"] = 100, ["endMs"] = 200 });
        var track = Result(Send(new() { ["cmd"] = "library.recalculateCues", ["trackId"] = _track }))["track"]!;
        Assert.Equal("analyzer", track["cueSource"]!.GetValue<string>());
        Assert.InRange(track["startMs"]!.GetValue<long>(), 995, 1005);
        Assert.InRange(track["endMs"]!.GetValue<long>(), 3995, 4005);
    }
}
