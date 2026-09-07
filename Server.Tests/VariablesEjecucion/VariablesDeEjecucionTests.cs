using System.Text.Json;
using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.VariablesEjecucion;

/// <summary>
/// Variables del pipeline: el proyecto declara las claves ("tematica", "keyword")
/// y cada ejecucion fija su valor. El valor se sustituye en los prompts y viaja
/// ademas como bloque etiquetado en el system prompt de todos los modulos.
/// </summary>
public class VariablesDeEjecucionTests
{
    private static ProjectVariable Definicion(string key, string? descripcion = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Key = key,
            Description = descripcion,
        };

    private static Dictionary<string, string> Valores(params (string Key, string Value)[] pares)
    {
        var map = ExecutionVariables.NewMap();
        foreach (var (k, v) in pares) map[k] = v;
        return map;
    }

    // ── Claves ──

    [Theory]
    [InlineData("tematica")]
    [InlineData("keyword")]
    [InlineData("_privada")]
    [InlineData("tema_2")]
    public void IsValidKey_AceptaClavesUsablesComoMarcador(string key) =>
        Assert.True(ExecutionVariables.IsValidKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2temas")]        // no puede empezar por digito
    [InlineData("tema tica")]     // el espacio rompe el marcador
    [InlineData("tema-tica")]
    [InlineData("tema.tica")]
    public void IsValidKey_RechazaLoQueNoPuedeSerUnMarcador(string? key) =>
        Assert.False(ExecutionVariables.IsValidKey(key));

    [Fact]
    public void IsValidKey_RechazaClavesDemasiadoLargas() =>
        Assert.False(ExecutionVariables.IsValidKey(new string('a', ExecutionVariables.MaxKeyLength + 1)));

    // ── Serializacion ──

    [Fact]
    public void SerializeYParse_ConservanLosValores()
    {
        var json = ExecutionVariables.Serialize(Valores(("tematica", "cafe"), ("keyword", "espresso")));
        var vuelta = ExecutionVariables.Parse(json);

        Assert.Equal("cafe", vuelta["tematica"]);
        Assert.Equal("espresso", vuelta["keyword"]);
    }

    [Fact]
    public void Serialize_SinValores_DevuelveNullParaNoGuardarColumnaVacia() =>
        Assert.Null(ExecutionVariables.Serialize(ExecutionVariables.NewMap()));

    [Fact]
    public void Parse_ConJsonCorrupto_NoRompeLaEjecucion()
    {
        // Preferimos correr sin variables a tumbar el pipeline entero.
        Assert.Empty(ExecutionVariables.Parse("{esto no es json"));
        Assert.Empty(ExecutionVariables.Parse("[1,2,3]"));
        Assert.Empty(ExecutionVariables.Parse(null));
    }

    [Fact]
    public void Parse_IgnoraClavesQueNoPuedenSerMarcadores()
    {
        var map = ExecutionVariables.Parse(@"{""tematica"":""cafe"",""no valida"":""x""}");

        Assert.Equal("cafe", map["tematica"]);
        Assert.Single(map);
    }

    // ── Resolucion ──

    [Fact]
    public void Resolve_TomaElValorQueAportaLaEjecucion()
    {
        var definiciones = new[] { Definicion("tematica") };

        var resuelto = ExecutionVariables.Resolve(definiciones, Valores(("tematica", "cafe de especialidad")));

        Assert.Equal("cafe de especialidad", resuelto["tematica"]);
    }

    [Fact]
    public void Resolve_SinValorEnLaEjecucion_LaVariableNoSeSustituyeYSeAvisa()
    {
        // Una variable no tiene valor fijo al que caer: o lo trae la ejecucion, o el
        // marcador se queda visible y el executor lo avisa en el log.
        var definiciones = new[] { Definicion("tematica") };

        var resuelto = ExecutionVariables.Resolve(definiciones, ExecutionVariables.NewMap());

        Assert.Empty(resuelto);
        Assert.Equal(new[] { "tematica" }, ExecutionVariables.MissingKeys(definiciones, resuelto));
    }

    [Fact]
    public void Resolve_ValorEnBlanco_CuentaComoSinValor()
    {
        var definiciones = new[] { Definicion("tematica") };

        var resuelto = ExecutionVariables.Resolve(definiciones, Valores(("tematica", "   ")));

        Assert.Empty(resuelto);
    }

    [Fact]
    public void Resolve_DescartaValoresDeVariablesQueElProyectoYaNoDeclara()
    {
        // Caso real: la programacion guardo "borrada" y despues se elimino la variable.
        var definiciones = new[] { Definicion("tematica") };

        var resuelto = ExecutionVariables.Resolve(definiciones, Valores(("borrada", "x"), ("tematica", "cafe")));

        Assert.False(resuelto.ContainsKey("borrada"));
        Assert.Single(resuelto);
    }

    // ── Sustitucion ──

    [Fact]
    public void Apply_SustituyeElMarcadorPorSuValor()
    {
        var texto = ExecutionVariables.Apply(
            "Escribe un post sobre {{tematica}} usando {{keyword}}.",
            Valores(("tematica", "cafe"), ("keyword", "espresso")));

        Assert.Equal("Escribe un post sobre cafe usando espresso.", texto);
    }

    [Fact]
    public void Apply_ToleraEspaciosYMayusculasEnElMarcador()
    {
        var texto = ExecutionVariables.Apply("{{ Tematica }}", Valores(("tematica", "cafe")));

        Assert.Equal("cafe", texto);
    }

    [Fact]
    public void Apply_DejaIntactoUnMarcadorQueNoEsVariable()
    {
        // Puede ser una plantilla ajena o un ejemplo de JSON: romperla seria peor.
        const string original = "Hola {{nombre}}, tema: {{tematica}}";

        var texto = ExecutionVariables.Apply(original, Valores(("tematica", "cafe")));

        Assert.Equal("Hola {{nombre}}, tema: cafe", texto);
    }

    [Fact]
    public void Apply_SinVariables_DevuelveElTextoTalCual()
    {
        const string original = "Prompt con {{tematica}}";

        Assert.Equal(original, ExecutionVariables.Apply(original, ExecutionVariables.NewMap()));
        Assert.Null(ExecutionVariables.Apply(null, Valores(("tematica", "cafe"))));
    }

    [Fact]
    public void ApplyToConfig_SustituyeEnLosCamposDeTextoYRespetaElResto()
    {
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["systemPrompt"] = "Habla de {{tematica}}",
            ["imagePrompt"] = JsonDocument.Parse(@"""Foto de {{tematica}}""").RootElement,
            ["maxTokens"] = 800,
        };

        var aplicado = ExecutionVariables.ApplyToConfig(config, Valores(("tematica", "cafe")));

        Assert.Equal("Habla de cafe", aplicado["systemPrompt"]);
        Assert.Equal("Foto de cafe", aplicado["imagePrompt"]);
        Assert.Equal(800, aplicado["maxTokens"]);
    }

