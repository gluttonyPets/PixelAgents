using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.OrquestadorLoop;

/// <summary>
/// Logica del orquestador loop que no necesita IA ni ffmpeg: cuantas partes
/// salen de una duracion, que tramo cubre cada una, que texto de la locucion le
/// toca y como se lee y valida el plan que devuelve la IA.
/// </summary>
public class LoopPlannerTests
{
    // ── Numero de partes ──

    [Theory]
    [InlineData(30, 5, 6)]
    [InlineData(39, 5, 8)]   // el caso real: 39 s de voz necesitan 8 clips, no 3
    [InlineData(30.002, 5, 6)] // el redondeo de ffprobe no anade un clip entero
    [InlineData(12.05, 3, 4)]  // ni el relleno del MP3
    [InlineData(30.4, 5, 7)]   // pero casi medio segundo de voz sin imagen si
    [InlineData(2, 5, 1)]
    [InlineData(11, 10, 2)]
    public void LasPartesCubrenTodaLaDuracion(double total, double clip, int expected) =>
        Assert.Equal(expected, LoopPlanner.CountParts(total, clip));

    [Fact]
    public void LasPartesTienenTope()
    {
        Assert.Equal(LoopPlanner.MaxParts, LoopPlanner.CountParts(600, 5));
    }

    // ── Tramos ──

    [Fact]
    public void ElUltimoTramoAcabaDondeAcabaLaEntrada()
    {
        var windows = LoopPlanner.BuildWindows(8, 5, 39, narration: null);

        Assert.Equal(8, windows.Count);
        Assert.Equal(0, windows[0].Start);
        Assert.Equal(5, windows[0].End);
        Assert.Equal(35, windows[7].Start);
        Assert.Equal(39, windows[7].End);
        Assert.All(windows, w => Assert.Null(w.Narration));
    }

    [Fact]
    public void LocucionSinMarcas_SeRepartePorProporcionYEnOrden()
    {
        var words = Enumerable.Range(1, 30).Select(i => $"p{i}").ToList();
        var windows = LoopPlanner.BuildWindows(3, 5, 15, string.Join(" ", words));

        // Las 30 palabras, en orden y sin repetir, 10 por tramo de 5 s.
        var rebuilt = windows.SelectMany(w => w.Narration!.Split(' ')).ToList();
        Assert.Equal(words, rebuilt);
        Assert.All(windows, w => Assert.Equal(10, w.Narration!.Split(' ').Length));
    }

    [Fact]
    public void LocucionConMarcas_CadaFraseCaeEnSuTramo()
    {
        var narration = """
            [00:00.0 - 00:04.0] Los pulpos tienen tres corazones.
            [00:05.5 - 00:09.0] Y su sangre es azul.
            [00:11.0 - 00:14.0] Por eso sobreviven en el fondo del mar.
            """;

        var windows = LoopPlanner.BuildWindows(3, 5, 15, narration);

        Assert.Equal("Los pulpos tienen tres corazones.", windows[0].Narration);
        Assert.Equal("Y su sangre es azul.", windows[1].Narration);
        Assert.Equal("Por eso sobreviven en el fondo del mar.", windows[2].Narration);
    }

    [Fact]
    public void FraseQueCruzaUnCorte_SeParteEntreLosDosTramos()
    {
        // 8 palabras de 3 s a 7 s: las que caen antes del segundo 5 van al tramo 1.
        var windows = LoopPlanner.BuildWindows(2, 5, 10, "[3 - 7] a b c d e f g h");

        Assert.Equal("a b c d", windows[0].Narration);
        Assert.Equal("e f g h", windows[1].Narration);
    }

    [Theory]
    [InlineData("[00:04.2 - 00:09.8] hola", 4.2, 9.8)]
    [InlineData("[4.2 - 9.8] hola", 4.2, 9.8)]
    [InlineData("[0:01:15,5 --> 0:01:20,0] hola", 75.5, 80)]
    public void SeLeenLosFormatosDeMarcaDeTiempo(string line, double start, double end)
    {
        var segment = Assert.Single(LoopPlanner.ParseTimestamps(line));
        Assert.Equal(start, segment.Start, 3);
        Assert.Equal(end, segment.End, 3);
        Assert.Equal("hola", segment.Text);
    }

    [Fact]
    public void TextoNormal_NoSeConfundeConMarcas()
    {
        Assert.Empty(LoopPlanner.ParseTimestamps("El guion [version 2] habla de pulpos."));
    }

    // ── Prompt y plan ──

    [Fact]
    public void ElPromptPideExactamenteLasPartesYNombraCadaCampo()
    {
        var outputs = Outputs();
        var windows = LoopPlanner.BuildWindows(3, 5, 13, "uno dos tres cuatro cinco seis");

        var prompt = LoopPlanner.BuildSystemPrompt(outputs, windows, null);

        Assert.Contains("EXACTLY 3 parts", prompt);
        Assert.Contains("\"output_1\" (Tematica)", prompt);
        Assert.Contains("\"output_2\" (Descripcion video)", prompt);
        Assert.Contains("Part 3: 00:10.0 - 00:13.0", prompt);
        Assert.Contains("narration heard in this part", prompt);
    }

