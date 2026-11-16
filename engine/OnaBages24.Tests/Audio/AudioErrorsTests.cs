using System.Runtime.InteropServices;
using OnaBages.Audio;

namespace OnaBages24.Tests.Audio;

/// <summary>Els errors de Windows en català (fase 5, pas 3; D-158).</summary>
public class AudioErrorsTests
{
    [Fact]
    public void Els_codis_coneguts_es_diuen_en_catala_amb_el_codi_entre_parentesis()
    {
        var disk = new COMException("Media Foundation error 0x8003001E", unchecked((int)0x8003001E));
        Assert.Equal("error de lectura del disc (0x8003001E)", AudioErrors.Message(disk));
        Assert.True(AudioErrors.IsAccessProblem(disk));

        // També si és a dins d'un altre error
        var wrapped = new InvalidOperationException("embolcall", new IOException("xarxa", unchecked((int)0x80070040)));
        Assert.Equal("s'ha perdut la connexió de xarxa (0x80070040)", AudioErrors.Describe(wrapped));

        // El format no és un problema d'accés
        var format = new COMException("format", unchecked((int)0xC00D36C4));
        Assert.Equal("format no compatible (0xC00D36C4)", AudioErrors.Describe(format));
        Assert.False(AudioErrors.IsAccessProblem(format));
    }

    [Fact]
    public void Un_codi_desconegut_deixa_el_missatge_original()
    {
        var other = new COMException("Alguna cosa estranya", unchecked((int)0x80004005));
        Assert.Null(AudioErrors.Describe(other));
        Assert.Equal("Alguna cosa estranya", AudioErrors.Message(other));
        Assert.False(AudioErrors.IsAccessProblem(other));
    }

    [Fact]
    public void Un_fitxer_bloquejat_per_un_altre_programa_no_diu_que_el_format_no_es_compatible()
    {
        string path = TestAudio.CreateWav(48000, 2, 1);
        try
        {
            using var lockedBy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var ex = Assert.Throws<AudioFileException>(() => AudioFileSource.Open(path, 48000));
            Assert.DoesNotContain("format no compatible", ex.Message);
            Assert.Contains("un altre programa", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
