using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Server.Data;
using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.PlanificadorTematicas;

/// <summary>
/// El planificador busca tematicas: el usuario dice sobre que quiere las proximas
/// ejecuciones ("Halloween") y cuantas (5), y el modelo elige los 5 mejores temas
/// dentro de eso, uno por ejecucion, con su titulo y su descripcion. Para no gastar
/// ejecuciones en lo mismo, se le recuerdan los temas que ya estan en la cola o se
/// han usado hace poco.
/// </summary>
public class BuscarTematicasTests
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

    private static UserDbContext CrearDbEnMemoria() =>
        new(new DbContextOptionsBuilder<UserDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static PlannedPrompt Planificada(
        Guid projectId, string content, string status, string? topic = null, int order = 0, DateTime? usedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrderIndex = order,
            Content = content,
            Topic = topic,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UsedAt = usedAt,
        };

    // ── Lo que se le pide al modelo ──

    [Fact]
    public void BuildPlannerPrompt_PideLasMejoresTematicasDeLoQueDiceElUsuario()
    {
        var prompt = PromptPlannerService.BuildPlannerPrompt(
            5, "Halloween", "Cuenta de manualidades para familias", SinVariables,
            today: new DateOnly(2026, 10, 29));

        Assert.Contains("Halloween", prompt, StringComparison.Ordinal);
        Assert.Contains("las 5 mejores tematicas", prompt, StringComparison.Ordinal);
        Assert.Contains("dedicar una ejecucion a cada una", prompt, StringComparison.Ordinal);
        Assert.Contains("distintas entre si", prompt, StringComparison.Ordinal);
        Assert.Contains("Cuenta de manualidades para familias", prompt, StringComparison.Ordinal);
        // Con la fecha, "Halloween" se planifica a tiempo y no para el ano que viene.
        Assert.Contains("Fecha de hoy: 2026-10-29", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerPrompt_CadaEjecucionEsUnaSolaPiezaYNoPideMasTematicas()
    {
        // Bug real: con "creame tematicas para publicaciones de Halloween" y 5, cada una
        // de las 5 ejecuciones salia con "Genera 5 tematicas..." dentro de su prompt.
        var prompt = PromptPlannerService.BuildPlannerPrompt(
            5, "quiero que me crees tematicas para publicaciones de Halloween", null, SinVariables);

        Assert.Contains("produce UNA sola pieza", prompt, StringComparison.Ordinal);
        Assert.Contains("5 es el\n  numero de ejecuciones", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("NUNCA le pide al pipeline varias tematicas", prompt, StringComparison.Ordinal);
        Assert.Contains("- Mal: \"Genera 5 tematicas", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDraftPrompt_TambienEsUnaSolaPieza()
    {
        var prompt = PromptPlannerService.BuildDraftPrompt("tematicas de Halloween", null, SinVariables);

        Assert.Contains("UNA sola pieza", prompt, StringComparison.Ordinal);
        Assert.Contains("nunca le pide al pipeline varias tematicas", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerPrompt_PideElTituloDelTemaDeCadaEjecucion()
    {
        var prompt = PromptPlannerService.BuildPlannerPrompt(5, "Halloween", null, SinVariables);

        Assert.Contains($"\"{PlannerSchema.TopicKey}\"", prompt, StringComparison.Ordinal);
        Assert.Contains("exactamente 5 ejecuciones ordenadas", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerPrompt_ConTemasYaPlanificados_LeProhibeRepetirlos()
    {
        var prompt = PromptPlannerService.BuildPlannerPrompt(
            5, "Halloween", null, SinVariables,
            alreadyPlanned: ["Calabazas talladas", "Disfraces caseros de ultima hora"]);

        Assert.Contains("NO las repitas", prompt, StringComparison.Ordinal);
        Assert.Contains("- Calabazas talladas", prompt, StringComparison.Ordinal);
        Assert.Contains("- Disfraces caseros de ultima hora", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerPrompt_SinTemasPrevios_NoAnadeLaListaAEvitar()
    {
        var prompt = PromptPlannerService.BuildPlannerPrompt(5, "Halloween", null, SinVariables, alreadyPlanned: []);

        Assert.DoesNotContain("NO las repitas", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlannerPrompt_UnSoloTema_HablaEnSingular()
    {
        var prompt = PromptPlannerService.BuildPlannerPrompt(1, "Halloween", null, SinVariables);

        Assert.Contains("la mejor tematica", prompt, StringComparison.Ordinal);
        Assert.Contains("exactamente 1 ejecucion", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstruction_ConTema_PideObjetosAunqueNoHayaVariables()
    {
        var instruccion = PlannerSchema.BuildInstruction(3, SinVariables, withTopic: true);

        Assert.Contains("\"tema\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("\"prompt\"", instruccion, StringComparison.Ordinal);
        Assert.DoesNotContain("\"variables\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("exactamente 3 ejecuciones ordenadas", instruccion, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstruction_ConTemaYVariables_LaVariableDeTematicaLlevaElTema()
    {
        var instruccion = PlannerSchema.BuildInstruction(
            3, [Definicion("tematica", "tema del post")], withTopic: true);

        Assert.Contains("\"tema\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("\"variables\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("su valor es el \"tema\" de esa ejecucion", instruccion, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstruction_SinTema_NoCambiaElContratoDeSiempre()
    {
        // El asistente de una ejecucion a mano sigue con el formato de antes.
        Assert.DoesNotContain("\"tema\"", PlannerSchema.BuildInstruction(1, SinVariables), StringComparison.Ordinal);
        Assert.DoesNotContain("\"tema\"", PlannerSchema.BuildInstruction(1, [Definicion("keyword")]), StringComparison.Ordinal);
    }

    // ── Lo que se lee de la respuesta ──

    [Fact]
    public void ParsePrompts_LeeElTemaDeCadaEjecucion()
    {
        const string raw = """
            {"prompts": [
              {"tema": "Calabazas con plantillas de constelaciones", "prompt": "Tutorial paso a paso..."},
              {"tema": "  Galletas   de\n fantasma  ", "prompt": "Receta sin horno..."},
              {"prompt": "Sin titulo"}
            ]}
            """;

        var drafts = PlannerSchema.ParsePrompts(raw, 5, SinVariables);

        Assert.Equal(3, drafts.Count);
        Assert.Equal("Calabazas con plantillas de constelaciones", drafts[0].Topic);
        Assert.Equal("Tutorial paso a paso...", drafts[0].Content);
        // El titulo es una cabecera: una sola linea y sin espacios de sobra.
        Assert.Equal("Galletas de fantasma", drafts[1].Topic);
        Assert.Null(drafts[2].Topic);
    }

    [Fact]
    public void ParsePrompts_FormaDeCadenaSuelta_QuedaSinTema()
    {
        var drafts = PlannerSchema.ParsePrompts("""{"prompts": ["Primer tema"]}""", 5, SinVariables);

        Assert.Null(Assert.Single(drafts).Topic);
    }

    [Fact]
    public void NormalizeTopic_RecortaLosTitulosLargosYVaciaLosEnBlanco()
    {
        Assert.Null(PlannerSchema.NormalizeTopic("   "));
        Assert.Equal(PlannerSchema.MaxTopicLength, PlannerSchema.NormalizeTopic(new string('a', 500))!.Length);
    }

    [Fact]
    public void TopicLabel_SinTema_CitaLaPrimeraLineaDelPrompt()
    {
        Assert.Equal("Calabazas", PromptPlannerService.TopicLabel("Calabazas", "Lo que sea"));
        Assert.Equal("Receta de galletas", PromptPlannerService.TopicLabel(null, "Receta de galletas\nPaso 1..."));
        Assert.EndsWith("...", PromptPlannerService.TopicLabel(null, new string('x', 400)));
        Assert.Equal("", PromptPlannerService.TopicLabel(null, "  "));
    }

    // ── Temas que no se pueden repetir ──

    [Fact]
    public async Task LoadPlannedTopicsAsync_JuntaLaColaYLasUltimasUsadasDelProyecto()
    {
        await using var db = CrearDbEnMemoria();
        var projectId = Guid.NewGuid();
        db.PlannedPrompts.AddRange(
            Planificada(projectId, "Tutorial...", PlannedPromptStatus.Pending, topic: "Calabazas talladas", order: 0),
            Planificada(projectId, "Receta de galletas de fantasma\nPaso 1", PlannedPromptStatus.Pending, order: 1),
            Planificada(projectId, "...", PlannedPromptStatus.Used, topic: "Disfraces caseros", usedAt: DateTime.UtcNow),
            Planificada(projectId, "...", PlannedPromptStatus.Skipped, topic: "Saltada"),
            Planificada(Guid.NewGuid(), "...", PlannedPromptStatus.Pending, topic: "De otro proyecto"));
        await db.SaveChangesAsync();

        var temas = await PromptPlannerService.LoadPlannedTopicsAsync(db, projectId, includePending: true);

        Assert.Equal(["Calabazas talladas", "Receta de galletas de fantasma", "Disfraces caseros"], temas);
    }

    [Fact]
    public async Task LoadPlannedTopicsAsync_AlReemplazarLaCola_SoloEvitaLasUsadas()
    {
        await using var db = CrearDbEnMemoria();
        var projectId = Guid.NewGuid();
        db.PlannedPrompts.AddRange(
            Planificada(projectId, "...", PlannedPromptStatus.Pending, topic: "Se va a borrar"),
            Planificada(projectId, "...", PlannedPromptStatus.Used, topic: "Disfraces caseros", usedAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        var temas = await PromptPlannerService.LoadPlannedTopicsAsync(db, projectId, includePending: false);

        Assert.Equal(["Disfraces caseros"], temas);
    }

    // ── De punta a punta, con el modelo simulado ──

    [Fact]
    public async Task GenerateAsync_DevuelveUnaEjecucionPorTemaYAvisaDeLoYaPlanificado()
    {
        await using var db = CrearDbEnMemoria();
        var projectId = Guid.NewGuid();
        db.Projects.Add(new Project { Id = projectId, Name = "Manualidades", Context = "Blog de manualidades" });
        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(), Name = "OpenAI", ProviderType = "OpenAI", EncryptedKey = "sk-test",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PlannedPrompts.Add(Planificada(projectId, "...", PlannedPromptStatus.Pending, topic: "Calabazas talladas"));
        await db.SaveChangesAsync();

        string? enviado = null;
        var provider = new Mock<IAiProvider>();
        provider.Setup(p => p.ExecuteAsync(It.IsAny<AiExecutionContext>()))
            .Callback<AiExecutionContext>(ctx => enviado = ctx.Input)
            .ReturnsAsync(new AiResult
            {
                Success = true,
                TextOutput = """
                    {"prompts": [
                      {"tema": "Murcielagos de papel reciclado", "prompt": "Manualidad con rollos de carton..."},
                      {"tema": "Galletas de fantasma", "prompt": "Receta sin horno..."}
                    ]}
                    """,
            });
        var registry = new Mock<IAiProviderRegistry>();
        registry.Setup(r => r.GetProvider("OpenAI")).Returns(provider.Object);

        var service = new PromptPlannerService(registry.Object, NullLogger<PromptPlannerService>.Instance);
        var result = await service.GenerateAsync(db, projectId, "gpt-4o", 2, "Halloween");

        Assert.True(result.Success, result.Error);
        Assert.Equal(["Murcielagos de papel reciclado", "Galletas de fantasma"], result.Prompts.Select(p => p.Topic));
        Assert.NotNull(enviado);
        Assert.Contains("Halloween", enviado, StringComparison.Ordinal);
        Assert.Contains("- Calabazas talladas", enviado, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateAsync_SinTematica_NoLlamaAlModelo()
    {
        await using var db = CrearDbEnMemoria();
        var registry = new Mock<IAiProviderRegistry>(MockBehavior.Strict);
        var service = new PromptPlannerService(registry.Object, NullLogger<PromptPlannerService>.Instance);

        var result = await service.GenerateAsync(db, Guid.NewGuid(), "gpt-4o", 5, "   ");

        Assert.False(result.Success);
        Assert.Contains("tematica", result.Error!, StringComparison.Ordinal);
    }
}
