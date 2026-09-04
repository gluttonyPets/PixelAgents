using Moq;
using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.ImagenMultiple;

/// <summary>
/// Comportamiento del modulo de imagen cuando tiene que generar varias.
///
/// Lo que se protege aqui es que N imagenes salgan de N llamadas con N prompts
/// distintos. Pedirlas en una sola llamada con n=N devuelve N copias del mismo
/// prompt (para la API "n" son muestras, no partes), que es justo el fallo que
/// hacia que cada imagen llevara dentro todas las secciones del diseño.
/// </summary>
public class ImageModuleHandlerTests
{
    [Fact]
    public async Task ConTextoSegmentado_HaceUnaLlamadaPorParte()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts:
        [
            "Estilo: fotografia luminosa.\n\n===IMAGEN 1===\nSofa lleno de pelos.\n\n===IMAGEN 2===\nSofa limpio."
        ]);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(2, provider.Calls.Count);

        // Cada llamada lleva su parte y solo la suya.
        Assert.Contains("Sofa lleno de pelos", provider.Calls[0].Input);
        Assert.DoesNotContain("Sofa limpio", provider.Calls[0].Input);
        Assert.Contains("Sofa limpio", provider.Calls[1].Input);
        Assert.DoesNotContain("Sofa lleno de pelos", provider.Calls[1].Input);

        // Y el contexto comun viaja en las dos.
        Assert.All(provider.Calls, c => Assert.Contains("fotografia luminosa", c.Input));

        // Nunca se le pide al proveedor un lote de varias.
        Assert.All(provider.Calls, c => Assert.Equal(1, MultiImagePrompt.ReadImageCount(c.Configuration)));

        Assert.Equal(2, result.ProducedFiles.Count);
        Assert.Equal("image_1.png", result.ProducedFiles[0].FileName);
        Assert.Equal("image_2.png", result.ProducedFiles[1].FileName);
    }

    [Fact]
    public async Task ElPromptPropioDelModulo_ViajaEnTodasLasLlamadas()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2,
            upstreamTexts: ["===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos"],
            extraConfig: new() { ["imagePrompt"] = "Formato vertical 1024x1536." });

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.All(provider.Calls, c => Assert.Contains("Formato vertical", c.Input));
    }

    [Fact]
    public async Task ElTextoDeOtraConexionSinMarcas_NoSeMeteEnUnaSolaParte()
    {
        // El indice del modulo Directorio entra por el mismo puerto que el plan.
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts:
        [
            "Biblioteca: cepillo.png (cepillo verde)",
            "===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos",
        ]);

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(2, provider.Calls.Count);
        Assert.All(provider.Calls, c => Assert.Contains("Biblioteca", c.Input));
    }

    [Fact]
    public async Task LaParteDeCadaImagenVaDelanteDelContextoComun()
    {
        // Es lo que la salva del recorte: el proveedor trunca por el final.
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2,
            upstreamTexts: ["Contexto comun del diseno.\n\n===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos"]);

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.StartsWith("uno", provider.Calls[0].Input);
        Assert.StartsWith("dos", provider.Calls[1].Input);
    }

    [Fact]
    public async Task ConContextoComunEnorme_LaParteDeCadaImagenSobreviveEntera()
    {
        // El caso real: el indice del Directorio mas el concepto del disenador
        // pasaban del limite del modelo y el recorte se llevaba justo la escena,
        // asi que las dos imagenes salian con el mismo texto.
        var comun = new string('c', 6000);
        var escena1 = "Sofa lleno de pelos con el cepillo al lado.";
        var escena2 = "El mismo sofa ya limpio.";

        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2,
            upstreamTexts: [$"{comun}\n\n===IMAGEN 1===\n{escena1}\n===IMAGEN 2===\n{escena2}"]);

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Contains(escena1, provider.Calls[0].Input);
        Assert.Contains(escena2, provider.Calls[1].Input);
        Assert.DoesNotContain(escena2, provider.Calls[0].Input);

        // Y lo que se manda cabe en el limite del modelo, sin depender de que el
        // proveedor recorte por el final.
        Assert.All(provider.Calls, c => Assert.True(c.Input.Length <= 4000, $"prompt de {c.Input.Length} chars"));
    }

    [Fact]
    public async Task ParteMasLargaQueElLimite_SeRecortaEllaYSeVaElContextoComun()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts:
        [
            $"contexto comun\n\n===IMAGEN 1===\n{new string('a', 6000)}\n===IMAGEN 2===\ncorta"
        ]);

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.True(provider.Calls[0].Input.Length <= 4000);
        Assert.DoesNotContain("contexto comun", provider.Calls[0].Input);
        // La segunda cabe entera y conserva su contexto.
        Assert.Contains("contexto comun", provider.Calls[1].Input);
    }

    [Fact]
    public async Task SinTextoSegmentado_SigueHaciendoUnaSolaLlamada()
    {
        // No se puede repartir lo que no viene separado: se mantiene el
        // comportamiento anterior (y queda el aviso en el log de la ejecucion).
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts: ["Un cartel con dos secciones."]);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Single(provider.Calls);
        Assert.Equal(2, MultiImagePrompt.ReadImageCount(provider.Calls[0].Configuration));
    }

    [Fact]
    public async Task ConUnaSolaSalida_NoCambiaNada()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 1, upstreamTexts: ["Un cepillo verde sobre fondo blanco."]);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Single(provider.Calls);
        Assert.Equal(1, MultiImagePrompt.ReadImageCount(provider.Calls[0].Configuration));
        Assert.Equal("output.png", Assert.Single(result.ProducedFiles).FileName);
    }

    [Fact]
    public async Task MasPartesQueSalidas_SeGeneranLasQueCaben()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2,
            upstreamTexts: ["===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos\n===IMAGEN 3===\ntres"]);

        await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(2, provider.Calls.Count);
        Assert.DoesNotContain("tres", provider.Calls[1].Input);
    }

    [Fact]
    public async Task SiFallaUnaLlamada_SeEntregaElRestoEnVezDePerderloTodo()
    {
        var provider = new FakeImageProvider { FailOnCall = 2 };
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts: ["===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos"]);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Single(result.ProducedFiles);
    }

    [Fact]
    public async Task SiFallanTodasLasLlamadas_ElModuloFalla()
    {
        var provider = new FakeImageProvider { FailOnCall = 0 };
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts: ["===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos"]);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("cuota", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SinPrompt_ElModuloFalla()
    {
        var provider = new FakeImageProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts: []);

        var result = await CreateHandler(provider).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("Sin prompt", result.Error);
        Assert.Empty(provider.Calls);
    }

    // ── Helpers ──

    private static ImageModuleHandler CreateHandler(IAiProvider provider)
    {
        var registry = new Mock<IAiProviderRegistry>();
        registry.Setup(r => r.GetProvider(It.IsAny<string>())).Returns(provider);

        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());

        return new ImageModuleHandler(registry.Object, httpFactory.Object);
    }

    private static ModuleExecutionContext CreateContext(
        IAiProvider provider,
        int imageCount,
        List<string> upstreamTexts,
        Dictionary<string, object>? extraConfig = null)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Generar imagen",
            ModuleType = "Image",
            ProviderType = provider.ProviderType,
            ModelName = "gpt-image-2",
            ApiKey = new ApiKey { Id = Guid.NewGuid(), EncryptedKey = "test-key" },
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["n"] = imageCount,
            ["size"] = "1024x1536",
        };
        foreach (var (k, v) in extraConfig ?? []) config[k] = v;

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = "/tmp",
            MediaRoot = "/tmp",
            Config = config,
            InputsByPort = new Dictionary<string, List<PortData>>
            {
                ["input_prompt"] = upstreamTexts
                    .Select(t => new PortData { DataType = "text", TextContent = t })
                    .ToList(),
            },
        };
    }

    /// <summary>Proveedor de mentira que anota cada llamada recibida.</summary>
    private sealed class FakeImageProvider : IAiProvider
    {
        public List<AiExecutionContext> Calls { get; } = [];

        /// <summary>Numero de llamada (base 1) que falla; 0 = fallan todas.</summary>
        public int? FailOnCall { get; init; }

        public string ProviderType => "OpenAI";
        public IEnumerable<string> SupportedModuleTypes => ["Image"];

        public Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            Calls.Add(context);
            if (FailOnCall is int fail && (fail == 0 || fail == Calls.Count))
                return Task.FromResult(AiResult.Fail("cuota agotada"));

            return Task.FromResult(AiResult.OkFile([1, 2, 3], "image/png"));
        }

        public Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey) =>
            Task.FromResult((true, (string?)null));
    }
}
