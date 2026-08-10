using Server.Services.Ai;
using Xunit;

namespace Server.Tests.CatalogoModelos;

/// <summary>
/// El coste estimado se calculaba resolviendo el modelo por el primer prefijo que
/// encajase en el diccionario. Como "gpt-5" esta antes que los ids largos, cada
/// modelo nuevo de la familia heredaba la tarifa del mas antiguo: gpt-5.5 y
/// gpt-5.6-sol se cobraban tres veces por debajo de lo real y gpt-5.6-luna diez
/// veces por encima, sin ningun sintoma visible.
/// </summary>
public class PricingCatalogTests
{
    private const int UnMillon = 1_000_000;

    [Theory]
    [InlineData("gpt-5.6-sol", 5.00, 30.00)]
    [InlineData("gpt-5.6-terra", 2.00, 12.00)]
    [InlineData("gpt-5.6-luna", 0.20, 1.20)]
    [InlineData("gpt-5.6", 5.00, 30.00)]           // el alias factura como Sol
    [InlineData("gpt-5.5", 5.00, 30.00)]
    [InlineData("gpt-5.5-pro", 30.00, 180.00)]
    [InlineData("gpt-5.4", 2.50, 15.00)]
    [InlineData("gpt-5.4-mini", 0.75, 4.50)]
    [InlineData("gpt-5.4-nano", 0.20, 1.25)]
    [InlineData("gpt-5", 1.25, 10.00)]
    [InlineData("gpt-5-nano", 0.05, 0.40)]
    [InlineData("gpt-4o", 2.50, 10.00)]
    public void CadaModeloUsaSuPropiaTarifa(string modelo, double entrada, double salida)
    {
        Assert.True(PricingCatalog.HasExactTextPrice(modelo), $"{modelo} no tiene tarifa propia");

        Assert.Equal((decimal)entrada, PricingCatalog.EstimateTextCost(modelo, UnMillon, 0), 4);
        Assert.Equal((decimal)salida, PricingCatalog.EstimateTextCost(modelo, 0, UnMillon), 4);
    }

    [Fact]
    public void ModeloNuevoDeUnaFamiliaNoHeredaLaTarifaDelHermanoMasAntiguo()
    {
        // gpt-5.6-luna es 25 veces mas barato de salida que gpt-5; si el fallback
        // volviese a coger el primer prefijo, los dos costarian lo mismo.
        var luna = PricingCatalog.EstimateTextCost("gpt-5.6-luna", 0, UnMillon);
        var gpt5 = PricingCatalog.EstimateTextCost("gpt-5", 0, UnMillon);

        Assert.NotEqual(gpt5, luna);
        Assert.Equal(1.20m, luna, 4);
    }

    [Fact]
    public void ModeloDesconocidoCaeEnElParienteMasEspecificoYSeMarcaComoInexacto()
    {
        // Una version futura sin tarifa publicada aun: mejor estimar con el pariente
        // mas cercano que devolver 0, pero la UI tiene que poder decir que es aproximado.
        Assert.False(PricingCatalog.HasExactTextPrice("gpt-5.4-turbo-experimental"));
        Assert.Equal(2.50m, PricingCatalog.EstimateTextCost("gpt-5.4-turbo-experimental", UnMillon, 0), 4);
    }

    [Fact]
    public void ModeloSinNingunParienteCuesta0()
    {
        Assert.Equal(0m, PricingCatalog.EstimateTextCost("modelo-inventado", UnMillon, UnMillon));
        Assert.False(PricingCatalog.HasExactTextPrice("modelo-inventado"));
    }

    [Fact]
    public void SnapshotConFecha_UsaLaTarifaDeSuAlias()
    {
        Assert.True(PricingCatalog.HasExactTextPrice("gpt-5-mini-2025-08-07"));
        Assert.Equal(
            PricingCatalog.EstimateTextCost("gpt-5-mini", UnMillon, UnMillon),
            PricingCatalog.EstimateTextCost("gpt-5-mini-2025-08-07", UnMillon, UnMillon));
    }

    [Fact]
    public void ElSnapshotCaroDeGpt4oNoSeConfundeConElAliasBarato()
    {
        // gpt-4o-2024-05-13 cuesta 5/15; el alias gpt-4o bajo a 2,50/10.
        Assert.Equal(5.00m, PricingCatalog.EstimateTextCost("gpt-4o-2024-05-13", UnMillon, 0), 4);
        Assert.Equal(2.50m, PricingCatalog.EstimateTextCost("gpt-4o", UnMillon, 0), 4);
    }

    [Fact]
    public void CadaVersionDeGptImageUsaSuPropiaTarifa()
    {
        // gpt-image-1.5 no tenia tabla propia y caia en la de gpt-image-1, un 25%
        // mas cara: se estimaba de mas en cada imagen generada.
        var config = new Dictionary<string, object>
        {
            ["quality"] = "high",
            ["size"] = "1024x1024",
        };

        var v1 = PricingCatalog.EstimateImageCost("gpt-image-1", config);
        var v15 = PricingCatalog.EstimateImageCost("gpt-image-1.5", config);
        var v2 = PricingCatalog.EstimateImageCost("gpt-image-2", config);

        Assert.Equal(0.167m, v1);
        Assert.Equal(0.1331m, v15);
        Assert.Equal(0.1248m, v2);

        // Cada version nueva abarata la salida, asi que el orden debe mantenerse.
        Assert.True(v2 < v15 && v15 < v1);
    }

    [Fact]
    public void GptImage2_SinConfig_EstimaElTramoQueRealmenteSeEnvia()
    {
        // Igual que el resto de gpt-image: sin config va quality=medium y size=auto,
        // que la API resuelve a retrato.
        Assert.Equal(0.0475m, PricingCatalog.EstimateImageCost("gpt-image-2"));
    }

    [Fact]
    public void TodoModeloDeTextoDelCatalogoTieneTarifaPropia()
    {
        // Sin esto, un modelo nuevo se puede colar en el catalogo y estimarse con la
        // tarifa de otro sin que nadie se entere hasta ver la factura.
        var textModels = ModelCatalog.AllModels
            .Where(m => m.Types.Contains("Text", StringComparer.OrdinalIgnoreCase));

        foreach (var model in textModels)
        {
            Assert.True(PricingCatalog.HasExactTextPrice(model.Id),
                $"{model.Id} esta en el catalogo pero no tiene tarifa propia en PricingCatalog");
        }
    }

    [Fact]
    public void TodoModeloDeImagenDelCatalogoTienePrecio()
    {
        var imageModels = ModelCatalog.AllModels
            .Where(m => m.Types.Contains("Image", StringComparer.OrdinalIgnoreCase));

        foreach (var model in imageModels)
        {
            Assert.True(PricingCatalog.EstimateImageCost(model.Id) > 0m,
                $"{model.Id} esta en el catalogo pero se estima a coste 0");
        }
    }
}
