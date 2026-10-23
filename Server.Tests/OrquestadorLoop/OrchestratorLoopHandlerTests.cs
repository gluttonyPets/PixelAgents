using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// El orquestador en modo loop de punta a punta, con un proveedor de mentira y
/// audio real (hace falta ffprobe para medirlo). Lo que se protege: que el
/// numero de vueltas sale de la duracion de la Locucion y del clip del modulo de
/// duracion que hay dentro del rectangulo, y que cada salida emite sus partes de
/// forma que los modulos de imagen y video las vuelven a separar igual.
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
    public async Task LaDuracionSaleSoloDeLaLocucion()
    {
        // Un audio que entra por Instrucciones no cuenta: el tiempo lo marca la Locucion.
        var provider = new FakeTextProvider(PlanJson(2));
        var ctx = CreateContext(provider, [Path.Combine(_workspace, "voz.mp3")], mediaPort: "input_prompt");

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("La entrada Locucion no trae ningun audio ni video", result.Error);
    }

    [Fact]
    public async Task SinNadaConDuracion_FallaExplicandoQueConectar()
    {
        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("conecta ahi la voz en off", result.Error);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task SinRectangulo_Falla()
    {
        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider, withFrame: false);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("no tiene rectangulo de loop", result.Error);
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
        Assert.Contains("una imagen o un texto no tienen duracion", result.Error);
    }

    [Fact]
    public async Task SinModuloDeDuracionDentroDelLoop_Falla()
    {
        // Solo la imagen dentro del rectangulo: nada dice cuanto cubre cada vuelta.
        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider, [Path.Combine(_workspace, "voz.mp3")], withVideo: false);

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("no hay ningun modulo de duracion", result.Error);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ModuloDeDuracionSinDuracionFija_PideFijarLosSegundos()
    {
        if (!MediaFixtures.HasFfmpeg) return;

        // Un modulo de voz dentro del loop es de duracion, pero no fija.
        var provider = new FakeTextProvider(PlanJson(1));
        var ctx = CreateContext(provider, [MediaFixtures.Silence(_workspace, "voz.mp3", 4)],
            withVideo: false, extraMember: ("Audio", "gpt-4o-mini-tts"));

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([provider])).ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("Fija los segundos por vuelta", result.Error);
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
        Dictionary<string, object>? extraConfig = null, bool withVideo = true,
        string mediaPort = LoopPlanner.NarrationPort, bool withFrame = true,
        (string Type, string Model)? extraMember = null)
    {
        durationFiles ??= [];
        var orchestrator = LoopPlannerTests.Node("Orchestrator", "gpt-5.4", "Orquestador loop");
        orchestrator.AiModule.ApiKey = new ApiKey { Id = Guid.NewGuid(), EncryptedKey = "test-key" };
        orchestrator.ProjectModule.OrchestratorOutputs = LoopPlannerTests.Outputs();

        // El rectangulo de loop, con lo que se repite dentro: imagen -> video.
        var members = new List<ModuleNode>();
        var image = LoopPlannerTests.Node("Image", "gpt-image-2");
        LoopPlannerTests.Connect(orchestrator, "output_2", image, "input_prompt");
        members.Add(image);
        if (withVideo)
        {
            var video = LoopPlannerTests.Node("Video", "leonardo-motion-2", "Leonardo Video");
            LoopPlannerTests.Connect(image, "output_image", video, "input_image");
            members.Add(video);
        }
        if (extraMember is { } extra)
            members.Add(LoopPlannerTests.Node(extra.Type, extra.Model));
        if (withFrame)
            LoopPlannerTests.Frame(orchestrator, members);

        var inputs = new Dictionary<string, List<PortData>>
        {
            ["input_prompt"] = [new PortData { DataType = "text", TextContent = "Guion: curiosidades de los pulpos." }],
        };

        // La voz en off entra por Locucion: su duracion decide las vueltas.
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
                        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
                    },
                ],
            })];

        if (narration is not null)
            inputs[LoopPlanner.NarrationPort] =
                [.. inputs.GetValueOrDefault(LoopPlanner.NarrationPort) ?? [], new PortData { DataType = "text", TextContent = narration }];

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
