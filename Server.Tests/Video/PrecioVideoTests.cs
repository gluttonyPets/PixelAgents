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
    public void CadaResolucionDeCadaModeloDeVideoTieneSuTarifa()
    {
        // El simulador del alta y la estimacion de la ejecucion leen la tarifa de la
        // resolucion elegida: una que faltase saldria a $0 sin avisar.
        foreach (var spec in VideoModelOptions.Specs)
        foreach (var resolution in spec.Resolutions)
        {
            var rate = PricingCatalog.GetVideoRate(spec.ModelId, resolution.Id);

            Assert.True(rate is { PerSecond: > 0 }, $"{spec.ModelId} {resolution.Id} sin tarifa");
            Assert.True(rate!.For(audio: true) >= rate.PerSecond, $"{spec.ModelId} {resolution.Id}: el audio abarata");
        }
    }

    [Fact]
    public void LaResolucionYElAudioCambianElCoste()
    {
        var kling720 = PricingCatalog.EstimateVideoCost("leonardo-kling-3", 5, "720p");
        var kling1080 = PricingCatalog.EstimateVideoCost("leonardo-kling-3", 5, "1080p");
        Assert.True(kling1080 > kling720);

        // Veo dobla el precio con audio.
        Assert.Equal(8 * 0.20m, PricingCatalog.EstimateVideoCost("leonardo-veo-3.1", 8, "720p", audio: false));
        Assert.Equal(8 * 0.40m, PricingCatalog.EstimateVideoCost("leonardo-veo-3.1", 8, "720p", audio: true));
    }

    [Fact]
    public void SinResolucion_SeUsaLaDelModeloPorDefecto()
    {
        // Motion 2.0 arranca a 480p, y es la tarifa que ve la pantalla de modelos.
        Assert.Equal(PricingCatalog.GetVideoRate("leonardo-motion-2", "480p")!.PerSecond,
            PricingCatalog.GetAuxiliaryRate("leonardo-motion-2")!.Amount);
        Assert.Equal(5 * 0.015m, PricingCatalog.EstimateVideoCost("leonardo-motion-2", 5));
    }

    [Fact]
    public void LasTarifasViajanALaPantallaConLasOpciones()
    {
        var veo = VideoModelOptions.Describe().Single(o => o.ModelId == "leonardo-veo-3.1");
        var hd = veo.Resolutions.Single(r => r.Id == "720p");

        Assert.Equal(0.20m, hd.PerSecondUsd);
        Assert.Equal(0.40m, hd.PerSecondWithAudioUsd);
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
