using System.Reflection;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Verifica que el modulo de blog de Shopify envie el HTML (incluido el CSS inline
/// y los bloques &lt;style&gt;) sin escaparlo, y que el texto plano se siga
/// convirtiendo en parrafos. Se invoca el metodo privado <c>ToHtml</c> por reflexion
/// (misma tecnica que el resto de tests estructurales del proyecto).
/// </summary>
public class ShopifyBlogHtmlTests
{
    private static string ToHtml(string input)
    {
        var method = typeof(ShopifyBlogModuleHandler)
            .GetMethod("ToHtml", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object[] { input })!;
    }

    [Fact]
    public void Html_ConCssInline_PasaVerbatim()
    {
        var input = "<p style=\"color:#333;font-size:18px\">Hola <strong>mundo</strong></p>";
        Assert.Equal(input, ToHtml(input));
    }

    [Fact]
    public void Html_ConBloqueStyle_PasaVerbatim()
    {
        var input = "<style>.destacado{color:red}</style><div class=\"destacado\">Texto</div>";
        Assert.Equal(input, ToHtml(input));
    }

    [Theory]
    // Etiquetas que antes NO se detectaban y acababan escapadas.
    [InlineData("<table><tr><td>Celda</td></tr></table>")]
    [InlineData("<section><h3>Titular</h3></section>")]
    [InlineData("<span style=\"font-weight:bold\">Solo span</span>")]
    [InlineData("<img src=\"https://x/y.jpg\" alt=\"foto\" />")]
    public void Html_ConEtiquetasVariadas_PasaVerbatim(string input)
    {
        Assert.Equal(input, ToHtml(input));
    }

    [Fact]
    public void TextoPlano_SeEscapaYSeEnvuelveEnParrafo()
    {
        var result = ToHtml("Hola & bienvenido a casa");
        Assert.Equal("<p>Hola &amp; bienvenido a casa</p>", result);
    }

    [Fact]
    public void TextoPlano_ConComparacionMatematica_NoSeConfundeConHtml()
    {
        // "5 < 10" no debe interpretarse como una etiqueta HTML.
        var result = ToHtml("Si 5 < 10 entonces gana");
        Assert.StartsWith("<p>", result);
        Assert.Contains("5 &lt; 10", result);
    }

    [Fact]
    public void TextoPlano_DoblesSaltos_GeneranParrafosSeparados()
    {
        var result = ToHtml("Primero\n\nSegundo");
        Assert.Equal("<p>Primero</p><p>Segundo</p>", result);
    }
}
