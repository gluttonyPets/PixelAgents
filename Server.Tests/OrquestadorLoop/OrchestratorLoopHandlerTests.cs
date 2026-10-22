using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// El orquestador en modo loop de punta a punta, con un proveedor de mentira y
/// audio real (hace falta ffprobe para medirlo). Lo que se protege: que el
/// numero de partes sale de la duracion de la entrada y del clip del video de
/// detras, y que cada salida emite sus partes de forma que los modulos de
/// imagen y video las vuelven a separar igual.
/// </summary>
public class OrchestratorLoopHandlerTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"pixelagents_loop_{Guid.NewGuid():N}");

    public OrchestratorLoopHandlerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task VozDe12Segundos_ConClipsDe5_PideTresPartesYLasRepartePorSalida()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(3));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 12)]);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(3, (int)result.Output!.Metadata["parts"]);
        Assert.Equal(5d, (double)result.Output.Metadata["clipSeconds"]);

        // La IA recibe cuantas partes y que tramos cubren.
        var systemPrompt = provider.Calls.Single().Configuration["systemPrompt"].ToString()!;
        Assert.Contains("EXACTLY 3 parts", systemPrompt);
        Assert.Contains("Part 3: 00:10.0 - 00:12.0", systemPrompt);

        // Cada salida lleva sus tres partes y aguas abajo se separan igual.
        Assert.Equal(2, result.Output.Items.Count);
        var videoPrompts = MultiImagePrompt.Split([result.Output.Items[1].Content]).Segments;
        Assert.Equal(["video 1", "video 2", "video 3"], videoPrompts);
    }

    [Fact]
    public async Task SiLaIaDevuelvePartesDeMenos_SeRepiteUnaVez()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(2), PlanJson(3));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 12)]);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Contains("EXACTAMENTE 3 partes", provider.Calls[1].Input);
    }

    [Fact]
    public async Task SiTampocoAciertaAlReintentar_FallaDiciendoPorQue()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(2), PlanJson(2));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 12)]);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("se pidieron 3 partes y llegaron 2", result.Error);
    }

    [Fact]
    public async Task LaLocucionLlegaRepartidaPorTramos()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(2));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 9)],
            narration: "[00:00.0 - 00:04.0] Los pulpos tienen tres corazones.\n[00:05.0 - 00:09.0] Y sangre azul.");

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        var systemPrompt = provider.Calls.Single().Configuration["systemPrompt"].ToString()!;
        Assert.Contains("Part 1: 00:00.0 - 00:05.0 (5 s) | narration heard in this part: \"Los pulpos tienen tres corazones.\"", systemPrompt);
        Assert.Contains("Part 2: 00:05.0 - 00:09.0 (4 s) | narration heard in this part: \"Y sangre azul.\"", systemPrompt);
    }

    [Fact]
    public async Task LosSegundosFijadosEnElNodo_MandanSobreElVideo()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(4));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 12)],
            extraConfig: new() { [LoopPlanner.ClipSecondsKey] = "3" });

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(4, (int)result.Output!.Metadata["parts"]);
    }

    [Fact]
    public async Task VariosAudios_SeSumanSusDuraciones()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        // El modulo de voz con tomas entrega voz_1..voz_n: la duracion es la suma.
        var provider = new FakeTextProvider(PlanJson(3));
        var ctx = CreateContext(provider,
        [
            MediaFixtures.Silence(_workspace, "voz_1.mp3", 6),
            MediaFixtures.Silence(_workspace, "voz_2.mp3", 6),
        ]);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(3, (int)result.Output!.Metadata["parts"]);
    }

    [Fact]
    public async Task LaDuracionSeMideLlegueporLaEntradaQueLlegue()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(2));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 9)],
            mediaPort: LoopPlanner.NarrationPort);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(2, (int)result.Output!.Metadata["parts"]);
    }

    [Fact]
    public async Task SinNadaConDuracion_FallaExplicandoQueConectar()
    {
        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("modulo de duracion (audio o video)", result.Error);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task UnaImagenNoTieneDuracion()
    {
        var provider = new FakeTextProvider(PlanJson(1));
        var image = Path.Combine(_workspace, "foto.png");
        File.WriteAllBytes(image, [1, 2, 3]);
        var ctx = CreateContext(provider, [image]);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("Una imagen o un texto no tienen duracion", result.Error);
    }

    [Fact]
    public async Task SinVideoDetras_PideFijarLosSegundos()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 4)], withVideo: false);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("segundos por parte", result.Error);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task SinModoLoop_ElOrquestadorSigueComoAntes()
    {
        var provider = new FakeTextProvider("""
            {"summary":"s","outputs":[{"outputKey":"output_1","content":"tema"},{"outputKey":"output_2","content":"video"}]}
            """);
        var ctx = CreateContext(provider, extraConfig: new() { [LoopPlanner.ModeKey] = false });

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal("video", result.Output!.Items[1].Content);
    }

    // ── Helpers ──

    private static string PlanJson(int parts) =>
        "{\"summary\":\"pulpos\",\"parts\":["
        + string.Join(",", Enumerable.Range(1, parts).Select(i =>
            $"{{\"index\":{i},\"output_1\":\"tema {i}\",\"output_2\":\"video {i}\"}}"))
        + "]}";

    private ModuleExecutionContext CreateContext(
        IAiProvider provider, string[]? durationFiles = null, string? narration = null,
        Dictionary<string, object>? extraConfig = null, bool withVideo = true, string mediaPort = "input_prompt")
    {
        durationFiles ??= [];
        var orchestrator = LoopPlannerTests.Node("Orchestrator", "gpt-5.4", "Orquestador loop");
        orchestrator.AiModule.ApiKey = new ApiKey { Id = Guid.NewGuid(), EncryptedKey = "test-key" };
        orchestrator.ProjectModule.OrchestratorOutputs = LoopPlannerTests.Outputs();

        // Inicio de la cadena que hay detras: descripcion -> imagen -> video.
        var image = LoopPlannerTests.Node("Image", "gpt-image-2");
        LoopPlannerTests.Connect(orchestrator, "output_2", image, "input_prompt");
        if (withVideo)
            LoopPlannerTests.Connect(image, "output_image", LoopPlannerTests.Node("Video", "leonardo-motion-2"), "input_image");

        var inputs = new Dictionary<string, List<PortData>>
        {
            ["input_prompt"] = [new PortData { DataType = "text", TextContent = "Guion: curiosidades de los pulpos." }],
        };

        // La duracion no tiene puerto propio: el audio llega por una entrada
        // cualquiera (lo normal, el TTS conectado a Instrucciones).
        if (durationFiles.Length > 0)
            inputs[mediaPort] = [.. inputs.GetValueOrDefault(mediaPort) ?? [], .. durationFiles.Select(path => new PortData
            {
                DataType = "audio",
                Files =
                [
                    new OutputFile
                    {
                        FileName = path,
                        ContentType = path.EndsWith(".png") ? "image/png" : "audio/mpeg",
                        FileSize = new FileInfo(path).Length,
                    },
                ],
            })];

        if (narration is not null)
            inputs[LoopPlanner.NarrationPort] = [new PortData { DataType = "text", TextContent = narration }];

        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [LoopPlanner.ModeKey] = true,
        };
        foreach (var (k, v) in extraConfig ?? []) config[k] = v;

        return new ModuleExecutionContext
        {
            Node = orchestrator,
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

    /// <summary>Proveedor de texto que contesta, por orden, las respuestas dadas.</summary>
    private sealed class FakeTextProvider(params string[] responses) : IAiProvider
    {
        public List<AiExecutionContext> Calls { get; } = [];

        public string ProviderType => "OpenAI";
        public IEnumerable<string> SupportedModuleTypes => ["Text"];

        public Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            Calls.Add(context);
            var response = responses[Math.Min(Calls.Count, responses.Length) - 1];
            return Task.FromResult(AiResult.Ok(response));
        }

        public Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey) =>
            Task.FromResult((true, (string?)null));
    }
}
