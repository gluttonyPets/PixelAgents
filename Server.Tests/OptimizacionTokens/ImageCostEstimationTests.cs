using Server.Services.Ai;
using Xunit;

namespace Server.Tests.OptimizacionTokens;

/// <summary>
/// El coste estimado que se muestra en la UI tiene que coincidir con lo que
/// realmente factura OpenAI. Antes no coincidia: el estimador asumia tamano
/// cuadrado y traducia "standard"/"auto" de forma distinta a como lo hacia la
/// API, asi que se mostraba menos de lo que se pagaba.
/// </summary>
public class ImageCostEstimationTests
{
    [Fact]
    public void GptImage_SinConfig_EstimaElTramoQueRealmenteSeEnvia()
    {
        // Sin config el provider manda quality=medium y deja el tamano en auto,
        // que la API resuelve a retrato: medium-1024x1536 = 0,063 $.
        Assert.Equal(0.063m, PricingCatalog.EstimateImageCost("gpt-image-1"));
    }

    [Fact]
    public void GptImage_CalidadStandardHeredada_SeEstimaComoMedium()
    {
        // "standard" es un valor de DALL-E que la UI antigua guardaba tambien
        // para gpt-image. Ahora se interpreta como medium en los dos lados.
        var config = new Dictionary<string, object>
        {
            ["quality"] = "standard",
            ["size"] = "1024x1536",
        };

        Assert.Equal(0.063m, PricingCatalog.EstimateImageCost("gpt-image-1", config));
    }

    [Fact]
    public void GptImage_AltaEsCuatroVecesMediaAlMismoTamano()
    {
        var media = PricingCatalog.EstimateImageCost("gpt-image-1", new Dictionary<string, object>
        {
            ["quality"] = "medium",
            ["size"] = "1024x1536",
        });
        var alta = PricingCatalog.EstimateImageCost("gpt-image-1", new Dictionary<string, object>
        {
            ["quality"] = "high",
            ["size"] = "1024x1536",
        });

        Assert.Equal(0.063m, media);
        Assert.Equal(0.250m, alta);
        Assert.True(alta > media * 3, "El salto medium->high debe seguir siendo el gran diferencial de coste");
    }

    [Fact]
    public void GptImageMini_UsaSuPropiaTabla()
    {
        var config = new Dictionary<string, object>
        {
            ["quality"] = "medium",
            ["size"] = "1024x1536",
        };

        Assert.Equal(0.0225m, PricingCatalog.EstimateImageCost("gpt-image-1-mini", config));
    }

    [Fact]
    public void DallE3_MantieneSuVocabularioPropio()
    {
        // El cambio en gpt-image no debe alterar la estimacion de DALL-E.
        var config = new Dictionary<string, object>
        {
            ["quality"] = "standard",
            ["size"] = "1024x1024",
        };

        Assert.Equal(0.040m, PricingCatalog.EstimateImageCost("dall-e-3", config));
    }

    [Fact]
    public void DallE2_SinCalidad_SigueUsandoSoloElTamano()
    {
        var config = new Dictionary<string, object> { ["size"] = "512x512" };
        Assert.Equal(0.018m, PricingCatalog.EstimateImageCost("dall-e-2", config));
    }
}
