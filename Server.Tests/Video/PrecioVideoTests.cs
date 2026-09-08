using Server.Services.Ai;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Tarifa de los modelos de video.
///
/// El video introduce una unidad de facturacion que no existia en el catalogo: el
/// SEGUNDO. Meterlo en la tabla de imagen (precio por pieza) haria que un clip de
/// 8 s costase lo mismo que uno de 4 s, y meterlo en la de texto no tiene sentido
/// ninguno. Ademas Leonardo no cobra en dolares sino en creditos, asi que el coste
/// que se apunta en la ejecucion sale de la conversion, no de la estimacion.
/// </summary>
public class PrecioVideoTests
{
    [Fact]
    public void ElModeloDeVideoEstaEnElCatalogoConSuTipo()
    {
        var modelo = ModelCatalog.Find("leonardo-motion-2");

        Assert.NotNull(modelo);
        Assert.Contains("Video", modelo!.Types);
        Assert.Equal("LeonardoAI", modelo.Provider);
    }

    [Fact]
    public void TodoModeloDeVideoDelCatalogoTieneTarifaPorSegundo()
    {
        // Sin tarifa no aparece en la pantalla de modelos y el coste estimado de la
        // ejecucion sale a cero, que es peor que salir aproximado.
        foreach (var modelo in ModelCatalog.GetByModuleType("Video"))
        {
            var rate = PricingCatalog.GetAuxiliaryRate(modelo.Id);

            Assert.True(rate is not null, $"{modelo.Id} no tiene tarifa");
            Assert.Equal("segundo", rate!.Unit);
        }
    }

    [Fact]
    public void ElCosteEstimadoCreceConLaDuracion()
    {
        var cinco = PricingCatalog.EstimateVideoCost("leonardo-motion-2", 5);
        var diez = PricingCatalog.EstimateVideoCost("leonardo-motion-2", 10);

        Assert.Equal(0.075m, cinco, 4);
        Assert.Equal(cinco * 2, diez, 4);
    }

    [Fact]
    public void UnModeloQueNoSeFacturaPorSegundo_NoSeMultiplicaPorLaDuracion()
    {
        // whisper-1 cobra por minuto. Si EstimateVideoCost aceptase cualquier
        // unidad, devolveria su tarifa multiplicada por los segundos: un numero
        // que no significa nada y que nadie detectaria mirando la cifra.
        Assert.Equal(0m, PricingCatalog.EstimateVideoCost("whisper-1", 5));
        Assert.Equal(0m, PricingCatalog.EstimateVideoCost("modelo-inventado", 5));
    }

    [Fact]
    public void SinDuracionConocida_NoSeInventaUnCoste()
    {
        Assert.Equal(0m, PricingCatalog.EstimateVideoCost("leonardo-motion-2", 0));
        Assert.Equal(0m, PricingCatalog.EstimateVideoCost("", 5));
    }

    [Fact]
    public void LosCreditosDeLeonardoSeConviertenADolares()
    {
        // Es el coste REAL: la API dice cuantos creditos gasto cada generacion.
        Assert.Equal(25 * PricingCatalog.LeonardoCreditUsd,
            PricingCatalog.EstimateCostFromLeonardoCredits(25));

        Assert.Equal(0m, PricingCatalog.EstimateCostFromLeonardoCredits(0));
        Assert.Equal(0m, PricingCatalog.EstimateCostFromLeonardoCredits(-3));
    }
}
