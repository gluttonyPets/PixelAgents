using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ModuloCondicional;

/// <summary>
/// Comportamiento del grafo cuando un modulo Condicional descarta una rama:
/// la rama viva sigue ejecutandose, la descartada se marca como Skipped (no
/// como fallida ni pendiente, que dejaria la ejecucion en "grafo bloqueado") y
/// un nodo alimentado ademas por otra rama viva no se salta.
/// </summary>
public class RamasCondicionalesTests
{
    [Fact]
    public void CondicionCumplida_SoloSigueLaRamaVerdadera()
    {
        var (graph, conditional, siTrue, siFalse) = BuildGraph();

        CompleteConditional(graph, conditional, conditionMet: true);

        Assert.Equal(NodeStatus.Ready, siTrue.Status);
        Assert.Equal(NodeStatus.Pending, siFalse.Status);

        var skipped = graph.SkipUnreachableNodes();

        Assert.Equal(new[] { siFalse }, skipped);
        Assert.Equal(NodeStatus.Skipped, siFalse.Status);
        Assert.Equal(NodeStatus.Ready, siTrue.Status);
    }

    [Fact]
    public void CondicionNoCumplida_SoloSigueLaRamaFalsa()
    {
        var (graph, conditional, siTrue, siFalse) = BuildGraph();

        CompleteConditional(graph, conditional, conditionMet: false);
        var skipped = graph.SkipUnreachableNodes();

        Assert.Equal(new[] { siTrue }, skipped);
        Assert.Equal(NodeStatus.Ready, siFalse.Status);
    }

    [Fact]
    public void RamaDescartada_ArrastraATodosSusModulosPosteriores()
    {
        var (graph, conditional, _, siFalse) = BuildGraph();
        var nieto = AddNode(graph, "Text");
        Connect(graph, siFalse, "output_text", nieto, "input_prompt");

        CompleteConditional(graph, conditional, conditionMet: true);
        var skipped = graph.SkipUnreachableNodes();

        Assert.Equal(new[] { siFalse, nieto }, skipped);
        Assert.Equal(NodeStatus.Skipped, nieto.Status);
    }

    [Fact]
    public void ModuloAlimentadoTambienPorUnaRamaViva_NoSeSalta()
    {
        var (graph, conditional, siTrue, siFalse) = BuildGraph();
        var union = AddNode(graph, "Text");
        Connect(graph, siTrue, "output_text", union, "input_prompt");
        Connect(graph, siFalse, "output_text", union, "input_prompt");

        CompleteConditional(graph, conditional, conditionMet: true);
        var skipped = graph.SkipUnreachableNodes();

        Assert.Equal(new[] { siFalse }, skipped);
        Assert.Equal(NodeStatus.Pending, union.Status);

        // Al completar la rama viva, la union se ejecuta con lo que le llega por
        // ella: la conexion muerta ya no la mantiene esperando.
        siTrue.Output = new StepOutput { Type = "text", Content = "hola" };
        siTrue.Status = NodeStatus.Completed;
        graph.CompleteNodeAndPrepareDownstream(siTrue);

        Assert.Equal(NodeStatus.Ready, union.Status);
    }

    [Fact]
    public void SinPuertosBloqueados_LaRamaVivaSeDeduceDelMetadatoPersistido()
    {
        // Reanudar o reintentar una ejecucion reconstruye el grafo desde la BD:
        // el veredicto solo sobrevive en los metadatos de la salida del nodo.
        var (graph, conditional, siTrue, siFalse) = BuildGraph();

        conditional.Output = new StepOutput
        {
            Type = "conditional",
            Content = "texto",
            Metadata = { [ConditionalBranching.MetadataKey] = false },
        };
        conditional.Status = NodeStatus.Completed;
        graph.CompleteNodeAndPrepareDownstream(conditional);

        Assert.Equal(new[] { siTrue }, graph.SkipUnreachableNodes());
        Assert.Equal(NodeStatus.Ready, siFalse.Status);
    }

    [Fact]
    public void GrafoConRamaDescartada_QuedaCompletoYNoBloqueado()
    {
        var (graph, conditional, siTrue, siFalse) = BuildGraph();

        CompleteConditional(graph, conditional, conditionMet: true);
        graph.SkipUnreachableNodes();

        siTrue.Output = new StepOutput { Type = "text", Content = "ok" };
        siTrue.Status = NodeStatus.Completed;
        graph.CompleteNodeAndPrepareDownstream(siTrue);

        Assert.True(graph.IsComplete);
        Assert.False(graph.IsBlocked);
        Assert.Equal(NodeStatus.Skipped, siFalse.Status);
    }

    // ── Helpers de construccion del grafo ──

    /// <summary>Condicional con una rama por cada salida, ya listo para ejecutar.</summary>
    private static (ExecutionGraph Graph, ModuleNode Conditional, ModuleNode SiTrue, ModuleNode SiFalse) BuildGraph()
    {
        var graph = new ExecutionGraph();
        var conditional = AddNode(graph, ConditionalBranching.ModuleType);
        var siTrue = AddNode(graph, "Text");
        var siFalse = AddNode(graph, "Text");

        Connect(graph, conditional, ConditionalBranching.TruePort, siTrue, "input_prompt");
        Connect(graph, conditional, ConditionalBranching.FalsePort, siFalse, "input_prompt");

        return (graph, conditional, siTrue, siFalse);
    }

    private static void CompleteConditional(ExecutionGraph graph, ModuleNode conditional, bool conditionMet)
    {
        conditional.Output = new StepOutput
        {
            Type = "conditional",
            Content = "texto de entrada",
            Metadata = { [ConditionalBranching.MetadataKey] = conditionMet },
        };
        conditional.Status = NodeStatus.Completed;
        foreach (var portId in ConditionalBranching.BlockedPortsFor(conditionMet))
            conditional.BlockedOutputPorts.Add(portId);

        graph.CompleteNodeAndPrepareDownstream(conditional);
    }

    private static ModuleNode AddNode(ExecutionGraph graph, string moduleType)
    {
        var pm = new ProjectModule
        {
            Id = Guid.NewGuid(),
            IsActive = true,
            AiModule = new AiModule
            {
                Id = Guid.NewGuid(),
                Name = moduleType,
                ModuleType = moduleType,
                ProviderType = "System",
                ModelName = moduleType.ToLowerInvariant(),
            },
        };

        var node = new ModuleNode(pm);
        graph.Nodes[pm.Id] = node;
        return node;
    }

    private static void Connect(
        ExecutionGraph graph, ModuleNode from, string fromPort, ModuleNode to, string toPort)
    {
        var outputPort = from.OutputPorts.FirstOrDefault(p => p.PortId == fromPort);
        if (outputPort is null)
        {
            outputPort = new OutputPort { PortId = fromPort, DataType = "any" };
            from.OutputPorts.Add(outputPort);
        }

        var inputPort = to.InputPorts.FirstOrDefault(p => p.PortId == toPort);
        if (inputPort is null)
        {
            inputPort = new InputPort { PortId = toPort, DataType = "any", IsRequired = true };
            to.InputPorts.Add(inputPort);
        }

        var connection = new PortConnection
        {
            ConnectionId = Guid.NewGuid(),
            SourceNode = from,
            SourcePortId = fromPort,
            TargetNode = to,
            TargetPortId = toPort,
        };

        outputPort.Connections.Add(connection);
        inputPort.Connections.Add(connection);
    }
}
