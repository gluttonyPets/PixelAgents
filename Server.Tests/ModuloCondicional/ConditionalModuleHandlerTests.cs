using System.Reflection;
using Moq;
using Server.Data;
using Server.Models;
using Server.Services;
using Server.Services.Ai;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.ModuloCondicional;

/// <summary>
/// Decision del handler condicional en modo expresion (no llega a llamar a
/// ninguna IA): que puerto queda bloqueado, que se propaga la entrada tal cual
/// y que una condicion vacia o ininteligible falla en vez de continuar a ciegas.
/// </summary>
public class ConditionalModuleHandlerTests
{
    [Fact]
    public async Task CondicionCumplida_BloqueaLaRamaFalsa()
    {
        var handler = CreateHandler();
        var ctx = CreateContext("contiene \"aprobado\"", "el pedido esta aprobado");

        var result = await handler.ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal([ConditionalBranching.FalsePort], result.BlockedOutputPorts);
        Assert.True(ConditionalBranching.ReadConditionMet(result.Output));
    }

    [Fact]
    public async Task CondicionNoCumplida_BloqueaLaRamaVerdadera()
    {
        var handler = CreateHandler();
        var ctx = CreateContext("contiene \"aprobado\"", "el pedido esta pendiente");

        var result = await handler.ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Completed, result.Status);
        Assert.Equal([ConditionalBranching.TruePort], result.BlockedOutputPorts);
        Assert.False(ConditionalBranching.ReadConditionMet(result.Output));
    }

    [Fact]
    public async Task LaEntradaSePropagaSinTocar()
    {
        var handler = CreateHandler();
        var ctx = CreateContext("no esta vacio", "texto original del modulo anterior");

        var result = await handler.ExecuteAsync(ctx);

        Assert.Equal("texto original del modulo anterior", result.Output!.Content);
    }

    [Fact]
    public async Task SinCondicionEscrita_ElModuloFalla()
    {
        var handler = CreateHandler();
        var ctx = CreateContext("   ", "lo que sea");

        var result = await handler.ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("condicion", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModoExpresion_ConLenguajeNatural_FallaEnVezDeAdivinar()
    {
        var handler = CreateHandler();
        var ctx = CreateContext("el texto suena optimista", "hola", mode: "expression");

        var result = await handler.ExecuteAsync(ctx);

        Assert.Equal(ModuleResultStatus.Failed, result.Status);
        Assert.Contains("expresion", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Lo que se le pide al modelo.
    [InlineData("{\"cumple\": true, \"motivo\": \"habla de gatos\"}", true)]
    [InlineData("{\"cumple\": false, \"motivo\": \"no menciona gatos\"}", false)]
    // Variantes que devuelven los modelos en la practica.
    [InlineData("```json\n{\"cumple\": true, \"motivo\": \"ok\"}\n```", true)]
    [InlineData("{\"cumple\": \"si\", \"motivo\": \"ok\"}", true)]
    [InlineData("Claro: {\"cumple\": false} — eso es todo.", false)]
    [InlineData("no", false)]
    [InlineData("SI", true)]
    public void VeredictoDeLaIa_SeLeeEnSusFormatosHabituales(string raw, bool expected)
    {
        Assert.True(TryParseVerdict(raw, out var met, out var reason));
        Assert.Equal(expected, met);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("no lo tengo claro, depende del contexto")]
    [InlineData("")]
    public void VeredictoIlegible_NoSeInterpreta(string raw)
    {
        Assert.False(TryParseVerdict(raw, out _, out _));
    }

    // ── Helpers ──

    private static bool TryParseVerdict(string raw, out bool met, out string reason)
    {
        var method = typeof(ConditionalModuleHandler)
            .GetMethod("TryParseVerdict", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { raw, false, "" };
        var ok = (bool)method!.Invoke(null, args)!;
        met = (bool)args[1]!;
        reason = (string)args[2]!;
        return ok;
    }

    private static ConditionalModuleHandler CreateHandler() =>
        new(new Mock<IAiProviderRegistry>().Object, new Mock<ITenantDbContextFactory>().Object);

    private static ModuleExecutionContext CreateContext(string condition, string input, string mode = "auto")
    {
        var aiModule = new AiModule
        {
            Id = Guid.NewGuid(),
            Name = "Condicional",
            ModuleType = ConditionalBranching.ModuleType,
            ProviderType = "System",
            ModelName = "conditional",
        };
        var projectModule = new ProjectModule { Id = Guid.NewGuid(), IsActive = true, AiModule = aiModule };

        return new ModuleExecutionContext
        {
            Node = new ModuleNode(projectModule),
            Graph = new ExecutionGraph(),
            Execution = new ProjectExecution { Id = Guid.NewGuid() },
            Project = new Project { Id = Guid.NewGuid(), Name = "test" },
            TenantDbName = "tenant_test",
            WorkspacePath = "/tmp",
            MediaRoot = "/tmp",
            Config = new Dictionary<string, object>
            {
                ["condition"] = condition,
                ["conditionMode"] = mode,
            },
            InputsByPort = new Dictionary<string, List<PortData>>
            {
                ["input"] = [new PortData { DataType = "text", TextContent = input }],
            },
        };
    }
}
