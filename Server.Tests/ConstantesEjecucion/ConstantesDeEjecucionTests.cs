using System.Text.Json;
using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ConstantesEjecucion;

/// <summary>
/// Constantes del pipeline: el proyecto declara las claves ("tematica", "keyword")
/// y cada ejecucion fija su valor. El valor se sustituye en los prompts y viaja
/// ademas como bloque etiquetado en el system prompt de todos los modulos.
/// </summary>
public class ConstantesDeEjecucionTests
{
    private static ProjectConstant Definicion(string key, string? porDefecto = null, string? descripcion = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Key = key,
            DefaultValue = porDefecto,
            Description = descripcion,
        };

    private static Dictionary<string, string> Valores(params (string Key, string Value)[] pares)
    {
        var map = ExecutionConstants.NewMap();
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
        Assert.True(ExecutionConstants.IsValidKey(key));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2temas")]        // no puede empezar por digito
    [InlineData("tema tica")]     // el espacio rompe el marcador
    [InlineData("tema-tica")]
    [InlineData("tema.tica")]
    public void IsValidKey_RechazaLoQueNoPuedeSerUnMarcador(string? key) =>
        Assert.False(ExecutionConstants.IsValidKey(key));

    [Fact]
    public void IsValidKey_RechazaClavesDemasiadoLargas() =>
        Assert.False(ExecutionConstants.IsValidKey(new string('a', ExecutionConstants.MaxKeyLength + 1)));

    // ── Serializacion ──

    [Fact]
    public void SerializeYParse_ConservanLosValores()
    {
        var json = ExecutionConstants.Serialize(Valores(("tematica", "cafe"), ("keyword", "espresso")));
        var vuelta = ExecutionConstants.Parse(json);

        Assert.Equal("cafe", vuelta["tematica"]);
        Assert.Equal("espresso", vuelta["keyword"]);
    }

    [Fact]
    public void Serialize_SinValores_DevuelveNullParaNoGuardarColumnaVacia() =>
        Assert.Null(ExecutionConstants.Serialize(ExecutionConstants.NewMap()));

    [Fact]
    public void Parse_ConJsonCorrupto_NoRompeLaEjecucion()
    {
        // Preferimos correr sin constantes a tumbar el pipeline entero.
        Assert.Empty(ExecutionConstants.Parse("{esto no es json"));
        Assert.Empty(ExecutionConstants.Parse("[1,2,3]"));
        Assert.Empty(ExecutionConstants.Parse(null));
    }

    [Fact]
    public void Parse_IgnoraClavesQueNoPuedenSerMarcadores()
    {
        var map = ExecutionConstants.Parse(@"{""tematica"":""cafe"",""no valida"":""x""}");

        Assert.Equal("cafe", map["tematica"]);
        Assert.Single(map);
    }

    // ── Resolucion ──

    [Fact]
    public void Resolve_ElValorDeLaEjecucionGanaAlValorPorDefecto()
    {
        var definiciones = new[] { Definicion("tematica", "generico") };

        var resuelto = ExecutionConstants.Resolve(definiciones, Valores(("tematica", "cafe de especialidad")));

        Assert.Equal("cafe de especialidad", resuelto["tematica"]);
    }

    [Fact]
    public void Resolve_SinValorPropio_CaeAlValorPorDefecto()
    {
        var definiciones = new[] { Definicion("tematica", "generico") };

        var resuelto = ExecutionConstants.Resolve(definiciones, ExecutionConstants.NewMap());

        Assert.Equal("generico", resuelto["tematica"]);
    }

    [Fact]
    public void Resolve_ValorEnBlanco_CaeAlValorPorDefecto()
    {
        var definiciones = new[] { Definicion("tematica", "generico") };

        var resuelto = ExecutionConstants.Resolve(definiciones, Valores(("tematica", "   ")));

        Assert.Equal("generico", resuelto["tematica"]);
    }

    [Fact]
    public void Resolve_SinValorNiDefecto_NoDefineLaConstante()
    {
        // Sin valor no se sustituye nada: el marcador queda visible y el executor avisa.
        var definiciones = new[] { Definicion("tematica") };

        var resuelto = ExecutionConstants.Resolve(definiciones, ExecutionConstants.NewMap());

        Assert.Empty(resuelto);
        Assert.Equal(new[] { "tematica" }, ExecutionConstants.MissingKeys(definiciones, resuelto));
    }

    [Fact]
    public void Resolve_DescartaValoresDeConstantesQueElProyectoYaNoDeclara()
    {
        // Caso real: la programacion guardo "borrada" y despues se elimino la constante.
        var definiciones = new[] { Definicion("tematica", "generico") };

        var resuelto = ExecutionConstants.Resolve(definiciones, Valores(("borrada", "x")));

        Assert.False(resuelto.ContainsKey("borrada"));
        Assert.Single(resuelto);
    }

    // ── Sustitucion ──

    [Fact]
    public void Apply_SustituyeElMarcadorPorSuValor()
    {
        var texto = ExecutionConstants.Apply(
            "Escribe un post sobre {{tematica}} usando {{keyword}}.",
            Valores(("tematica", "cafe"), ("keyword", "espresso")));

        Assert.Equal("Escribe un post sobre cafe usando espresso.", texto);
    }

    [Fact]
    public void Apply_ToleraEspaciosYMayusculasEnElMarcador()
    {
        var texto = ExecutionConstants.Apply("{{ Tematica }}", Valores(("tematica", "cafe")));

        Assert.Equal("cafe", texto);
    }

    [Fact]
    public void Apply_DejaIntactoUnMarcadorQueNoEsConstante()
    {
        // Puede ser una plantilla ajena o un ejemplo de JSON: romperla seria peor.
        const string original = "Hola {{nombre}}, tema: {{tematica}}";

        var texto = ExecutionConstants.Apply(original, Valores(("tematica", "cafe")));

        Assert.Equal("Hola {{nombre}}, tema: cafe", texto);
    }

    [Fact]
    public void Apply_SinConstantes_DevuelveElTextoTalCual()
    {
        const string original = "Prompt con {{tematica}}";

        Assert.Equal(original, ExecutionConstants.Apply(original, ExecutionConstants.NewMap()));
        Assert.Null(ExecutionConstants.Apply(null, Valores(("tematica", "cafe"))));
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

        var aplicado = ExecutionConstants.ApplyToConfig(config, Valores(("tematica", "cafe")));

        Assert.Equal("Habla de cafe", aplicado["systemPrompt"]);
        Assert.Equal("Foto de cafe", aplicado["imagePrompt"]);
        Assert.Equal(800, aplicado["maxTokens"]);
    }

    [Fact]
    public void ApplyToConfig_SinConstantes_NoTocaLaConfiguracion()
    {
        var config = new Dictionary<string, object> { ["systemPrompt"] = "Habla de {{tematica}}" };

        var aplicado = ExecutionConstants.ApplyToConfig(config, ExecutionConstants.NewMap());

        Assert.Same(config, aplicado);
    }

    // ── Inyeccion en el system prompt ──

    [Fact]
    public void BuildBlock_ListaCadaConstanteConSuDescripcion()
    {
        var definiciones = new[] { Definicion("tematica", descripcion: "Tema principal del post") };

        var bloque = ExecutionConstants.BuildBlock(Valores(("tematica", "cafe")), definiciones);

        Assert.NotNull(bloque);
        Assert.StartsWith(ExecutionConstants.BlockHeader, bloque, StringComparison.Ordinal);
        Assert.Contains("- tematica (Tema principal del post): cafe", bloque, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBlock_SinConstantes_NoAnadeNadaAlPrompt() =>
        Assert.Null(ExecutionConstants.BuildBlock(ExecutionConstants.NewMap()));

    [Fact]
    public void SystemPromptComposer_InyectaLasConstantesAntesDelPromptDelModulo()
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
            ConstantsBlock = ExecutionConstants.BuildBlock(Valores(("tematica", "cafe"))),
            PreviousExecutionsSummary = "HISTORIAL_PREVIO",
            Configuration = new Dictionary<string, object> { ["systemPrompt"] = "PROMPT_MODULO" },
        };

        var prompt = SystemPromptComposer.Build(contexto);

        var contextoIdx = prompt.IndexOf("CONTEXTO_PROYECTO", StringComparison.Ordinal);
        var constantes = prompt.IndexOf(ExecutionConstants.BlockHeader, StringComparison.Ordinal);
        var historial = prompt.IndexOf("HISTORIAL_PREVIO", StringComparison.Ordinal);
        var modulo = prompt.IndexOf("PROMPT_MODULO", StringComparison.Ordinal);

        Assert.True(constantes > contextoIdx, "Las constantes van despues del contexto del proyecto");
        Assert.True(constantes < historial, "Las constantes van antes del historial");
        Assert.True(constantes < modulo, "Las constantes van antes del prompt del modulo");
        Assert.Contains("tematica", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SinConstantes_ElSystemPromptNoCambia()
    {
        AiExecutionContext Ctx(string? bloque) => new()
        {
            ModuleType = "Text",
            ModelName = "gpt-5.2",
            ApiKey = "k",
            Input = "entrada",
            ProjectContext = "CONTEXTO_PROYECTO",
            ConstantsBlock = bloque,
            Configuration = new Dictionary<string, object> { ["systemPrompt"] = "PROMPT_MODULO" },
        };

        Assert.Equal(SystemPromptComposer.Build(Ctx(null)), SystemPromptComposer.Build(Ctx("")));
    }
}
