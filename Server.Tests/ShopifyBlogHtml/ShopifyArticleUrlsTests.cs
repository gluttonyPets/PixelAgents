using System.Reflection;
using Server.Services.Shopify;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Verifica las URLs que el modulo devuelve tras crear el articulo: la del admin (unica
/// forma de revisar un borrador, porque Shopify no expone por API la vista previa de un
/// articulo sin publicar) y la publica de la tienda. Se invocan los metodos privados por
/// reflexion (misma tecnica que el resto de tests del proyecto).
/// </summary>
public class ShopifyArticleUrlsTests
{
    private static T Invoke<T>(string name, params object?[] args)
    {
        var method = typeof(ShopifyService)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }

    private static string? BuildAdminUrl(string shopDomain, string? gid) =>
        Invoke<string?>("BuildAdminUrl", shopDomain, gid);

    private static string? BuildPublicUrl(string? storeUrl, string? blogHandle, string? articleHandle) =>
        Invoke<string?>("BuildPublicUrl", storeUrl, blogHandle, articleHandle);

    private static string? ExtractNumericId(string? gid) =>
        Invoke<string?>("ExtractNumericId", gid);

    [Theory]
    [InlineData("gid://shopify/Article/123456789", "123456789")]
    [InlineData("gid://shopify/Article/123?locale=es", "123")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("123456789", "123456789")]
    // Un gid de otro formato no debe colarse como id numerico.
    [InlineData("gid://shopify/Article/abc", null)]
    public void ExtractNumericId_SoloAceptaIdsNumericos(string? gid, string? esperado)
    {
        Assert.Equal(esperado, ExtractNumericId(gid));
    }

    [Fact]
    public void AdminUrl_UsaElNuevoAdminConElHandleDeLaTienda()
    {
        Assert.Equal(
            "https://admin.shopify.com/store/zrfiuq-qu/articles/123456789",
            BuildAdminUrl("zrfiuq-qu.myshopify.com", "gid://shopify/Article/123456789"));
    }

    [Fact]
    public void AdminUrl_ToleraElDominioConProtocoloYBarra()
    {
        Assert.Equal(
            "https://admin.shopify.com/store/zrfiuq-qu/articles/1",
            BuildAdminUrl("https://zrfiuq-qu.myshopify.com/", "gid://shopify/Article/1"));
    }

    [Fact]
    public void AdminUrl_ConDominioPropioCaeEnLaRutaClasicaDelAdmin()
    {
        Assert.Equal(
            "https://tienda.gluttony.es/admin/articles/1",
            BuildAdminUrl("tienda.gluttony.es", "gid://shopify/Article/1"));
    }

    [Fact]
    public void AdminUrl_SinIdDevuelveNull()
    {
        Assert.Null(BuildAdminUrl("zrfiuq-qu.myshopify.com", null));
    }

    [Fact]
    public void PublicUrl_ComponeBlogsBlogArticulo()
    {
        Assert.Equal(
            "https://gluttony.es/blogs/consejos/mi-articulo",
            BuildPublicUrl("https://gluttony.es", "consejos", "mi-articulo"));
    }

    [Fact]
    public void PublicUrl_NoDuplicaLaBarraFinalDeLaTienda()
    {
        Assert.Equal(
            "https://gluttony.es/blogs/consejos/mi-articulo",
            BuildPublicUrl("https://gluttony.es/", "consejos", "mi-articulo"));
    }

    [Theory]
    [InlineData(null, "consejos", "mi-articulo")]
    [InlineData("https://gluttony.es", null, "mi-articulo")]
    [InlineData("https://gluttony.es", "consejos", null)]
    [InlineData("https://gluttony.es", "consejos", "  ")]
    public void PublicUrl_SinDatosSuficientesDevuelveNull(string? storeUrl, string? blog, string? article)
    {
        Assert.Null(BuildPublicUrl(storeUrl, blog, article));
    }
}
