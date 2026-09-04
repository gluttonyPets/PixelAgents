using System.Text.RegularExpressions;
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
            5.00m, 30.00m, null, null, null, null, null, null, "Text",
            new ModelLifecycleResponse("gpt-5.6-sol", "OpenAI", "active",
                null, null, null, null, null, true));

        Assert.Equal("text", texto.Kind);
        Assert.NotNull(texto.InputPerMTok);
        Assert.Null(texto.ImageMedium);
        Assert.Null(texto.AuxAmount);

        var imagen = new ModelPriceResponse(
            "gpt-image-2", "GPT Image 2", "OpenAI", "image",
            null, null, 0.0082m, 0.0317m, 0.1248m, null, null, null, "Image",
            new ModelLifecycleResponse("gpt-image-2", "OpenAI", "active",
                null, null, null, null, null, null));

        Assert.Equal("image", imagen.Kind);
        Assert.Null(imagen.InputPerMTok);
        Assert.NotNull(imagen.ImageMedium);

        var audio = new ModelPriceResponse(
            "tts-1", "TTS-1", "OpenAI", "other",
            null, null, null, null, null, 15.00m, "1M caracteres", null, "Audio",
            new ModelLifecycleResponse("tts-1", "OpenAI", "active",
                null, null, null, null, null, null));

        Assert.Equal("other", audio.Kind);
        Assert.Null(audio.InputPerMTok);
        Assert.Null(audio.ImageMedium);
        Assert.Equal("1M caracteres", audio.AuxUnit);
    }

    [Fact]
    public void ElCatalogoDelServidorTieneLosMismosModelosQueElDelCliente()
    {
        // Los dos catalogos se mantienen a mano y se desincronizaron: el del servidor
        // no tenia los modelos de embeddings, audio, transcripcion ni Canva, asi que
        // no salian en la pantalla de precios ni los ofrecia el bot de Telegram. Un
        // modelo que no aparece en ningun sitio es indistinguible de uno que no existe.
        var razor = File.ReadAllText(RutaDelCatalogoDelCliente());

        var enElCliente = Regex
            .Matches(razor, @"new\(""(?<id>[^""]+)"",\s*""[^""]+"",\s*""(?<provider>[^""]+)""")
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Si el parseo deja de encontrar modelos, el test dejaria de comprobar nada.
        Assert.True(enElCliente.Count > 50,
            $"solo se han parseado {enElCliente.Count} modelos del catalogo del cliente");

        var enElServidor = ModelCatalog.AllModels
            .Select(m => m.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var faltanEnElServidor = enElCliente.Except(enElServidor, StringComparer.OrdinalIgnoreCase).ToList();
        var faltanEnElCliente = enElServidor.Except(enElCliente, StringComparer.OrdinalIgnoreCase).ToList();

        Assert.True(faltanEnElServidor.Count == 0,
            "en ModelCatalog.cs faltan: " + string.Join(", ", faltanEnElServidor));
        Assert.True(faltanEnElCliente.Count == 0,
            "en Modules.razor faltan: " + string.Join(", ", faltanEnElCliente));
    }

    [Fact]
    public void LasCapacidadesDelServidorCoincidenConLasDelCliente()
    {
        // Las capacidades vivian solo en Modules.razor, donde ninguna API podia leerlas.
        // Ahora tambien estan en el catalogo del servidor, que es de donde las saca la
        // pantalla de modelos para filtrar por "lo que necesito". Si se desincronizan,
        // el filtro esconde modelos que si valen y nadie sabe por que.
        var razor = File.ReadAllText(RutaDelCatalogoDelCliente());

        var enElCliente = Regex
            .Matches(razor,
                @"new\(""(?<id>[^""]+)"",\s*""[^""]+"",\s*""[^""]+"",\s*\[[^\]]*\],\s*\[(?<caps>[^\]]*)\]")
            .ToDictionary(
                m => m.Groups["id"].Value,
                m => m.Groups["caps"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(c => c.Trim().Trim('"'))
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        Assert.True(enElCliente.Count > 50,
            $"solo se han parseado {enElCliente.Count} modelos del catalogo del cliente");

        foreach (var model in ModelCatalog.AllModels)
        {
            Assert.True(enElCliente.TryGetValue(model.Id, out var esperadas),
                $"{model.Id} no aparece en Modules.razor");

            Assert.True(
                esperadas.OrderBy(c => c).SequenceEqual((model.Capabilities ?? []).OrderBy(c => c)),
                $"{model.Id}: en Modules.razor tiene [{string.Join(", ", esperadas)}] y en "
                + $"ModelCatalog.cs [{string.Join(", ", model.Capabilities ?? [])}]");
        }
    }

    [Fact]
    public void TodoModeloDeTextoDeclaraSuVentanaDeContexto()
    {
        // Es la unica medida de capacidad comparable entre proveedores, y la grafica de
        // precio/capacidad deja fuera a los modelos que no la traen: uno sin contexto
        // desaparece de la comparativa sin decir por que.
        foreach (var model in ModelCatalog.AllModels
            .Where(m => m.Types.Contains("Text", StringComparer.OrdinalIgnoreCase)))
        {
            Assert.True(model.ContextTokens is > 0,
                $"{model.Id} es de texto y no dice cuanto contexto admite");
        }
    }

    [Fact]
    public void LaTarifaQueViajaAlClienteLlevaCapacidadesYContexto()
    {
        // La pantalla filtra por capacidades y pinta la nube precio/contexto con lo que
        // venga en este DTO: si llegase vacio, los filtros no encontrarian nada y la
        // grafica saldria en blanco sin ningun error.
        var modelo = ModelCatalog.AllModels.First(m => m.Id == "gpt-5.6-sol");
        var rates = ModelCatalogService.ResolveRates(modelo);

        var dto = new ModelPriceResponse(
            modelo.Id, modelo.DisplayName, modelo.Provider, rates.Kind,
            rates.InputPerMTok, rates.OutputPerMTok, null, null, null, null, null, null, "Text",
            new ModelLifecycleResponse(modelo.Id, modelo.Provider, "active",
                null, null, null, null, null, true),
            modelo.Capabilities, modelo.ContextTokens);

        Assert.Contains("reasoning", dto.Capabilities!);
        Assert.Equal(1_000_000, dto.ContextTokens);
    }

    private static string RutaDelCatalogoDelCliente()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Client", "Pages", "Modules.razor")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "Client", "Pages", "Modules.razor");
    }

    [Theory]
    [InlineData("text-embedding-3-small", 0.02, "1M tokens")]
    [InlineData("text-embedding-3-large", 0.13, "1M tokens")]
    [InlineData("tts-1", 15.00, "1M caracteres")]
    [InlineData("tts-1-hd", 30.00, "1M caracteres")]
    [InlineData("whisper-1", 0.006, "minuto")]
    [InlineData("gpt-4o-transcribe", 0.006, "minuto")]
    [InlineData("gpt-4o-mini-transcribe", 0.003, "minuto")]
    public void LosModelosQueNoVanPorTokensTraenSuPropiaUnidad(
        string modelo, double importe, string unidad)
    {
        // Mostrar un TTS que cobra por caracter como si cobrase por token seria
        // equivocarse por tres ordenes de magnitud.
        var rate = PricingCatalog.GetAuxiliaryRate(modelo);

        Assert.NotNull(rate);
        Assert.Equal((decimal)importe, rate!.Amount);
        Assert.Equal(unidad, rate.Unit);
    }

    [Fact]
    public void TodoModeloDelCatalogoTieneTarifaOSeSabePorQueNo()
    {
        // Canva se paga por suscripcion, no por llamada: es el unico caso legitimo
        // de modelo sin tarifa. Cualquier otro sin precio es un olvido.
        foreach (var model in ModelCatalog.AllModels)
        {
            if (model.Types.Contains("Text", StringComparer.OrdinalIgnoreCase)
                || model.Types.Contains("Image", StringComparer.OrdinalIgnoreCase))
                continue;

            if (string.Equals(model.Provider, "Canva", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Null(PricingCatalog.GetAuxiliaryRate(model.Id));
                continue;
            }

            Assert.True(PricingCatalog.GetAuxiliaryRate(model.Id) is not null,
                $"{model.Id} esta en el catalogo y no tiene tarifa ni motivo para no tenerla");
        }
    }
}
