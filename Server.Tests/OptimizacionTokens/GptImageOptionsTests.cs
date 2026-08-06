using Server.Services.Ai;
using Xunit;

namespace Server.Tests.OptimizacionTokens;

/// <summary>
/// gpt-image factura la imagen como tokens de salida y el tramo de calidad
/// multiplica el coste por 4 entre "medium" y "high". El fallo que estos tests
/// blindan: la UI guardaba "standard" (vocabulario de DALL-E), gpt-image no lo
/// reconocia y la peticion acababa generando en "high".
/// </summary>
public class GptImageOptionsTests
{
    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("HIGH", "high")]
    [InlineData("  low  ", "low")]
    [InlineData("hd", "high")]
    public void NormalizeQuality_RespetaLosTramosValidos(string configurado, string esperado)
    {
        Assert.Equal(esperado, GptImageOptions.NormalizeQuality(configurado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("standard")]
    [InlineData("auto")]
    [InlineData("valor-desconocido")]
    public void NormalizeQuality_SinValorUtil_CaeEnMediumYNoEnHigh(string? configurado)
    {
        Assert.Equal("medium", GptImageOptions.NormalizeQuality(configurado));
        Assert.NotEqual("high", GptImageOptions.NormalizeQuality(configurado));
    }

    [Theory]
    [InlineData("1024x1024", "1024x1024")]
    [InlineData("1536x1024", "1536x1024")]
    [InlineData("1024x1536", "1024x1536")]
    public void NormalizeSizeForPricing_RespetaLosTamanosValidos(string configurado, string esperado)
    {
        Assert.Equal(esperado, GptImageOptions.NormalizeSizeForPricing(configurado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    public void NormalizeSizeForPricing_SinTamano_AsumeRetrato(string? configurado)
    {
        // "auto" no es 1024x1024: la API devuelve retrato, que es el tramo caro.
        // Asumir el cuadrado hacia que el coste estimado saliera por debajo del real.
        Assert.Equal("1024x1536", GptImageOptions.NormalizeSizeForPricing(configurado));
    }

    [Theory]
    [InlineData("gpt-image-1", true)]
    [InlineData("gpt-image-1-mini", true)]
    [InlineData("GPT-IMAGE-1.5", true)]
    [InlineData("dall-e-3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsGptImage_DetectaLaFamilia(string? modelo, bool esperado)
    {
        Assert.Equal(esperado, GptImageOptions.IsGptImage(modelo));
    }
}
