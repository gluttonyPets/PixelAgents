using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.VariablesEjecucion;

/// <summary>
/// El planificador no solo escribe el prompt de cada ejecucion futura: tambien le
/// pone valor a las variables del pipeline. Si no lo hiciera, todas las corridas
/// programadas usarian el mismo valor por defecto y la planificacion no serviria.
/// </summary>
public class PlanificadorVariablesTests
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

    // ── Lo que se le pide al modelo ──

    [Fact]
    public void BuildInstruction_SinVariables_MantieneElFormatoSimpleDeSoloPrompts()
    {
        var instruccion = PlannerSchema.BuildInstruction(3, SinVariables);

        Assert.Contains("\"prompts\"", instruccion, StringComparison.Ordinal);
        Assert.DoesNotContain("\"variables\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("exactamente 3 prompts", instruccion, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInstruction_ConVariables_PideUnValorPorEjecucionYExplicaCadaUna()
    {
        var variables = new[]
        {
            Definicion("keyword", "es la palabra clave sobre la que centramos el analisis"),
            Definicion("tematica"),
        };

        var instruccion = PlannerSchema.BuildInstruction(5, variables);

        // Lo unico que el pipeline declara: el nombre y para que sirve.
        Assert.Contains("\"keyword\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("es la palabra clave sobre la que centramos el analisis", instruccion, StringComparison.Ordinal);
        Assert.Contains("\"tematica\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("sin descripcion", instruccion, StringComparison.Ordinal);

        // Y el contrato de salida ya no es una lista de cadenas.
        Assert.Contains("\"variables\"", instruccion, StringComparison.Ordinal);
        Assert.Contains("exactamente 5 ejecuciones", instruccion, StringComparison.Ordinal);
    }

    // ── Lo que se lee de su respuesta ──

    [Fact]
    public void ParsePrompts_LeeElPromptYSusVariables()
    {
        var variables = new[] { Definicion("keyword"), Definicion("tematica") };
        const string raw = """
            {"prompts": [
              {"prompt": "Analiza el mercado", "variables": {"keyword": "espresso", "tematica": "cafe"}},
              {"prompt": "Compara tostadores", "variables": {"keyword": "tueste", "tematica": "cafe"}}
            ]}
            """;

        var drafts = PlannerSchema.ParsePrompts(raw, 5, variables);

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Analiza el mercado", drafts[0].Content);
        Assert.Equal("espresso", drafts[0].Variables["keyword"]);
        Assert.Equal("cafe", drafts[0].Variables["tematica"]);
        Assert.Equal("tueste", drafts[1].Variables["keyword"]);
    }

    [Fact]
    public void ParsePrompts_AceptaLaFormaAntiguaDeCadenaSuelta()
    {
        // Un modelo puede ignorar el esquema y devolver solo texto: mejor guardar el
        // prompt sin variables que descartar la planificacion entera.
        const string raw = """{"prompts": ["Primer tema", "Segundo tema"]}""";

        var drafts = PlannerSchema.ParsePrompts(raw, 5, [Definicion("keyword")]);

        Assert.Equal(2, drafts.Count);
        Assert.Equal("Primer tema", drafts[0].Content);
        Assert.Empty(drafts[0].Variables);
    }

    [Fact]
    public void ParsePrompts_DescartaVariablesQueElProyectoNoDeclara()
    {
        const string raw = """
            {"prompts": [{"prompt": "Tema", "variables": {"keyword": "espresso", "inventada": "x"}}]}
            """;

        var drafts = PlannerSchema.ParsePrompts(raw, 5, [Definicion("keyword")]);

        Assert.Equal("espresso", drafts[0].Variables["keyword"]);
        Assert.False(drafts[0].Variables.ContainsKey("inventada"));
    }

    [Fact]
    public void ParsePrompts_GuardaLaClaveComoLaDeclaraElProyecto()
    {
        // El modelo escribe "Keyword"; la UI y la sustitucion buscan "keyword".
        const string raw = """{"prompts": [{"prompt": "Tema", "variables": {"Keyword": "espresso"}}]}""";

        var drafts = PlannerSchema.ParsePrompts(raw, 5, [Definicion("keyword")]);

        Assert.Equal("espresso", drafts[0].Variables["keyword"]);
    }

    [Fact]
    public void ParsePrompts_IgnoraValoresVaciosYPromptsSinTexto()
    {
        const string raw = """
            {"prompts": [
              {"prompt": "Tema", "variables": {"keyword": "   "}},
              {"prompt": "   ", "variables": {"keyword": "espresso"}}
            ]}
            """;

        var drafts = PlannerSchema.ParsePrompts(raw, 5, [Definicion("keyword")]);

        Assert.Single(drafts);
        Assert.Empty(drafts[0].Variables);
    }

    [Fact]
    public void ParsePrompts_RecortaAlNumeroPedidoYToleraVallasMarkdown()
    {
        const string raw = """
            ```json
            {"prompts": ["uno", "dos", "tres"]}
            ```
            """;

        var drafts = PlannerSchema.ParsePrompts(raw, 2, SinVariables);

        Assert.Equal(2, drafts.Count);
        Assert.Equal("uno", drafts[0].Content);
    }

    [Fact]
    public void ParsePrompts_ConRespuestaInvalida_NoDevuelveNada()
    {
        Assert.Empty(PlannerSchema.ParsePrompts("lo siento, no puedo", 3, SinVariables));
        Assert.Empty(PlannerSchema.ParsePrompts("""{"otra": []}""", 3, SinVariables));
        Assert.Empty(PlannerSchema.ParsePrompts("", 3, SinVariables));
    }

    // ── Prioridad de valores al ejecutar ──

    [Fact]
    public void Merge_LoQueTraeElPromptPlanificadoGanaALoQueFijaLaProgramacion()
    {
        var programacion = ExecutionVariables.Parse("""{"keyword":"generico","tematica":"cafe"}""");
        var promptPlanificado = ExecutionVariables.Parse("""{"keyword":"espresso"}""");

        var efectivas = ExecutionVariables.Merge(programacion, promptPlanificado);

        Assert.Equal("espresso", efectivas["keyword"]);
        Assert.Equal("cafe", efectivas["tematica"]);   // lo que el prompt no toca se conserva
    }

    [Fact]
    public void Merge_IgnoraCapasVaciasYValoresEnBlanco()
    {
        var basePrograma = ExecutionVariables.Parse("""{"keyword":"generico"}""");
        var vacia = ExecutionVariables.Parse("""{"keyword":"   "}""");

        var efectivas = ExecutionVariables.Merge(null, basePrograma, vacia);

        Assert.Equal("generico", efectivas["keyword"]);
    }
}
