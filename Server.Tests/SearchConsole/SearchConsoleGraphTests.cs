using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.SearchConsole;

/// <summary>
/// El nodo Search Console no tiene puertos de entrada: es una fuente de datos,
/// como Texto plano. Si el grafo no lo arranca por su cuenta se queda pendiente
/// para siempre y la ejecucion acaba en "Grafo bloqueado" con todo lo que cuelga
/// de el sin ejecutar.
/// </summary>
public class SearchConsoleGraphTests
{
    private static ProjectModule Node(string moduleType) => new()
    {
        Id = Guid.NewGuid(),
        AiModule = new AiModule { Id = Guid.NewGuid(), Name = moduleType, ModuleType = moduleType },
    };

    [Fact]
    public void SearchConsole_ArrancaSinEntradas_YDesbloqueaElSiguiente()
    {
        var start = Node("Start");
        var searchConsole = Node("SearchConsole");
        var text = Node("Text");
        var connections = new List<ModuleConnection>
        {
            new() { Id = Guid.NewGuid(), FromModuleId = start.Id, FromPort = "output_prompt", ToModuleId = text.Id, ToPort = "input_prompt" },
            new() { Id = Guid.NewGuid(), FromModuleId = searchConsole.Id, FromPort = "output_text", ToModuleId = text.Id, ToPort = "input_prompt" },
        };

        var graph = ExecutionGraph.Build([start, searchConsole, text], connections);
        graph.MarkInitialReadyNodes();

        var ready = graph.GetReadyNodes().Select(n => n.ModuleId).ToHashSet();
        Assert.Contains(start.Id, ready);
        Assert.Contains(searchConsole.Id, ready);
        Assert.DoesNotContain(text.Id, ready);
    }
}
