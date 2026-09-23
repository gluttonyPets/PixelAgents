using System.Reflection;
using Server.Services.Ai.Handlers;
using Server.Services.Shopify;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Modo "Modificar un articulo existente" del nodo ShopifyBlog: como se interpreta
/// el articulo a modificar y que campos se consideran cambiados.
/// </summary>
public class ShopifyArticleUpdateTests
{
    [Theory]
    [InlineData("https://gluttony.es/blogs/consejos-mascotas/como-quitar-pelo-perro-sofa-facilmente", "consejos-mascotas", "como-quitar-pelo-perro-sofa-facilmente")]
    [InlineData("https://gluttony.es/blogs/consejos-mascotas/mi-articulo?utm_source=x#seccion", "consejos-mascotas", "mi-articulo")]
    [InlineData("https://gluttony.es/en/blogs/news/my-post/", "news", "my-post")]
    [InlineData("/blogs/consejos-mascotas/mi-articulo", "consejos-mascotas", "mi-articulo")]
    [InlineData("  mi-articulo  ", null, "mi-articulo")]
    public void Parse_UrlOHandle(string raw, string? blog, string handle)
    {
        var target = ShopifyArticleTarget.Parse(raw);

        Assert.NotNull(target);
        Assert.Null(target!.ArticleGid);
        Assert.Equal(blog, target.BlogHandle);
        Assert.Equal(handle, target.Handle);
    }

    [Theory]
    [InlineData("gid://shopify/Article/123", "gid://shopify/Article/123")]
    [InlineData("123", "gid://shopify/Article/123")]
    [InlineData("https://admin.shopify.com/store/gluttony/articles/456", "gid://shopify/Article/456")]
    public void Parse_IdOGidOUrlDelAdmin(string raw, string gid)
    {
        Assert.Equal(gid, ShopifyArticleTarget.Parse(raw)!.ArticleGid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://gluttony.es/blogs/consejos-mascotas")]
    [InlineData("https://gluttony.es/products/pala-arenero")]
    public void Parse_SinArticulo_DevuelveNull(string raw)
    {
        Assert.Null(ShopifyArticleTarget.Parse(raw));
    }

    [Fact]
    public void ChangedFields_SoloLosQueTraenValor()
    {
        var changes = new ShopifyArticleChanges
        {
            Title = "Nuevo titulo",
            BodyHtml = " ",
            SeoTitle = "SEO",
            MetaDescription = null,
            Tags = [],
        };

        Assert.Equal(["titulo", "titulo SEO"], changes.ChangedFields());
    }

    [Fact]
    public void JsonDelModuloAnterior_TraeElArticuloAModificar()
    {
        // StructuredArticle es interno al servidor: se invoca por reflexion, como el
        // resto de tests del nodo.
        var type = typeof(ShopifyBlogModuleHandler).Assembly.GetType("Server.Services.Ai.Handlers.StructuredArticle")!;
        var parse = type.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static)!;

        var article = parse.Invoke(null, ["""
            {"articulo_url": "https://gluttony.es/blogs/consejos-mascotas/mi-articulo",
             "seo_titulo": "Titulo nuevo", "cuerpo": ""}
            """]);

        Assert.Equal("https://gluttony.es/blogs/consejos-mascotas/mi-articulo",
            type.GetProperty("TargetArticle")!.GetValue(article));
        Assert.Equal("Titulo nuevo", type.GetProperty("SeoTitle")!.GetValue(article));
        // Un cuerpo vacio no cuenta: en modo actualizar no se toca el cuerpo.
        Assert.Null(type.GetProperty("Body")!.GetValue(article));
    }
}
