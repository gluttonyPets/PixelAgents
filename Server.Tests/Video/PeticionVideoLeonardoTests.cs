using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Cuerpo de POST /v2/generations para video y lectura del coste real.
///
/// La v2 valida cada modelo contra su propio esquema: un campo que el modelo no
/// tiene (la duracion en Motion, el audio en Hailuo 2.3) es un 400. Por eso el
/// cuerpo se construye con lo que dice <see cref="VideoModelOptions"/> y estos
/// tests miran tanto lo que va como lo que NO va.
/// </summary>
public class PeticionVideoLeonardoTests
{
    private static JsonElement Body(string modelId, string apiModel, Dictionary<string, object>? config = null)
    {
        var spec = VideoModelOptions.Find(modelId)!;
        var options = VideoModelOptions.Resolve(spec, config ?? new Dictionary<string, object>(), (1280, 720));
        var body = LeonardoProvider.BuildVideoRequest(apiModel, spec, options, "zoom lento", "img-123");
        return JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement;
    }

    [Fact]
    public void LaPeticionLlevaModeloPrivadoYFotogramaInicialSubido()
    {
        var body = Body("leonardo-kling-3", "kling-3.0");

        Assert.Equal("kling-3.0", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("public").GetBoolean());

        var p = body.GetProperty("parameters");
        Assert.Equal("zoom lento", p.GetProperty("prompt").GetString());
        Assert.Equal(1, p.GetProperty("quantity").GetInt32());

        var frame = p.GetProperty("guidances").GetProperty("start_frame")[0].GetProperty("image");
        Assert.Equal("img-123", frame.GetProperty("id").GetString());
        Assert.Equal("UPLOADED", frame.GetProperty("type").GetString());
    }

    [Fact]
    public void Motion_NoLlevaDuracionNiAudio_PeroSiInterpolacionYMejoraDePrompt()
    {
        var p = Body("leonardo-motion-2", "motion_2.0", new() { ["resolution"] = "RESOLUTION_720" })
            .GetProperty("parameters");

        Assert.False(p.TryGetProperty("duration", out _));
        Assert.False(p.TryGetProperty("motion_has_audio", out _));
        Assert.True(p.GetProperty("frame_interpolation").GetBoolean());
        Assert.Equal("OFF", p.GetProperty("prompt_enhance").GetString());
        Assert.Equal(1280, p.GetProperty("width").GetInt32());
        Assert.Equal(720, p.GetProperty("height").GetInt32());
    }

    [Fact]
    public void Veo_LlevaDuracionYAudioApagado_SinLosCamposDeMotion()
    {
        var p = Body("leonardo-veo-3.1", "veo-3.1-generate-001", new() { ["duration"] = 6L })
            .GetProperty("parameters");

        Assert.Equal(6, p.GetProperty("duration").GetInt32());
        Assert.False(p.GetProperty("motion_has_audio").GetBoolean());
        Assert.False(p.TryGetProperty("frame_interpolation", out _));
        Assert.False(p.TryGetProperty("prompt_enhance", out _));
        Assert.False(p.TryGetProperty("quality", out _));
    }

    [Fact]
    public void Hailuo03_LlevaSiempreAudioYCalidad()
    {
        var p = Body("leonardo-hailuo-03", "hailuo-03", new() { ["audio"] = false })
            .GetProperty("parameters");

        Assert.True(p.GetProperty("motion_has_audio").GetBoolean());
        Assert.Equal("TURBO", p.GetProperty("quality").GetString());
    }

    [Fact]
    public void Hailuo23Fast_NoLlevaAudio()
    {
        var p = Body("leonardo-hailuo-2.3-fast", "hailuo-2_3-fast", new() { ["audio"] = true })
            .GetProperty("parameters");

        Assert.False(p.TryGetProperty("motion_has_audio", out _));
    }

    // ── Coste real ──

    private static decimal? Cost(string json) =>
        LeonardoProvider.ReadSubmitCost(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void LosCreditosDeLaV2SeConviertenADolares()
    {
        Assert.Equal(25 * PricingCatalog.LeonardoCreditUsd,
            Cost("""{"generationId":"g1","apiCreditCost":25}"""));
    }

    [Fact]
    public void ElCosteEnDolaresDelPagoPorUsoSeApuntaTalCual()
    {
        Assert.Equal(0.0147m, Cost("""
            {"motionVideoGenerationJob":{"generationId":"g1","apiCreditCost":null,
             "cost":{"amount":"0.0147","unit":"DOLLARS"}}}
            """));
    }

    [Fact]
    public void ElCosteEnCreditosConUnidadTambienSeConvierte()
    {
        Assert.Equal(40 * PricingCatalog.LeonardoCreditUsd,
            Cost("""{"generationId":"g1","cost":{"amount":"40","unit":"CREDITS"}}"""));
    }

    [Fact]
    public void SinCosteEnLaRespuesta_NoSeInventaUno()
    {
        // El provider cae entonces en la tarifa estimada del catalogo.
        Assert.Null(Cost("""{"generationId":"g1","apiCreditCost":null}"""));
    }
}
