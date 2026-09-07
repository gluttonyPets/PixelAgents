using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.PlanificadorManual;

/// <summary>
/// Una ejecucion planificada tambien se puede dar de alta a mano, sin generar la cola
/// entera: el usuario escribe el prompt (y el valor de las variables) y, si quiere, le
/// pide a la IA que se lo redacte. El borrador no se guarda hasta que lo anade, asi que
/// lo que se comprueba aqui es el prompt que se le manda al modelo y las validaciones
/// previas a gastar una llamada.
/// </summary>
public class EjecucionManualConIaTests
{
    private static ProjectVariable Definicion(string key, string? descripcion = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Key = key,
            Description = descripcion,
        };

    private static readonly ProjectVariable[] SinVariables = [];

    private static PromptPlannerService CrearServicio() =>
        new(new Mock<IAiProviderRegistry>().Object, NullLogger<PromptPlannerService>.Instance);

    private static UserDbContext CrearDbEnMemoria() =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    // ── El contrato: una sola ejecucion, no una lista ──

    [Fact]
    public void BuildInstruction_ParaUnaSolaEjecucion_PideExactamenteUna()
    {
        var sinVariables = PlannerSchema.BuildInstruction(1, SinVariables);
        var conVariables = PlannerSchema.BuildInstruction(1, [Definicion("keyword")]);

        Assert.Contains("exactamente 1 prompt", sinVariables, StringComparison.Ordinal);
        Assert.DoesNotContain("prompts ordenados", sinVariables, StringComparison.Ordinal);

        Assert.Contains("exactamente 1 ejecucion", conVariables, StringComparison.Ordinal);
        Assert.DoesNotContain("ejecuciones ordenadas", conVariables, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstruction_ParaVariasEjecuciones_SigueEnPlural()
    {
        Assert.Contains("exactamente 4 prompts ordenados",
            PlannerSchema.BuildInstruction(4, SinVariables), StringComparison.Ordinal);
        Assert.Contains("exactamente 4 ejecuciones ordenadas",
            PlannerSchema.BuildInstruction(4, [Definicion("keyword")]), StringComparison.Ordinal);
    }

    // ── Lo que se le manda al modelo cuando el usuario pide ayuda ──

    [Fact]
    public void BuildDraftPrompt_LlevaLaIdeaElContextoYElContratoDeUnaEjecucion()
    {
        var prompt = PromptPlannerService.BuildDraftPrompt(
            idea: "una comparativa de zapatillas de trail",
            projectContext: "Blog de material de montana",
            variables: SinVariables);

        Assert.Contains("una comparativa de zapatillas de trail", prompt, StringComparison.Ordinal);
        Assert.Contains("Blog de material de montana", prompt, StringComparison.Ordinal);
        Assert.Contains("UNA ejecucion planificada", prompt, StringComparison.Ordinal);
        Assert.Contains("exactamente 1 prompt", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftPrompt_ConBorradorAMano_LoMandaParaPulirloEnVezDeTirarlo()
    {
        var prompt = PromptPlannerService.BuildDraftPrompt(
            idea: "hazlo mas concreto",
            projectContext: null,
            variables: SinVariables,
            currentContent: "Escribe algo sobre zapatillas");

        Assert.Contains("Escribe algo sobre zapatillas", prompt, StringComparison.Ordinal);
        Assert.Contains("mejoralo, no lo tires", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftPrompt_SinIdea_SeApoyaEnElBorradorDelUsuario()
    {
        var prompt = PromptPlannerService.BuildDraftPrompt(
            idea: "   ",
            projectContext: null,
            variables: SinVariables,
            currentContent: "Analiza el mercado de zapatillas de trail");

        Assert.Contains("Analiza el mercado de zapatillas de trail", prompt, StringComparison.Ordinal);
        Assert.Contains("quedate con su borrador", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftPrompt_ConVariablesFijadasAMano_LasDeclaraIntocables()
    {
        ProjectVariable[] variables = [Definicion("keyword", "palabra clave del analisis"), Definicion("tematica")];

        var prompt = PromptPlannerService.BuildDraftPrompt(
            idea: "un analisis de producto",
            projectContext: null,
            variables: variables,
            currentVariables: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["keyword"] = "zapatillas trail",
                // Vacia: la IA la rellena, asi que no se le dice que no la toque.
                ["tematica"] = "   ",
                // No declarada por el proyecto: no tiene por que llegar al modelo.
                ["inventada"] = "algo",
            });

        Assert.Contains("NO debes cambiar", prompt, StringComparison.Ordinal);
        Assert.Contains("- keyword: zapatillas trail", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("- tematica:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("inventada", prompt, StringComparison.Ordinal);

        // Y el contrato sigue pidiendo el valor de las variables declaradas.
        Assert.Contains("\"variables\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftPrompt_SinValoresFijados_NoAnadeElBloqueDeIntocables()
    {
        var prompt = PromptPlannerService.BuildDraftPrompt(
            idea: "un analisis de producto",
            projectContext: null,
            variables: [Definicion("keyword")]);

        Assert.DoesNotContain("NO debes cambiar", prompt, StringComparison.Ordinal);
    }

    // ── Validaciones antes de gastar una llamada al modelo ──

    [Fact]
    public async Task DraftAsync_SinIdeaNiBorrador_NoLlamaAlModelo()
    {
        await using var db = CrearDbEnMemoria();

        var result = await CrearServicio().DraftAsync(db, Guid.NewGuid(), "gpt-4o", idea: "  ");

        Assert.False(result.Success);
        Assert.Contains("Escribe la idea", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DraftAsync_ModeloNoSoportado_DevuelveElError()
    {
        await using var db = CrearDbEnMemoria();

        var result = await CrearServicio().DraftAsync(
            db, Guid.NewGuid(), "modelo-que-no-existe", idea: "una comparativa");

        Assert.False(result.Success);
        Assert.Contains("no soportado", result.Error!, StringComparison.Ordinal);
    }

    // ── Lo que se lee de la respuesta ──

    [Fact]
    public void ParsePrompts_RespuestaDeUnaEjecucion_DevuelvePromptYVariables()
    {
        ProjectVariable[] variables = [Definicion("keyword")];
        var raw = """
        {
          "prompts": [
            { "prompt": "Compara las 5 mejores zapatillas de trail", "variables": { "keyword": "zapatillas trail" } }
          ]
        }
        """;

        var drafts = PlannerSchema.ParsePrompts(raw, 1, variables);

        var draft = Assert.Single(drafts);
        Assert.Equal("Compara las 5 mejores zapatillas de trail", draft.Content);
        Assert.Equal("zapatillas trail", draft.Variables["keyword"]);
    }
}
