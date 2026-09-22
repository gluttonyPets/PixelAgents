using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.BusquedaInternet;

/// <summary>
/// Opcion "Busqueda en internet" de los modulos de texto. Lo que se blinda: que
/// un modulo antiguo (sin la clave) no empiece a buscar y a gastar por su cuenta,
/// que Claude reciba las herramientas que su modelo admite y que la narracion
/// intermedia ("voy a buscar...") no acabe en la salida del modulo.
/// </summary>
public class WebSearchOptionTests
{
    [Fact]
    public void IsEnabled_SinClave_EstaDesactivada()
    {
        Assert.False(WebSearchOption.IsEnabled(new Dictionary<string, object>()));
        Assert.False(WebSearchOption.IsEnabled(null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(1L, false)]
    public void IsEnabled_LeeElValorGuardado(object valor, bool esperado)
    {
        var config = new Dictionary<string, object> { ["webSearch"] = valor };
        Assert.Equal(esperado, WebSearchOption.IsEnabled(config));
    }

    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-sonnet-5")]
    [InlineData("claude-fable-5-1")]
    public void AnthropicTools_ModelosRecientes_BuscanYLeenUrls(string modelo)
    {
        var tipos = WebSearchOption.AnthropicTools(modelo).Select(t => (string)t["type"]).ToList();
        Assert.Equal(["web_search_20260209", "web_fetch_20260209"], tipos);
    }

    [Theory]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-sonnet-4-5")]
    public void AnthropicTools_ModelosAnteriores_SoloBusquedaBasica(string modelo)
    {
        var tipos = WebSearchOption.AnthropicTools(modelo).Select(t => (string)t["type"]).ToList();
        Assert.Equal(["web_search_20250305"], tipos);
    }

    [Fact]
    public void ExtractFinalText_ConHerramientas_SoloTextoTrasElUltimoResultado()
    {
        var bloques = Blocks("""
            [
              {"type":"text","text":"Voy a buscar eso."},
              {"type":"server_tool_use","id":"a","name":"web_search","input":{"query":"x"}},
              {"type":"web_search_tool_result","tool_use_id":"a","content":[]},
              {"type":"text","text":"Ahora leo la pagina."},
              {"type":"server_tool_use","id":"b","name":"web_fetch","input":{"url":"https://e.com"}},
              {"type":"web_fetch_tool_result","tool_use_id":"b","content":{}},
              {"type":"text","text":"Respuesta "},
              {"type":"text","text":"final."}
            ]
            """);

        Assert.Equal("Respuesta final.", AnthropicProvider.ExtractFinalText(bloques));
    }

    [Fact]
    public void ExtractFinalText_SinHerramientas_DevuelveTodoElTexto()
    {
        var bloques = Blocks("""[{"type":"text","text":"Hola "},{"type":"text","text":"mundo"}]""");
        Assert.Equal("Hola mundo", AnthropicProvider.ExtractFinalText(bloques));
    }

    [Fact]
    public void ExtractFinalText_SinTextoTrasLasHerramientas_NoDevuelveVacio()
    {
        var bloques = Blocks("""
            [
              {"type":"text","text":"Lo que encontre."},
              {"type":"web_search_tool_result","tool_use_id":"a","content":[]}
            ]
            """);

        Assert.Equal("Lo que encontre.", AnthropicProvider.ExtractFinalText(bloques));
    }

    private static List<JsonElement> Blocks(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
}
