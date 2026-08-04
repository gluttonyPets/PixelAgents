using System.Reflection;
using System.Text.Json;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Verifica como se leen las casillas de la config de un nodo. El editor las guarda como
/// CADENA ("true"/"false"), no como booleano JSON: leer solo JsonValueKind.True hacia que
/// una casilla marcada se interpretara como false y el nodo ShopifyBlog dejara el
/// articulo en borrador aunque "Publicar" estuviese activado. Se invoca el metodo privado
/// por reflexion (misma tecnica que el resto de tests del proyecto).
/// </summary>
public class ConfigBoolTests
{
    private static bool ParseConfigBool(object? val, bool fallback)
    {
        var method = typeof(ModuleExecutionContext)
            .GetMethod("ParseConfigBool", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { val, fallback })!;
    }

    /// <summary>Reproduce como llega el valor tras deserializar la config guardada.</summary>
    private static JsonElement Json(string rawJson) =>
        JsonDocument.Parse(rawJson).RootElement.Clone();

    [Theory]
    // Asi lo guarda el editor: cadena, no booleano.
    [InlineData("\"true\"", true)]
    [InlineData("\"True\"", true)]
    [InlineData("\"false\"", false)]
    // Booleano JSON (config escrita a mano o por API).
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void CasillaGuardada_SeLeeConSuValor(string rawJson, bool esperado)
    {
        Assert.Equal(esperado, ParseConfigBool(Json(rawJson), fallback: !esperado));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"quiza\"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ValorNoInterpretable_UsaElValorPorDefecto(string rawJson)
    {
        Assert.True(ParseConfigBool(Json(rawJson), fallback: true));
        Assert.False(ParseConfigBool(Json(rawJson), fallback: false));
    }

    [Fact]
    public void BoolNativoYCadenaSuelta_TambienSeSoportan()
    {
        Assert.True(ParseConfigBool(true, fallback: false));
        Assert.False(ParseConfigBool(false, fallback: true));
        Assert.True(ParseConfigBool("true", fallback: false));
        Assert.True(ParseConfigBool(null, fallback: true));
    }
}
