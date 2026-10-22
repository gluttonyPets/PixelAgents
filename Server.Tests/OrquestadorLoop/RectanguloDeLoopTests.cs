using System.Text.Json;
using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Rectangulo de loop: que modulos quedan dentro, quien lo controla, que el
/// cuerpo espera al orquestador, como se corta lo que llega a cada vuelta y como
/// se juntan las N vueltas en el resultado del modulo.
/// </summary>
public class RectanguloDeLoopTests
{
    // ── Quien esta dentro ──

    [Fact]
    public void ElCuerpoSonLosModulosDentroYElControladorElOrquestadorLoop()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, """{"loopMode":true}""");
        var frame = p.Add(LoopFrames.ModuleType, 330, 60, """{"frameWidth":540,"frameHeight":200}""");
        var image = p.Add("Image", 370, 120);
        var video = p.Add("Video", 610, 120, model: "leonardo-motion-2");
        var montage = p.Add("VideoAssembly", 920, 300);
        p.Connect(orch, "output_2", image, "input_prompt");
        p.Connect(orch, "output_1", video, "input_prompt");
        p.Connect(image, "output_image", video, "input_image");
        p.Connect(frame, "output", montage, "input_videos");

        var graph = p.Build();
        var info = graph.Nodes[frame.Id].OwnedLoopFrame!;

        Assert.Equal(new HashSet<Guid> { image.Id, video.Id }, info.Members.Select(m => m.ModuleId).ToHashSet());
        Assert.Equal(orch.Id, info.Driver!.ModuleId);
        Assert.Same(info, graph.Nodes[video.Id].LoopFrame);
        Assert.Null(graph.Nodes[montage.Id].LoopFrame);

        // El ultimo del cuerpo (el video) alimenta al rectangulo por el puerto interno.
        var body = Assert.Single(graph.Nodes[frame.Id].InputPorts, ip => ip.PortId == LoopFrames.BodyPort);
        Assert.Equal(video.Id, Assert.Single(body.Connections).SourceNode.ModuleId);
    }

    [Fact]
    public void UnOrquestadorSinModoLoop_NoControlaElRectangulo()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 0, 0);
        var frame = p.Add(LoopFrames.ModuleType, 300, 0);
        var image = p.Add("Image", 320, 40);
        p.Connect(orch, "output_1", image, "input_prompt");

        var info = p.Build().Nodes[frame.Id].OwnedLoopFrame!;

        Assert.Null(info.Driver);
        Assert.Single(info.Members);
    }

    [Fact]
    public void DentroSeDecidePorLaEsquinaSuperiorIzquierda()
    {
        var frame = new ProjectModule { Id = Guid.NewGuid(), PosX = 100, PosY = 100, Configuration = """{"frameWidth":200,"frameHeight":100}""" };

        Assert.True(LoopFrames.Contains(frame, new ProjectModule { Id = Guid.NewGuid(), PosX = 100, PosY = 100 }));
        Assert.True(LoopFrames.Contains(frame, new ProjectModule { Id = Guid.NewGuid(), PosX = 300, PosY = 200 }));
        Assert.False(LoopFrames.Contains(frame, new ProjectModule { Id = Guid.NewGuid(), PosX = 301, PosY = 150 }));
        Assert.False(LoopFrames.Contains(frame, new ProjectModule { Id = Guid.NewGuid(), PosX = 90, PosY = 150 }));
        // Sin tamano guardado vale el de por defecto.
        Assert.Equal((LoopFrames.DefaultWidth, LoopFrames.DefaultHeight), LoopFrames.ReadSize(null));
    }

    [Fact]
    public void ElCuerpoEsperaAlOrquestadorAunqueNoRecibaNadaDeEl()
    {
        // Imagen dentro del rectangulo alimentada desde fuera por un texto: sin
        // esperar al orquestador no sabria cuantas vueltas dar.
        var p = new Pipeline();
        var start = p.Add("Start", -400, 0);
        var text = p.Add("Text", -200, 0);
        var orch = p.Add("Orchestrator", 0, 300, """{"loopMode":true}""");
        var frame = p.Add(LoopFrames.ModuleType, 300, 0);
        var image = p.Add("Image", 320, 40);
        var video = p.Add("Video", 520, 40);
        p.Connect(start, "output_prompt", text, "input_prompt");
        p.Connect(text, "output_text", image, "input_prompt");
        p.Connect(start, "output_prompt", orch, "input_prompt");
        p.Connect(orch, "output_1", video, "input_prompt");
        p.Connect(image, "output_image", video, "input_image");

        var graph = p.Build();
        var imageNode = graph.Nodes[image.Id];
        var textNode = graph.Nodes[text.Id];
        textNode.Status = NodeStatus.Completed;
        textNode.Output = new StepOutput { Type = "text", Content = "===TOMA 1===\na\n===TOMA 2===\nb" };
        graph.CompleteNodeAndPrepareDownstream(textNode);

        Assert.Equal(NodeStatus.Pending, imageNode.Status);

        var orchNode = graph.Nodes[orch.Id];
        orchNode.Status = NodeStatus.Completed;
        orchNode.Output = new StepOutput { Type = "orchestrator", Metadata = { ["parts"] = 2 }, Items = [new OutputItem { Content = "x" }] };
        graph.CompleteNodeAndPrepareDownstream(orchNode);

        Assert.Equal(NodeStatus.Ready, imageNode.Status);
        Assert.Equal(2, LoopFrames.PartsFor(imageNode.LoopFrame!));
    }

    [Fact]
    public void LasPartesSeLeenTambienDeUnaEjecucionGuardada()
    {
        // Al reanudar, la metadata del orquestador vuelve de la BD como JsonElement.
        var driver = new ModuleNode(new ProjectModule { Id = Guid.NewGuid(), AiModule = new AiModule { ModuleType = "Orchestrator" } })
        {
            Output = new StepOutput { Type = "orchestrator", Metadata = { ["parts"] = JsonSerializer.SerializeToElement(7) } },
        };
        var info = new LoopFrames.FrameInfo { Frame = driver, Driver = driver };

        Assert.Equal(7, LoopFrames.PartsFor(info));
        Assert.Equal(1, LoopFrames.PartsFor(new LoopFrames.FrameInfo { Frame = driver }));
    }

    // ── Lo que llega a cada vuelta ──

    [Fact]
    public void CadaVueltaRecibeSuParteYLoComunEntero()
    {
        var inputs = new Dictionary<string, List<PortData>>
        {
            ["input_prompt"] =
            [
                new PortData { TextContent = "Estilo comun.\n\n" + LoopPlanner.JoinParts(["uno", "dos", "tres"]) },
                new PortData { TextContent = "texto sin repartir" },
            ],
            ["input_image"] =
            [
                new PortData { Files = [File("a.png"), File("b.png"), File("c.png")] },
                new PortData { Files = [File("referencia.png")] },
            ],
        };

        var part = LoopFrames.SliceInputs(inputs, 1, 3);

        Assert.Equal("Estilo comun.\n\ndos", part["input_prompt"][0].TextContent);
        Assert.Equal("texto sin repartir", part["input_prompt"][1].TextContent);
        Assert.Equal("b.png", Assert.Single(part["input_image"][0].Files!).FileName);
        Assert.Equal("referencia.png", Assert.Single(part["input_image"][1].Files!).FileName);
        // Las entradas originales no se tocan: las usan las otras vueltas.
        Assert.Equal(3, inputs["input_image"][0].Files!.Count);
    }

    // ── Como se juntan las vueltas ──

    [Fact]
    public void LasVueltasSeJuntanEnOrdenConNombresQueNoChocan()
    {
        var results = Enumerable.Range(1, 3).Select(i =>
        {
            // Recien producido: todavia sin FileId, lo pone el executor al guardarlo.
            var output = new StepOutput
            {
                Type = "video",
                Content = $"clip {i}",
                Files = [new OutputFile { FileName = "clip_1.mp4", ContentType = "video/mp4" }],
            };
            return ModuleResult.Completed(output, 0.5m,
                [new ProducedFile { Data = [(byte)i], FileName = "clip_1.mp4", ContentType = "video/mp4" }]);
        }).ToList();

        var merged = LoopFrames.MergeResults(results);

        Assert.Equal(ModuleResultStatus.Completed, merged.Status);
        Assert.Equal(["p01_clip_1.mp4", "p02_clip_1.mp4", "p03_clip_1.mp4"], merged.ProducedFiles.Select(f => f.FileName));
        Assert.Equal(new byte[] { 2 }, merged.ProducedFiles[1].Data);
        Assert.Equal(["p01_clip_1.mp4", "p02_clip_1.mp4", "p03_clip_1.mp4"], merged.Output!.Files.Select(f => f.FileName));
        Assert.Equal(1.5m, merged.Cost);
        Assert.Equal(3, (int)merged.Output.Metadata["loopParts"]);
        // El texto viaja como tomas, para que el siguiente modulo lo vuelva a cortar.
        Assert.Equal(["clip 1", "clip 2", "clip 3"], MultiImagePrompt.Split([merged.Output.Content]).Segments);
    }

    [Fact]
    public void SiFallaUnaVuelta_FallaElModuloDiciendoCual()
    {
        var results = new List<ModuleResult>
        {
            ModuleResult.Completed(new StepOutput { Type = "video" }),
            ModuleResult.Failed("cuota agotada"),
        };

        var merged = LoopFrames.MergeResults(results);

        Assert.Equal(ModuleResultStatus.Failed, merged.Status);
        Assert.Equal("Parte 2/2: cuota agotada", merged.Error);
    }

    [Fact]
    public void UnModuloQueSePausa_NoPuedeIrDentro()
    {
        var merged = LoopFrames.MergeResults([ModuleResult.Paused("revisar")]);

        Assert.Equal(ModuleResultStatus.Failed, merged.Status);
        Assert.Contains("no puede ir dentro de un loop", merged.Error);
    }

    // ── El rectangulo ──

    [Fact]
    public async Task LaSalidaDelRectanguloEntregaLoQueProdujoElCuerpo()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 0, 0, """{"loopMode":true}""");
        var frame = p.Add(LoopFrames.ModuleType, 300, 0);
        var video = p.Add("Video", 320, 40);
        p.Connect(orch, "output_1", video, "input_prompt");
        var graph = p.Build();
        graph.Nodes[orch.Id].Output = new StepOutput { Type = "orchestrator", Metadata = { ["parts"] = 2 } };

        var frameNode = graph.Nodes[frame.Id];
        var ctx = Context(frameNode, new Dictionary<string, List<PortData>>
        {
            [LoopFrames.BodyPort] = [new PortData { Files = [File("p01_clip.mp4", "video/mp4"), File("p02_clip.mp4", "video/mp4")] }],
        });

        var result = await new LoopFrameModuleHandler().ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal(["p01_clip.mp4", "p02_clip.mp4"], result.Output!.Files.Select(f => f.FileName));
        Assert.Equal(2, (int)result.Output.Metadata["parts"]);
    }

    [Fact]
    public async Task UnRectanguloVacio_FallaExplicandoloSinBloquearElGrafo()
    {
        var p = new Pipeline();
        var frame = p.Add(LoopFrames.ModuleType, 300, 0);
        var frameNode = p.Build().Nodes[frame.Id];

        Assert.True(frameNode.CanStartWithoutInputs);

        var result = await new LoopFrameModuleHandler().ExecuteAsync(Context(frameNode, []));

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("no tiene modulos dentro", result.Error);
    }

    // ── Helpers ──

    private static OutputFile File(string name, string type = "image/png") =>
        new() { FileId = Guid.NewGuid(), FileName = name, ContentType = type };

    private static ModuleExecutionContext Context(ModuleNode node, Dictionary<string, List<PortData>> inputs) => new()
    {
        Node = node,
        Graph = new ExecutionGraph(),
        Execution = new ProjectExecution { Id = Guid.NewGuid() },
        Project = new Project { Id = Guid.NewGuid(), Name = "test" },
        TenantDbName = "tenant_test",
        WorkspacePath = Path.GetTempPath(),
        MediaRoot = Path.GetTempPath(),
        Config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
        InputsByPort = inputs,
    };

    /// <summary>Pipeline de prueba: modulos con posicion y conexiones, como en la BD.</summary>
    private sealed class Pipeline
    {
        private readonly List<ProjectModule> _modules = [];
        private readonly List<ModuleConnection> _connections = [];

        public ProjectModule Add(string type, double x, double y, string? config = null, string model = "m")
        {
            var pm = new ProjectModule
            {
                Id = Guid.NewGuid(),
                IsActive = true,
                PosX = x,
                PosY = y,
                Configuration = config,
                AiModule = new AiModule { Id = Guid.NewGuid(), Name = type, ModuleType = type, ProviderType = "OpenAI", ModelName = model },
            };
            _modules.Add(pm);
            return pm;
        }

        public void Connect(ProjectModule from, string fromPort, ProjectModule to, string toPort) =>
            _connections.Add(new ModuleConnection
            {
                Id = Guid.NewGuid(),
                FromModuleId = from.Id,
                FromPort = fromPort,
                ToModuleId = to.Id,
                ToPort = toPort,
            });

        public ExecutionGraph Build() => ExecutionGraph.Build(_modules, _connections);
    }
}
