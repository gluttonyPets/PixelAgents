using Server.Services.Shopify;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Handle (identificador URL) del articulo: normalizacion, alternativas cuando el
/// slug ya existe en la tienda y deteccion del userError
/// "Handle has already been taken", que era lo que tumbaba la publicacion.
/// </summary>
public class ShopifyHandleTests
{
    [Theory]
    [InlineData("Mi Artículo de Prueba", "mi-articulo-de-prueba")]
    [InlineData("  Ñandú  con   espacios ", "nandu-con-espacios")]
    [InlineData("¿Qué comer? ¡Ya!", "que-comer-ya")]
    [InlineData("", "articulo")]
    [InlineData(null, "articulo")]
    [InlineData("---", "articulo")]
    public void Slugify_NormalizaAcentosYSeparadores(string? texto, string esperado)
    {
        Assert.Equal(esperado, ShopifyHandle.Slugify(texto));
    }

    [Fact]
    public void Slugify_RecortaAlMaximoSinDejarGuionFinal()
    {
        var slug = ShopifyHandle.Slugify(new string('a', 200) + " cola");
        Assert.Equal(ShopifyHandle.MaxLength, slug.Length);
        Assert.DoesNotContain("-", slug);
    }

    [Theory]
    [InlineData(2, "mi-articulo-2")]
    [InlineData(3, "mi-articulo-3")]
    [InlineData(4, "mi-articulo-4")]
    public void Candidate_LosPrimerosReintentosLlevanSufijoNumerico(int intento, string esperado)
    {
        Assert.Equal(esperado, ShopifyHandle.Candidate("mi-articulo", intento, new DateTime(2026, 9, 5, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Candidate_PasadoElLimiteNumericoUsaLaFecha()
    {
        Assert.Equal(
            "mi-articulo-20260905123000",
            ShopifyHandle.Candidate("mi-articulo", ShopifyHandle.MaxNumericAttempts + 1,
                new DateTime(2026, 9, 5, 12, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void WithSuffix_RecortaLaBaseParaNoPasarseDelMaximo()
    {
        var handle = ShopifyHandle.WithSuffix(new string('a', 200), "20260905123000");
        Assert.True(handle.Length <= ShopifyHandle.MaxLength);
        Assert.EndsWith("-20260905123000", handle);
    }

    [Theory]
    // Lo que devuelve Shopify de verdad.
    [InlineData("handle", "Handle has already been taken", true)]
    [InlineData("article.handle", "has already been taken", true)]
    [InlineData(null, "Handle has already been taken", true)]
    // Otros errores no deben disparar el reintento.
    [InlineData("image", "Image upload failed. Invalid URL provided.", false)]
    [InlineData("handle", "Handle is invalid", false)]
    [InlineData("title", "Title has already been taken", false)]
    [InlineData(null, null, false)]
    public void IsTakenError_SoloDetectaElChoqueDeHandle(string? campo, string? mensaje, bool esperado)
    {
        Assert.Equal(esperado, ShopifyHandle.IsTakenError(campo, mensaje));
    }
}
