using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.Video;

/// <summary>
/// Modulo de video: N imagenes -> N clips.
///
/// Lo que se protege aqui es lo mismo que en el modulo de imagen —una llamada
/// por pieza, con SU prompt— mas dos cosas propias del video: que las llamadas
/// no vayan en serie (cinco clips en serie no caben en el timeout de 10 min por
/// modulo del executor) y que un clip fallido no se lleve por delante a los que
/// si salieron, porque esos ya estan pagados.
/// </summary>
public class VideoModuleHandlerTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"pixelagents_test_{Guid.NewGuid():N}");

    public VideoModuleHandlerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CadaImagenGeneraSuClip()
    {
        var provider = new FakeVideoProvider();
        var ctx = CreateContext(provider, imageCount: 3);

        var result = await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal(3, result.ProducedFiles.Count);
        Assert.Equal("clip_1.mp4", result.ProducedFiles[0].FileName);
        Assert.Equal("clip_3.mp4", result.ProducedFiles[2].FileName);
        Assert.All(result.ProducedFiles, f => Assert.Equal("video/mp4", f.ContentType));
    }

    [Fact]
    public async Task CadaLlamadaLlevaUnaSolaImagen()
    {
        // Si se mandasen todas juntas, el proveedor animaria solo la primera y
        // las demas se pagarian sin aparecer en ningun sitio.
        var provider = new FakeVideoProvider();
        var ctx = CreateContext(provider, imageCount: 3);

        await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.All(provider.Calls, c => Assert.Single(c.InputFiles!));
    }

    [Fact]
    public async Task ConTextoSegmentado_CadaClipRecibeSuParteYElContextoComun()
    {
        var provider = new FakeVideoProvider();
        var ctx = CreateContext(provider, imageCount: 2, upstreamTexts:
        [
            "Camara estable, ritmo lento.\n\n===IMAGEN 1===\nEl perro empieza a correr."
            + "\n\n===IMAGEN 2===\nLa camara sube hacia el cielo."
        ]);

        await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Contains("El perro empieza a correr", provider.Calls[0].Input);
        Assert.DoesNotContain("sube hacia el cielo", provider.Calls[0].Input);
        Assert.Contains("sube hacia el cielo", provider.Calls[1].Input);
        Assert.All(provider.Calls, c => Assert.Contains("ritmo lento", c.Input));
    }

    [Fact]
    public async Task ElMovimientoConfiguradoEnElNodo_ViajaEnTodasLasLlamadas()
    {
        var provider = new FakeVideoProvider();
        var ctx = CreateContext(provider, imageCount: 2,
            extraConfig: new() { ["videoPrompt"] = "Zoom lento de acercamiento." });

        await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.All(provider.Calls, c => Assert.Contains("Zoom lento", c.Input));
    }

    [Fact]
    public async Task LasLlamadasSeSolapan()
    {
        // En serie, cinco clips de varios minutos se comen el timeout de 10 min por
        // modulo del executor. Se mide el solapamiento y no el reloj de pared: el
        // tiempo depende de la maquina, pero que dos llamadas coincidan vivas no.
        var provider = new FakeVideoProvider { DelayMs = 200 };
        var ctx = CreateContext(provider, imageCount: 3);

        await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.True(provider.MaxConcurrent >= 2,
            $"las llamadas van en serie: como mucho hubo {provider.MaxConcurrent} a la vez");
    }

    [Fact]
    public async Task SiFallaUnClip_SeEntreganLosDemasYElFalloQuedaAnotado()
    {
        var provider = new FakeVideoProvider { FailOnCall = 2 };
        var ctx = CreateContext(provider, imageCount: 3);

        var result = await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(2, result.ProducedFiles.Count);
        Assert.Equal(1, (int)result.Output!.Metadata["failed"]);
        Assert.Equal(3, (int)result.Output.Metadata["requested"]);
    }

    [Fact]
    public async Task SiFallanTodos_ElModuloFalla()
    {
        var provider = new FakeVideoProvider { FailOnCall = 0 };
        var ctx = CreateContext(provider, imageCount: 2);

        var result = await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("cuota agotada", result.Error);
    }

    [Fact]
    public async Task SinImagenes_ElErrorDiceQuePuertoHayQueConectar()
    {
        var provider = new FakeVideoProvider();
        var ctx = CreateContext(provider, imageCount: 0);

        var result = await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("input_image", result.Error);
    }

    [Fact]
    public async Task ElCosteEsLaSumaDeLosClipsQueSalieron()
    {
        var provider = new FakeVideoProvider { CostPerCall = 0.075m, FailOnCall = 3 };
        var ctx = CreateContext(provider, imageCount: 3);

        var result = await new VideoModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(0.150m, result.Cost);
    }

    // ── Helpers ──

    private static IAiProviderRegistry Registry(IAiProvider provider) =>
        new AiProviderRegistry([provider]);

    private ModuleExecutionContext CreateContext(
        IAiProvider provider,
        int imageCount,
        List<string>? upstreamTexts = null,
        Dictionary<string, object>? extraConfig = null)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Animar imagen",
            ModuleType = "Video",
            ProviderType = provider.ProviderType,
            ModelName = "leonardo-motion-2",
            ApiKey = new ApiKey { Id = Guid.NewGuid(), EncryptedKey = "test-key" },
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in extraConfig ?? []) config[k] = v;

        var inputs = new Dictionary<string, List<PortData>>();

        if (imageCount > 0)
        {
            var files = new List<OutputFile>();
            for (var i = 1; i <= imageCount; i++)
            {
                var name = $"image_{i}.png";
                File.WriteAllBytes(Path.Combine(_workspace, name), [(byte)i, 2, 3]);
                files.Add(new OutputFile { FileName = name, ContentType = "image/png", FileSize = 3 });
            }

            inputs["input_image"] = [new PortData { DataType = "image", Files = files }];
        }

        if (upstreamTexts is { Count: > 0 })
            inputs["input_prompt"] = upstreamTexts
                .Select(t => new PortData { DataType = "text", TextContent = t })
                .ToList();

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = _workspace,
            MediaRoot = _workspace,
            Config = config,
            InputsByPort = inputs,
        };
    }

    private sealed class FakeVideoProvider : IAiProvider
    {
        private readonly object _lock = new();
        private int _running;

        public List<AiExecutionContext> Calls { get; } = [];

        /// <summary>Mayor numero de llamadas solapadas que ha visto.</summary>
        public int MaxConcurrent { get; private set; }

        /// <summary>Numero de llamada (base 1) que falla; 0 = fallan todas.</summary>
        public int? FailOnCall { get; init; }

        public int DelayMs { get; init; }
        public decimal CostPerCall { get; init; }

        public string ProviderType => "LeonardoAI";
        public IEnumerable<string> SupportedModuleTypes => ["Image", "Video"];

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            int callNumber;
            lock (_lock)
            {
                Calls.Add(context);
                callNumber = Calls.Count;
                _running++;
                if (_running > MaxConcurrent) MaxConcurrent = _running;
            }

            try
            {
                if (DelayMs > 0) await Task.Delay(DelayMs, context.CancellationToken);

                if (FailOnCall is int fail && (fail == 0 || fail == callNumber))
                    return AiResult.Fail("cuota agotada");

                var result = AiResult.OkFile([9, 9, 9], "video/mp4", new Dictionary<string, object>
                {
                    ["durationSeconds"] = 5,
                });
                result.EstimatedCost = CostPerCall;
                return result;
            }
            finally
            {
                lock (_lock) _running--;
            }
        }

        public Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey) =>
            Task.FromResult((true, (string?)null));
    }
}
