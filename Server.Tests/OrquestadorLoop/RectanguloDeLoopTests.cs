using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Rectangulo de loop: el area de fondo del orquestador loop que marca que
/// modulos se repiten. Se prueba que modulos quedan dentro, que el cuerpo espera
/// al orquestador, que la salida del loop solo sale cuando ha terminado el
/// cuerpo, como se corta lo que llega a cada vuelta y como se juntan las vueltas.
/// </summary>
public class RectanguloDeLoopTests
{
    private const string Frame = """{"loopMode":true,"loopFrame":{"x":330,"y":60,"w":540,"h":200}}""";

    // ── Quien esta dentro ──

    [Fact]
    public void ElCuerpoSonLosModulosDentroDelRectanguloDelOrquestador()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, Frame);
        var image = p.Add("Image", 370, 120);
        var video = p.Add("Video", 610, 120);
        var montage = p.Add("VideoAssembly", 920, 300);
        p.Connect(orch, "output_2", image, "input_prompt");
        p.Connect(image, "output_image", video, "input_image");
        p.Connect(orch, LoopFrames.OutputPort, montage, "input_videos");

        var graph = p.Build();
        var info = graph.Nodes[orch.Id].OwnedLoopFrame!;

        Assert.Equal(new HashSet<Guid> { image.Id, video.Id }, info.Members.Select(m => m.ModuleId).ToHashSet());
        Assert.Equal(orch.Id, info.Driver.ModuleId);
        Assert.Equal(video.Id, Assert.Single(info.Sinks).ModuleId);
        Assert.Same(info, graph.Nodes[video.Id].LoopFrame);
        Assert.Null(graph.Nodes[montage.Id].LoopFrame);
    }

    [Fact]
    public void SinModoLoop_ElRectanguloGuardadoNoCuenta()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, """{"loopMode":false,"loopFrame":{"x":330,"y":60,"w":540,"h":200}}""");
        var image = p.Add("Image", 370, 120);

        var graph = p.Build();

        Assert.Null(graph.Nodes[orch.Id].OwnedLoopFrame);
        Assert.Null(graph.Nodes[image.Id].LoopFrame);
    }

    [Fact]
    public void DentroSeDecidePorLaEsquinaSuperiorIzquierda()
    {
        var area = LoopFrames.ReadFrame("""{"loopFrame":{"x":100,"y":100,"w":200,"h":100}}""")!.Value;

        Assert.True(area.Contains(100, 100));
        Assert.True(area.Contains(300, 200));
        Assert.False(area.Contains(301, 150));
        Assert.False(area.Contains(90, 150));
        Assert.Null(LoopFrames.ReadFrame("""{"loopMode":true}"""));
        Assert.Null(LoopFrames.ReadFrame("""{"loopFrame":{"x":1,"y":1,"w":0,"h":10}}"""));
    }

    // ── Orden de ejecucion ──

    [Fact]
    public void ElCuerpoEsperaAlOrquestadorAunqueNoRecibaNadaDeEl()
    {
        // Imagen dentro del rectangulo alimentada desde fuera por un texto: sin
        // esperar al orquestador no sabria cuantas vueltas dar.
        var p = new Pipeline();
        var text = p.Add("Text", -200, 0);
        var orch = p.Add("Orchestrator", 0, 300, Frame);
        var image = p.Add("Image", 370, 120);
        p.Connect(text, "output_text", image, "input_prompt");

        var graph = p.Build();
        var imageNode = graph.Nodes[image.Id];
        Complete(graph, text, new StepOutput { Type = "text", Content = "===TOMA 1===\na\n===TOMA 2===\nb" });

        Assert.Equal(NodeStatus.Pending, imageNode.Status);

        Complete(graph, orch, new StepOutput { Type = "orchestrator", Metadata = { ["parts"] = 2 } });

        Assert.Equal(NodeStatus.Ready, imageNode.Status);
        Assert.Equal(2, LoopFrames.PartsFor(imageNode.LoopFrame!));
    }

    [Fact]
    public void LaSalidaDelLoopSaleCuandoAcabaElCuerpoYUnaSolaVez()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, Frame);
        var image = p.Add("Image", 370, 120);
        var video = p.Add("Video", 610, 120);
        var montage = p.Add("VideoAssembly", 920, 300);
        p.Connect(orch, "output_2", image, "input_prompt");
        p.Connect(image, "output_image", video, "input_image");
        p.Connect(orch, LoopFrames.OutputPort, montage, "input_videos");
        var graph = p.Build();
        var clipsPort = graph.Nodes[montage.Id].InputPorts.Single(ip => ip.PortId == "input_videos");

        // Al terminar el orquestador su salida del loop todavia no sale.
        Complete(graph, orch, new StepOutput
        {
            Type = "orchestrator",
            Metadata = { ["parts"] = 2 },
            Items = [new OutputItem { Content = "x" }, new OutputItem { Content = "===TOMA 1===\na\n===TOMA 2===\nb" }],
        });
        Assert.Empty(clipsPort.ReceivedData);

        Complete(graph, image, new StepOutput { Type = "image", Files = [File("p01_output.png"), File("p02_output.png")] });
        Assert.Empty(clipsPort.ReceivedData);

        // Termina el ultimo del cuerpo: el montaje recibe los clips en orden.
        Complete(graph, video, new StepOutput { Type = "video", Files = [File("p01_clip_1.mp4"), File("p02_clip_1.mp4")] });
        var received = Assert.Single(clipsPort.ReceivedData);
        Assert.Equal(["p01_clip_1.mp4", "p02_clip_1.mp4"], received.Files!.Select(f => f.FileName));
        Assert.Equal(NodeStatus.Ready, graph.Nodes[montage.Id].Status);

        // Volver a completar un miembro (al reanudar) no la entrega otra vez.
        graph.CompleteNodeAndPrepareDownstream(graph.Nodes[video.Id]);
        Assert.Single(clipsPort.ReceivedData);
    }

    [Fact]
    public void ReintentarUnModuloDelCuerpo_ReiniciaLoQueCuelgaDeLaSalidaDelLoop()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, Frame);
        var video = p.Add("Video", 610, 120);
        var montage = p.Add("VideoAssembly", 920, 300);
        p.Connect(orch, "output_1", video, "input_prompt");
        p.Connect(orch, LoopFrames.OutputPort, montage, "input_videos");

        var downstream = p.Build().GetDownstreamNodes(video.Id);

        Assert.Contains(montage.Id, downstream);
    }

    [Fact]
    public void LasPartesSeLeenTambienDeUnaEjecucionGuardada()
    {
        // Al reanudar, la metadata del orquestador vuelve de la BD como JsonElement.
        var driver = new ModuleNode(new ProjectModule { Id = Guid.NewGuid(), AiModule = new AiModule { ModuleType = "Orchestrator" } })
        {
            Output = new StepOutput { Type = "orchestrator", Metadata = { ["parts"] = JsonSerializer.SerializeToElement(7) } },
        };

        Assert.Equal(7, LoopFrames.PartsFor(new LoopFrames.FrameInfo { Driver = driver, Area = default }));
        driver.Output = null;
        Assert.Equal(1, LoopFrames.PartsFor(new LoopFrames.FrameInfo { Driver = driver, Area = default }));
    }

    [Fact]
    public async Task UnRectanguloVacio_HaceFallarAlOrquestador()
    {
        var p = new Pipeline();
        var orch = p.Add("Orchestrator", 60, 90, Frame);
        orch.OrchestratorOutputs = LoopPlannerTests.Outputs();
        var node = p.Build().Nodes[orch.Id];

        var result = await new OrchestratorModuleHandler(new AiProviderRegistry([])).ExecuteAsync(Context(node,
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { [LoopPlanner.ModeKey] = true }));

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("rectangulo de loop esta vacio", result.Error);
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

    // ── El modulo "Bucle" de antes ──

    [Fact]
    public async Task ElModuloBucleRetiradoSeBorraAlArrancar()
    {
        await using var db = new UserDbContext(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase($"bucle_{Guid.NewGuid():N}").Options);
        db.AiModules.Add(new AiModule { Id = Guid.NewGuid(), Name = "Bucle", ProviderType = "System", ModuleType = "LoopFrame", ModelName = "loop-frame" });
        db.AiModules.Add(new AiModule { Id = Guid.NewGuid(), Name = "Montaje de video", ProviderType = "System", ModuleType = "VideoAssembly", ModelName = "video-assembly" });
        await db.SaveChangesAsync();

        await SystemModuleCatalog.RemoveRetiredModulesAsync(db);

        Assert.DoesNotContain(db.AiModules, m => m.ModuleType == "LoopFrame");
        Assert.Contains(db.AiModules, m => m.ModuleType == "VideoAssembly");
    }

    // ── Helpers ──

    private static void Complete(ExecutionGraph graph, ProjectModule module, StepOutput output)
    {
        var node = graph.Nodes[module.Id];
        node.Status = NodeStatus.Completed;
        node.Output = output;
        graph.CompleteNodeAndPrepareDownstream(node);
    }

    private static OutputFile File(string name, string type = "image/png") =>
        new() { FileId = Guid.NewGuid(), FileName = name, ContentType = type };

    private static ModuleExecutionContext Context(ModuleNode node, Dictionary<string, object> config) => new()
    {
        Node = node,
        Graph = new ExecutionGraph(),
        Execution = new ProjectExecution { Id = Guid.NewGuid() },
        Project = new Project { Id = Guid.NewGuid(), Name = "test" },
        TenantDbName = "tenant_test",
        WorkspacePath = Path.GetTempPath(),
        MediaRoot = Path.GetTempPath(),
        Config = config,
        InputsByPort = [],
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
