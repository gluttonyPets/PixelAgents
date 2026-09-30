using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.TextoAVoz;

/// <summary>
/// Modulo de voz: una locucion por toma cuando el guion viene repartido, para
/// que el montaje pueda cuadrar cada frase con su clip.
/// </summary>
public class AudioModuleHandlerTests
{
    [Fact]
    public async Task GuionConTomas_UnAudioPorTomaEnOrden()
    {
        var provider = new FakeTtsProvider();
        var ctx = CreateContext("""
            Tono calido y cercano.

            ===TOMA 1===
            Tu perro deja pelo en todas partes.
            ===TOMA 2===
            Con este cepillo, el sofa queda limpio.
            ===TOMA 3===
            Pruebalo hoy.
            """);

        var result = await new AudioModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(["voz_1.mp3", "voz_2.mp3", "voz_3.mp3"], result.ProducedFiles.Select(f => f.FileName));
        Assert.Equal(3, provider.Calls.Count);
        Assert.Equal("Tu perro deja pelo en todas partes.", provider.Calls[0].Input);
        Assert.Equal("Pruebalo hoy.", provider.Calls[2].Input);
        // El preambulo es contexto: no se locuta en ninguna toma.
        Assert.DoesNotContain(provider.Calls, c => c.Input.Contains("Tono calido"));
        Assert.Equal(3, (int)result.Output!.Metadata["takes"]);
    }

    [Fact]
    public async Task TextoSinTomas_UnSoloAudio()
    {
        var provider = new FakeTtsProvider();
        var ctx = CreateContext("Tu perro deja pelo en todas partes. Pruebalo hoy.");

        var result = await new AudioModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Single(result.ProducedFiles);
        Assert.Equal("output.mp3", result.ProducedFiles[0].FileName);
        Assert.Equal("Tu perro deja pelo en todas partes. Pruebalo hoy.", provider.Calls.Single().Input);
    }

    [Fact]
    public async Task SiFallaUnaToma_FallaElModulo()
    {
        // Entregar las otras dos correria la voz de la toma 3 al clip 2.
        var provider = new FakeTtsProvider { FailOnCall = 2 };
        var ctx = CreateContext("===TOMA 1===\nuno\n===TOMA 2===\ndos\n===TOMA 3===\ntres");

        var result = await new AudioModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("Toma 2", result.Error);
    }

    [Fact]
    public async Task ElCosteEsLaSumaDeLasTomas()
    {
        var provider = new FakeTtsProvider { CostPerCall = 0.002m };
        var ctx = CreateContext("===TOMA 1===\nuno\n===TOMA 2===\ndos");

        var result = await new AudioModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(0.004m, result.Cost);
    }

    [Fact]
    public void ElMarkdownNoSeLocuta()
    {
        var takes = AudioModuleHandler.BuildTakes(["**Oferta** limitada:\n- solo hoy"], out _);

        Assert.Equal("Oferta limitada: solo hoy", takes.Single());
    }

    [Fact]
    public async Task SinTexto_Falla()
    {
        var provider = new FakeTtsProvider();
        var ctx = CreateContext("   ");

        var result = await new AudioModuleHandler(Registry(provider)).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Empty(provider.Calls);
    }

    private static IAiProviderRegistry Registry(IAiProvider provider) => new AiProviderRegistry([provider]);

    private static ModuleExecutionContext CreateContext(string text)
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Voz en off",
            ModuleType = "Audio",
            ProviderType = "OpenAI",
            ModelName = "gpt-4o-mini-tts",
            ApiKey = new ApiKey { Id = Guid.NewGuid(), EncryptedKey = "test-key" },
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = Path.GetTempPath(),
            MediaRoot = Path.GetTempPath(),
            Config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["voice"] = "nova" },
            InputsByPort = new Dictionary<string, List<PortData>>
            {
                [AudioModuleHandler.InputPort] = [new PortData { DataType = "text", TextContent = text }],
            },
        };
    }

    private sealed class FakeTtsProvider : IAiProvider
    {
        public List<AiExecutionContext> Calls { get; } = [];
        public int? FailOnCall { get; init; }
        public decimal CostPerCall { get; init; }

        public string ProviderType => "OpenAI";
        public IEnumerable<string> SupportedModuleTypes => ["Audio"];

        public Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            Calls.Add(context);
            if (FailOnCall == Calls.Count)
                return Task.FromResult(AiResult.Fail("cuota agotada"));

            var result = AiResult.OkFile([1, 2, 3], "audio/mpeg");
            result.EstimatedCost = CostPerCall;
            return Task.FromResult(result);
        }

        public Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey) =>
            Task.FromResult((true, (string?)null));
    }
}
