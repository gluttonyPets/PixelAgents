using Server.Services.Ai;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Transcripcion de OpenAI con marcas de tiempo: el formulario que se manda y
/// el texto que sale, que es el que lee el orquestador loop por su puerto de
/// locucion.
/// </summary>
public class TranscripcionConMarcasTests
{
    [Fact]
    public void ConMarcas_UnaFrasePorLineaQueElLoopSabeLeer()
    {
        var json = """
            {"text":"Los pulpos tienen tres corazones. Y sangre azul.","duration":9.1,
             "segments":[
               {"id":0,"start":0.0,"end":4.02,"text":" Los pulpos tienen tres corazones."},
               {"id":1,"start":5.0,"end":9.1,"text":" Y sangre azul."}
             ]}
            """;

        var text = OpenAiTranscription.ParseResponse(json);

        Assert.Equal("[00:00.0 - 00:04.0] Los pulpos tienen tres corazones.\n[00:05.0 - 00:09.1] Y sangre azul.", text);
        var segments = LoopPlanner.ParseTimestamps(text);
        Assert.Equal(2, segments.Count);
        Assert.Equal(5.0, segments[1].Start, 3);
        Assert.Equal(9.1, OpenAiTranscription.ReadDuration(json));
    }

    [Fact]
    public void SinMarcas_ElTextoPlano()
    {
        Assert.Equal("hola mundo", OpenAiTranscription.ParseResponse("""{"text":" hola mundo "}"""));
    }

    [Fact]
    public async Task ElFormularioPideVerboseJsonSoloConMarcas()
    {
        using var withTs = OpenAiTranscription.BuildForm("whisper-1", [1, 2], "voz.mp3", "audio/mpeg", true, "es");
        using var without = OpenAiTranscription.BuildForm("gpt-4o-transcribe", [1, 2], "voz.mp3", "audio/mpeg", false, null);

        var a = await withTs.ReadAsStringAsync();
        var b = await without.ReadAsStringAsync();

        Assert.Contains("verbose_json", a);
        Assert.Contains("timestamp_granularities[]", a);
        Assert.Contains("name=language", a);
        Assert.DoesNotContain("verbose_json", b);
        Assert.DoesNotContain("timestamp_granularities", b);
        Assert.Contains("gpt-4o-transcribe", b);
    }

    [Fact]
    public void ElRechazoDelFormato_SeReconoce()
    {
        Assert.True(OpenAiTranscription.IsUnsupportedFormatError(
            """{"error":{"message":"response_format 'verbose_json' is not compatible with model"}}"""));
        Assert.False(OpenAiTranscription.IsUnsupportedFormatError("""{"error":{"message":"Invalid file format."}}"""));
    }

    [Fact]
    public void ElCosteSaleDeLosMinutos()
    {
        // whisper-1: 0.006 USD por minuto.
        Assert.Equal(0.006m, OpenAiTranscription.EstimateCost("whisper-1", 60));
        Assert.Equal(0m, OpenAiTranscription.EstimateCost("whisper-1", null));
    }
}
