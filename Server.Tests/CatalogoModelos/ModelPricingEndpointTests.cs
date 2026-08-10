using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.CatalogoModelos;

/// <summary>
/// La pantalla de precios calcula el coste por ejecución en el cliente a partir de
/// las tarifas por millón que devuelve el servidor. Si esas tarifas no llegan, la
/// tabla muestra ceros sin decir que algo ha fallado.
/// </summary>
public class ModelPricingEndpointTests
{
    [Fact]
    public void GetTextRate_DevuelveLaTarifaPorMillon()
    {
        var rate = PricingCatalog.GetTextRate("gpt-5.6-sol");

        Assert.NotNull(rate);
        Assert.Equal(5.00m, rate!.Value.InputPerMTok);
        Assert.Equal(30.00m, rate.Value.OutputPerMTok);
    }

    [Fact]
    public void GetTextRate_EsCoherenteConEstimateTextCost()
    {
        // Las dos rutas tienen que dar lo mismo: una alimenta la pantalla de precios
        // y la otra el coste que se guarda en cada ejecución.
        foreach (var model in ModelCatalog.AllModels
            .Where(m => m.Types.Contains("Text", StringComparer.OrdinalIgnoreCase)))
        {
            var rate = PricingCatalog.GetTextRate(model.Id);
            Assert.NotNull(rate);

            var viaRate = rate!.Value.InputPerMTok * 10_000 / 1_000_000m
                        + rate.Value.OutputPerMTok * 2_000 / 1_000_000m;

            Assert.Equal(PricingCatalog.EstimateTextCost(model.Id, 10_000, 2_000), viaRate);
        }
    }

    [Fact]
    public void GetTextRate_DevuelveNullSiNoHayNadaAplicable()
    {
        Assert.Null(PricingCatalog.GetTextRate("modelo-inventado"));
    }

    [Fact]
    public void CostFor_ConCeroTokensNoRevienta()
    {
        // El usuario puede vaciar los dos campos de la pantalla; eso es 0, no un error.
        Assert.Equal(0m, PricingCatalog.EstimateTextCost("gpt-5.6-sol", 0, 0));
    }

    [Fact]
    public void ElDtoDePrecioSoloRellenaLosCamposDeSuTipo()
    {
        // El cliente decide qué columnas pinta según Kind: si un modelo de texto
        // trajese precios de imagen (o al revés), la tabla mezclaría unidades.
        var texto = new ModelPriceResponse(
            "gpt-5.6-sol", "GPT-5.6 Sol", "OpenAI", "text",
            5.00m, 30.00m, null, null, null,
            new ModelLifecycleResponse("gpt-5.6-sol", "OpenAI", "active",
                null, null, null, null, null, true));

        Assert.Equal("text", texto.Kind);
        Assert.NotNull(texto.InputPerMTok);
        Assert.Null(texto.ImageMedium);

        var imagen = new ModelPriceResponse(
            "gpt-image-2", "GPT Image 2", "OpenAI", "image",
            null, null, 0.0082m, 0.0317m, 0.1248m,
            new ModelLifecycleResponse("gpt-image-2", "OpenAI", "active",
                null, null, null, null, null, null));

        Assert.Equal("image", imagen.Kind);
        Assert.Null(imagen.InputPerMTok);
        Assert.NotNull(imagen.ImageMedium);
    }
}
