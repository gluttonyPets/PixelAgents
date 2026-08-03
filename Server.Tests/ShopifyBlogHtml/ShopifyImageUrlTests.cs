using System.Reflection;
using Server.Services.Ai.Handlers;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Verifica el filtro que decide si la URL de la imagen destacada puede enviarse a
/// Shopify. Shopify descarga la imagen desde esa URL al crear el articulo, por lo que
/// una URL relativa (PublicBaseUrl sin configurar) o que apunte a localhost/red privada
/// la rechaza con "Image upload failed. Invalid URL provided." y con ella el articulo
/// entero. En esos casos el modulo debe publicar sin imagen en vez de fallar. Se invoca
/// el metodo privado por reflexion (misma tecnica que el resto de tests del proyecto).
/// </summary>
public class ShopifyImageUrlTests
{
    private static bool IsPubliclyReachableImageUrl(string? url)
    {
        var method = typeof(ShopifyBlogModuleHandler)
            .GetMethod("IsPubliclyReachableImageUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, new object?[] { url })!;
    }

    [Theory]
    [InlineData("https://tienda.gluttony.es/api/public/files/t/e/f/output.png")]
    [InlineData("https://cdn.example.com/imagen.jpg")]
    [InlineData("https://203.0.113.10/api/public/files/t/e/f/output.png")]
    public void UrlPublicaHttps_EsAceptada(string url)
    {
        Assert.True(IsPubliclyReachableImageUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // URL relativa: PublicBaseUrl vacio -> GetPublicFileUrl devuelve solo la ruta.
    [InlineData("/api/public/files/tenant/exec/file/output.png")]
    // http plano sobre IP publica y puerto raro: es EXACTAMENTE lo que genera el
    // docker-compose por defecto (BaseUrl "http://${PUBLIC_IP}:${APP_PORT}") y lo que
    // Shopify rechazaba con "Invalid URL provided".
    [InlineData("http://203.0.113.10:8080/api/public/files/t/e/f/output.png")]
    [InlineData("http://cdn.example.com/imagen.jpg")]
    // localhost y loopback: Shopify no los alcanza.
    [InlineData("https://localhost:5000/api/public/files/t/e/f/output.png")]
    [InlineData("https://127.0.0.1:5000/api/public/files/t/e/f/output.png")]
    [InlineData("https://[::1]:5000/api/public/files/t/e/f/output.png")]
    // Redes privadas / link-local.
    [InlineData("https://10.0.0.5/output.png")]
    [InlineData("https://172.16.4.9/output.png")]
    [InlineData("https://192.168.1.20/output.png")]
    [InlineData("https://169.254.1.1/output.png")]
    // Host interno sin dominio publico.
    [InlineData("https://servidor-interno/output.png")]
    // Esquemas no descargables por Shopify.
    [InlineData("ftp://example.com/output.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    public void UrlNoDescargablePorShopify_EsRechazada(string? url)
    {
        Assert.False(IsPubliclyReachableImageUrl(url));
    }
}
