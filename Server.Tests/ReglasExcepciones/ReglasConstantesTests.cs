using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ReglasExcepciones;

/// <summary>
/// Las reglas constantes se trocearon por clave para poder excluir una sola en un
/// modulo. Lo que no puede cambiar es lo que recibe un modulo sin excepciones:
/// ahi el texto tiene que ser el de siempre, entero y en el mismo orden.
/// </summary>
public class ReglasConstantesTests
{
    [Fact]
    public void Compose_SinExcepciones_MandaLasTresEnOrden()
    {
        var texto = BuiltInRules.Compose();

        var general = texto.IndexOf("Reglas generales:", StringComparison.Ordinal);
        var formato = texto.IndexOf("Reglas de formato (OBLIGATORIAS):", StringComparison.Ordinal);
        var contenido = texto.IndexOf("Reglas de contenido (OBLIGATORIAS):", StringComparison.Ordinal);

        Assert.True(general >= 0 && general < formato && formato < contenido);
    }

    [Fact]
    public void Compose_SinExcepciones_EsLoMismoQueGetTextContentRules()
    {
        // El resto del servidor sigue entrando por OutputSchemaHelper.
        Assert.Equal(BuiltInRules.Compose(), OutputSchemaHelper.GetTextContentRules());
    }

    [Fact]
    public void Compose_ConUnaExcepcion_QuitaSoloEsBloque()
    {
        var texto = BuiltInRules.Compose(new HashSet<string> { BuiltInRules.FormatKey });

        Assert.DoesNotContain("Reglas de formato (OBLIGATORIAS):", texto);
        Assert.DoesNotContain("NO uses emojis", texto);
        Assert.Contains("Reglas generales:", texto);
        Assert.Contains("Reglas de contenido (OBLIGATORIAS):", texto);
    }

    [Fact]
    public void Compose_ConTodasExcepcionadas_NoMandaNada()
    {
        var todas = BuiltInRules.All.Select(r => r.Key).ToHashSet();

        Assert.Equal("", BuiltInRules.Compose(todas));
    }

    [Fact]
    public void Compose_ConClaveDesconocida_NoQuitaNada()
    {
        var texto = BuiltInRules.Compose(new HashSet<string> { "tenant:" + Guid.NewGuid(), "no-existe" });

        Assert.Equal(BuiltInRules.Compose(), texto);
    }

    [Fact]
    public void SystemPrompt_NoLlevaLaConstanteExcepcionada()
    {
        var ctx = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            SuppressedRuleKeys = new HashSet<string> { BuiltInRules.BrandsKey },
        };

        var prompt = SystemPromptComposer.Build(ctx);

        Assert.DoesNotContain("NUNCA menciones marcas", prompt);
        Assert.Contains("NUNCA hagas preguntas al usuario", prompt);
    }

    [Fact]
    public void SystemPrompt_SinExcepciones_LasLlevaTodas()
    {
        var ctx = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
        };

        var prompt = SystemPromptComposer.Build(ctx);

        Assert.Contains("NUNCA menciones marcas", prompt);
        Assert.Contains("NO uses emojis", prompt);
        Assert.Contains("NUNCA hagas preguntas al usuario", prompt);
    }

    [Fact]
    public void TenantKey_YVuelta_ConservanElId()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, BuiltInRules.TenantRuleId(BuiltInRules.TenantKey(id)));
        Assert.Null(BuiltInRules.TenantRuleId(BuiltInRules.FormatKey));
        Assert.Null(BuiltInRules.TenantRuleId("tenant:no-es-un-guid"));
        Assert.True(BuiltInRules.IsBuiltInKey(BuiltInRules.FormatKey));
        Assert.False(BuiltInRules.IsBuiltInKey("tenant:" + id));
    }
}