    [Fact]
    public void ElPlanSeLeeAunqueVengaEnBloqueDeCodigoYDesordenado()
    {
        var raw = """
            ```json
            {"summary":"pulpos","parts":[
              {"index":2,"output_1":"b1","output_2":"b2"},
              {"index":1,"output_1":"a1","output_2":"a2"}
            ]}
            ```
            """;

        var plan = LoopPlanner.ParsePlan(raw);

        Assert.NotNull(plan);
        Assert.Equal("pulpos", plan!.Summary);
        Assert.Equal("a1", plan.Parts[0]["output_1"]);
        Assert.Equal("b2", plan.Parts[1]["output_2"]);
        Assert.Empty(LoopPlanner.ValidatePlan(plan, Outputs(), 2));
    }

    [Fact]
    public void PlanConPartesDeMenosOCamposVacios_EsInvalido()
    {
        var plan = LoopPlanner.ParsePlan("""{"parts":[{"output_1":"a","output_2":""}]}""")!;

        var errors = LoopPlanner.ValidatePlan(plan, Outputs(), 3);

        Assert.Contains(errors, e => e.Contains("se pidieron 3 partes y llegaron 1"));
        Assert.Contains(errors, e => e.Contains("output_2"));
    }

    [Fact]
    public void RespuestaQueNoEsJson_NoEsUnPlan()
    {
        Assert.Null(LoopPlanner.ParsePlan("Aqui tienes las partes: ..."));
    }

    [Fact]
    public void LasPartesUnidasSeVuelvenARepartirIgualAguasAbajo()
    {
        // El contrato con imagen/video/audio: lo que sale por una salida se tiene
        // que deshacer en las mismas N partes, en el mismo orden.
        var values = new List<string> { "pulpo en el arrecife", "corazon latiendo", "fondo marino" };

        var split = MultiImagePrompt.Split([LoopPlanner.JoinParts(values)]);

        Assert.Equal(values, split.Segments);
        Assert.Equal("", split.Common);
    }

    [Fact]
    public void UnaSolaParte_ViajaSinMarcas()
    {
        Assert.Equal("solo esto", LoopPlanner.JoinParts(["solo esto"]));
    }

    // ── Quien marca el ritmo ──

    [Fact]
    public void ElRitmoLoMarcaElModuloDeDuracionDeDentroDelLoop()
    {
        var orchestrator = Node("Orchestrator", "gpt-5.4");
        var frame = Frame(orchestrator,
        [
            Node("Image", "gpt-image-2"),
            Node("Video", "leonardo-motion-2", "Leonardo Video"),
        ]);

        var pacer = LoopPlanner.FindPacer(frame);

        Assert.NotNull(pacer);
        Assert.Equal(5, pacer!.ClipSeconds);
        Assert.Equal("Leonardo Video", pacer.NodeName);
    }

    [Fact]
    public void SinModuloDeDuracionFijaDentro_NoHayRitmo()
    {
        var orchestrator = Node("Orchestrator", "gpt-5.4");
        var frame = Frame(orchestrator, [Node("Image", "gpt-image-2"), Node("Audio", "gpt-4o-mini-tts")]);

        Assert.Null(LoopPlanner.FindPacer(frame));
    }

    [Theory]
    [InlineData("Video", true)]
    [InlineData("Audio", true)]
    [InlineData("VideoAssembly", true)]
    [InlineData("Image", false)]
    [InlineData("Text", false)]
    public void ModulosDeDuracion_SonLosDeAudioYVideo(string type, bool expected) =>
        Assert.Equal(expected, LoopPlanner.IsDurationModule(type));

    [Fact]
    public void LaDuracionDelClipVieneDelCatalogo()
    {
        Assert.Equal(5, ModelCatalog.GetClipSeconds("leonardo-motion-2"));
        Assert.Null(ModelCatalog.GetClipSeconds("gpt-image-2"));
    }

    // ── Helpers ──

    internal static List<OrchestratorOutput> Outputs() =>
    [
        new() { OutputKey = "output_1", Label = "Tematica", Prompt = "tema de la parte", SortOrder = 1 },
        new() { OutputKey = "output_2", Label = "Descripcion video", Prompt = "lo que se ve", SortOrder = 2 },
    ];

    internal static ModuleNode Node(string type, string model, string? name = null)
    {
        var ai = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = name ?? type,
            ModuleType = type,
            ProviderType = "OpenAI",
            ModelName = model,
        };
        return new ModuleNode(new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = ai });
    }

    /// <summary>Rectangulo de loop del orquestador con estos modulos dentro.</summary>
    internal static LoopFrames.FrameInfo Frame(ModuleNode orchestrator, List<ModuleNode> members)
    {
        var info = new LoopFrames.FrameInfo { Driver = orchestrator, Area = new LoopFrames.Rect(0, 0, 600, 300) };
        info.Members.AddRange(members);
        foreach (var m in members) m.LoopFrame = info;
        orchestrator.OwnedLoopFrame = info;
        return info;
    }

    internal static void Connect(ModuleNode from, string fromPort, ModuleNode to, string toPort)
    {
        var output = from.OutputPorts.FirstOrDefault(p => p.PortId == fromPort);
        if (output is null)
        {
            output = new OutputPort { PortId = fromPort };
            from.OutputPorts.Add(output);
        }

        var input = to.InputPorts.FirstOrDefault(p => p.PortId == toPort);
        if (input is null)
        {
            input = new InputPort { PortId = toPort, IsRequired = true };
            to.InputPorts.Add(input);
        }

        var conn = new PortConnection
        {
            ConnectionId = Guid.NewGuid(),
            SourceNode = from,
            SourcePortId = fromPort,
            TargetNode = to,
            TargetPortId = toPort,
        };
        output.Connections.Add(conn);
        input.Connections.Add(conn);
    }
}