    [Fact]
    public void ApplyToConfig_SinVariables_NoTocaLaConfiguracion()
    {
        var config = new Dictionary<string, object> { ["systemPrompt"] = "Habla de {{tematica}}" };

        var aplicado = ExecutionVariables.ApplyToConfig(config, ExecutionVariables.NewMap());

        Assert.Same(config, aplicado);
    }

    // ── Inyeccion en el system prompt ──

    [Fact]
    public void BuildBlock_ListaCadaVariableConSuDescripcion()
    {
        var definiciones = new[] { Definicion("tematica", "Tema principal del post") };

        var bloque = ExecutionVariables.BuildBlock(Valores(("tematica", "cafe")), definiciones);

        Assert.NotNull(bloque);
        Assert.StartsWith(ExecutionVariables.BlockHeader, bloque, StringComparison.Ordinal);
        Assert.Contains("- tematica (Tema principal del post): cafe", bloque, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBlock_SinVariables_NoAnadeNadaAlPrompt() =>
        Assert.Null(ExecutionVariables.BuildBlock(ExecutionVariables.NewMap()));

    [Fact]
    public void SystemPromptComposer_InyectaLasVariablesAntesDelPromptDelModulo()
    {
        // Son invariantes durante toda la ejecucion: van con los bloques cacheables,
        // por delante de lo que cambia en cada modulo.
        var contexto = new AiExecutionContext
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            ProjectContext = "CONTEXTO_PROYECTO",
            VariablesBlock = ExecutionVariables.BuildBlock(Valores(("tematica", "cafe"))),
            PreviousExecutionsSummary = "HISTORIAL_PREVIO",
            Configuration = new Dictionary<string, object> { ["systemPrompt"] = "PROMPT_MODULO" },
        };

        var prompt = SystemPromptComposer.Build(contexto);

        var contextoIdx = prompt.IndexOf("CONTEXTO_PROYECTO", StringComparison.Ordinal);
        var variables = prompt.IndexOf(ExecutionVariables.BlockHeader, StringComparison.Ordinal);
        var historial = prompt.IndexOf("HISTORIAL_PREVIO", StringComparison.Ordinal);
        var modulo = prompt.IndexOf("PROMPT_MODULO", StringComparison.Ordinal);

        Assert.True(variables > contextoIdx, "Las variables van despues del contexto del proyecto");
        Assert.True(variables < historial, "Las variables van antes del historial");
        Assert.True(variables < modulo, "Las variables van antes del prompt del modulo");
        Assert.Contains("tematica", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SinVariables_ElSystemPromptNoCambia()
    {
        AiExecutionContext Ctx(string? bloque) => new()
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            ProjectContext = "CONTEXTO_PROYECTO",
            VariablesBlock = bloque,
            Configuration = new Dictionary<string, object> { ["systemPrompt"] = "PROMPT_MODULO" },
        };

        Assert.Equal(SystemPromptComposer.Build(Ctx(null)), SystemPromptComposer.Build(Ctx("")));
    }
}
