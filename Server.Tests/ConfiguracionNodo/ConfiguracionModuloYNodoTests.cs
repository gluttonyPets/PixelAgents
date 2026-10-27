using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ConfiguracionNodo;

/// <summary>
/// Configuracion efectiva de un nodo: la del modulo con la del nodo por encima.
///
/// Al anadir un modulo a un pipeline se copiaba en el nodo toda su configuracion,
/// y esa copia ganaba despues a cualquier cambio del modulo: en un pipeline real
/// se cambio la voz de ElevenLabs a Paloma con estabilidad 0.4 y el nodo seguia
/// locutando con Carla a 0.7. Lo que solo se edita en el modulo sale siempre del
/// modulo; lo que el canvas cambia por nodo sigue siendo del nodo.
/// </summary>
public class ConfiguracionModuloYNodoTests
{
    [Fact]
    public void LaVozYSusAjustesSalenDelModulo_AunqueElNodoTengaUnaCopiaVieja()
    {
        const string modulo = """
            {"voice":"paloma","voiceName":"Paloma","voiceOwner":"o2","stability":0.4,"similarityBoost":0.75,"style":0,"speed":0.95}
            """;
        const string nodo = """
            {"voice":"carla","voiceName":"Carla","voiceOwner":"o1","stability":0.7,"similarityBoost":0.75,"style":0,"skipped":false}
            """;

        var config = GraphPipelineExecutor.MergeConfiguration(modulo, nodo);

        Assert.Equal("paloma", config["voice"]);
        Assert.Equal("o2", config["voiceOwner"]);
        Assert.Equal(0.4, config["stability"]);
        Assert.Equal(0.95, config["speed"]);
        Assert.Equal(false, config["skipped"]);
    }

    [Fact]
    public void ElPromptYElTamanoDeImagenSalenDelModulo()
    {
        var config = GraphPipelineExecutor.MergeConfiguration(
            """{"imagePrompt":"nuevo","size":"1024x1536","quality":"high"}""",
            """{"imagePrompt":"viejo","size":"auto","quality":"low","webSearch":true}""");

        Assert.Equal("nuevo", config["imagePrompt"]);
        Assert.Equal("1024x1536", config["size"]);
        Assert.Equal("high", config["quality"]);
        // Tampoco cuenta una clave del modulo que el modulo ya no tiene.
        Assert.False(config.ContainsKey("webSearch"));
    }

    [Fact]
    public void LoQueElCanvasCambiaPorNodoSigueGanando()
    {
        var config = GraphPipelineExecutor.MergeConfiguration(
            """{"n":1,"modelName":"gpt-5.4","caption":"del modulo"}""",
            """{"n":3,"sceneCount":4,"modelName":"gpt-5.6-sol","loopMode":true,"caption":"del nodo","skipped":true}""");

        Assert.Equal(3, Convert.ToInt32(config["n"]));
        Assert.Equal(4, Convert.ToInt32(config["sceneCount"]));
        Assert.Equal("gpt-5.6-sol", config["modelName"]);
        Assert.Equal(true, config["loopMode"]);
        Assert.Equal("del nodo", config["caption"]);
        Assert.Equal(true, config["skipped"]);
    }

    [Fact]
    public void UnNodoNuevoSoloSeQuedaConLoQueEsDelNodo()
    {
        // Es lo que se copia al anadir un modulo al pipeline o al duplicar el proyecto.
        Assert.Equal("""{"n":2}""",
            ModuleConfigKeys.NodeOnly("""{"systemPrompt":"x","size":"auto","n":2,"voice":"v"}"""));
        Assert.Null(ModuleConfigKeys.NodeOnly("""{"voice":"v","stability":0.5}"""));
        Assert.Null(ModuleConfigKeys.NodeOnly(null));
        Assert.Null(ModuleConfigKeys.NodeOnly("no es json"));
        Assert.Null(ModuleConfigKeys.NodeOnly("[1,2]"));
    }
}
