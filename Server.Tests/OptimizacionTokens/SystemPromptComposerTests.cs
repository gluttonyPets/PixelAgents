using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.OptimizacionTokens;

/// <summary>
/// El orden del system prompt decide cuanto se paga: OpenAI y xAI cachean por
/// prefijo comun, asi que los bloques invariantes tienen que ir antes del primer
/// bloque que cambia entre modulos. Si el prompt del modulo vuelve a subir por
/// encima del contexto del proyecto, los modulos de una misma ejecucion dejan de
/// compartir prefijo y se paga todo a precio completo.
/// </summary>
public class SystemPromptComposerTests
{
    private static AiExecutionContext Contexto(string? systemPrompt = "PROMPT_MODULO") =>
        new()
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            MandatoryRules = "REGLAS_OBLIGATORIAS",
            ProjectContext = "CONTEXTO_PROYECTO",
            PreviousExecutionsSummary = "HISTORIAL_PREVIO",
            PastExecutionsLearning = "APRENDIZAJE_PASADO",
            Configuration = systemPrompt is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["systemPrompt"] = systemPrompt },
        };

    [Fact]
    public void Build_ColocaLosBloquesInvariantesAntesDelPromptDelModulo()
    {
        var prompt = SystemPromptComposer.Build(Contexto());

        var reglas = prompt.IndexOf("REGLAS_OBLIGATORIAS", StringComparison.Ordinal);
        var contexto = prompt.IndexOf("CONTEXTO_PROYECTO", StringComparison.Ordinal);
        var historial = prompt.IndexOf("HISTORIAL_PREVIO", StringComparison.Ordinal);
        var modulo = prompt.IndexOf("PROMPT_MODULO", StringComparison.Ordinal);
        var aprendizaje = prompt.IndexOf("APRENDIZAJE_PASADO", StringComparison.Ordinal);

        Assert.True(reglas >= 0 && contexto >= 0 && historial >= 0 && modulo >= 0 && aprendizaje >= 0);

        // Invariantes primero, en orden de estabilidad decreciente.
        Assert.True(reglas < contexto, "Las reglas obligatorias deben preceder al contexto del proyecto");
        Assert.True(contexto < historial, "El contexto del proyecto debe preceder al historial");

        // Y todo lo invariante antes de lo que cambia por modulo.
        Assert.True(historial < modulo, "El prompt del modulo debe ir despues de los bloques invariantes");
        Assert.True(modulo < aprendizaje, "El aprendizaje filtrado por modulo va al final");
    }

    [Fact]
    public void Build_EmpiezaSiempreConLasReglasDeFormato()
    {
        // Es el unico bloque constante en toda la instalacion: si no va primero,
        // no hay prefijo cacheable comun entre proyectos distintos.
        var prompt = SystemPromptComposer.Build(Contexto());
        Assert.StartsWith(OutputSchemaHelper.GetTextContentRules(), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ConservaElPromptDelModuloYSuEtiquetaDePrioridad()
    {
        var prompt = SystemPromptComposer.Build(Contexto());

        Assert.Contains("INSTRUCCION PRINCIPAL", prompt, StringComparison.Ordinal);
        Assert.Contains("PROMPT_MODULO", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OmiteLosBloquesVacios()
    {
        var ctx = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            Configuration = new Dictionary<string, object>(),
        };

        var prompt = SystemPromptComposer.Build(ctx);

        Assert.Equal(OutputSchemaHelper.GetTextContentRules(), prompt);
        Assert.DoesNotContain("[Contexto del proyecto]", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("INSTRUCCION PRINCIPAL", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_PrefijoEstableEntreModulosDeLaMismaEjecucion()
    {
        // Dos modulos distintos de la misma ejecucion: todo lo anterior a su
        // prompt propio tiene que ser byte a byte identico, que es la condicion
        // para que el proveedor reutilice el prefijo cacheado.
        var a = SystemPromptComposer.Build(Contexto("MODULO_A"));
        var b = SystemPromptComposer.Build(Contexto("MODULO_B"));

        var prefijoA = a[..a.IndexOf("MODULO_A", StringComparison.Ordinal)];
        var prefijoB = b[..b.IndexOf("MODULO_B", StringComparison.Ordinal)];

        Assert.Equal(prefijoA, prefijoB);
        Assert.Contains("CONTEXTO_PROYECTO", prefijoA, StringComparison.Ordinal);
    }
}
