using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// Extraccion de las imagenes que un modelo elige citando su URL. Lo que se
/// prueba aqui es sobre todo lo que NO se debe descargar: el texto lo escribe un
/// modelo, y seguir cualquier URL que aparezca convertiria el servidor en un
/// proxy hacia donde diga el prompt.
/// </summary>
public class ReferenceImageFetcherTests
{
    private const string Base = "https://pixel.ejemplo.com";
    private const string Dir = Base + "/api/public/directory/tenant_x/11111111-1111-1111-1111-111111111111";

    [Fact]
    public void CogeLasUrlsDelDirectorioQueAparecenEnElTexto()
    {
        var texto = $"Voy a usar {Dir}/Pala%20Arenero/main.jpg y tambien {Dir}/Pala%20Arenero/lateral.png";

        var urls = ReferenceImageFetcher.ExtractDirectoryUrls(texto, Base);

        Assert.Equal(
            [$"{Dir}/Pala%20Arenero/main.jpg", $"{Dir}/Pala%20Arenero/lateral.png"],
            urls);
    }

    [Theory]
    [InlineData("https://otro-sitio.com/imagen.jpg")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://pixel.ejemplo.com.atacante.com/api/public/directory/x/y/z.jpg")]
    [InlineData("https://pixel.ejemplo.com/api/public/files/tenant/exec/id/otro.jpg")]
    public void NoSigueUrlsAjenasAlDirectorioDeEsteServidor(string url)
    {
        var urls = ReferenceImageFetcher.ExtractDirectoryUrls($"usa esta: {url}", Base);

        Assert.Empty(urls);
    }

    [Fact]
    public void LasUrlsDelDirectorio_SeCambianPorElNombreDelFichero()
    {
        // Ya descargadas y adjuntas, la URL solo gasta caracteres del limite del
        // prompt y un modelo que dibuja texto puede acabar pintandola.
        var texto = $"- cepillo: cepillo quitapelos 1\n  URL: {Dir}/Cepillo%20Quitapelos/cepillo%20vertical.png";

        var limpio = ReferenceImageFetcher.ReplaceUrlsWithNames(texto, Base);

        Assert.Equal("- cepillo: cepillo quitapelos 1\n  URL: cepillo vertical.png", limpio);
        Assert.True(limpio.Length < texto.Length);
    }

    [Fact]
    public void LaPuntuacionQueSigueALaUrl_SeConserva()
    {
        var limpio = ReferenceImageFetcher.ReplaceUrlsWithNames($"usa {Dir}/a/main.jpg.", Base);

        Assert.Equal("usa main.jpg.", limpio);
    }

    [Theory]
    [InlineData("https://otro-sitio.com/imagen.jpg")]
    [InlineData("https://pixel.ejemplo.com/api/public/files/tenant/exec/id/otro.jpg")]
    public void LasUrlsAjenasAlDirectorio_NoSeTocan(string url)
    {
        Assert.Equal($"mira {url}", ReferenceImageFetcher.ReplaceUrlsWithNames($"mira {url}", Base));
    }

    [Fact]
    public void SinUrlPublicaConfigurada_ElTextoNoSeToca()
    {
        var texto = $"usa {Dir}/a/main.jpg";

        Assert.Equal(texto, ReferenceImageFetcher.ReplaceUrlsWithNames(texto, null));
    }

    [Fact]
    public void SinUrlPublicaConfigurada_NoDescargaNada()
    {
        // Sin base con la que comparar no hay lista blanca posible, y aceptar
        // cualquier host seria justo lo que se quiere evitar.
        var urls = ReferenceImageFetcher.ExtractDirectoryUrls($"usa {Dir}/a.jpg", publicBaseUrl: null);

        Assert.Empty(urls);
    }

    [Fact]
    public void NoRepiteLaMismaUrl()
    {
        var texto = $"{Dir}/a.jpg y otra vez {Dir}/a.jpg";

        Assert.Single(ReferenceImageFetcher.ExtractDirectoryUrls(texto, Base));
    }

    [Fact]
    public void RespetaElMaximoYPermiteContarLasQueSeIgnoran()
    {
        var texto = string.Join(" ", Enumerable.Range(1, 10).Select(i => $"{Dir}/img{i}.jpg"));

        Assert.Equal(3, ReferenceImageFetcher.ExtractDirectoryUrls(texto, Base, max: 3).Count);
        Assert.Equal(10, ReferenceImageFetcher.CountDirectoryUrls(texto, Base));
    }

    [Fact]
    public void ConMaximoCero_NoCogeNinguna()
    {
        Assert.Empty(ReferenceImageFetcher.ExtractDirectoryUrls($"{Dir}/a.jpg", Base, max: 0));
    }

    [Theory]
    // Puntuacion pegada al final de la frase: no forma parte de la ruta.
    [InlineData("usa {URL}.", "{URL}")]
    [InlineData("usa {URL}, y luego otra", "{URL}")]
    [InlineData("(ver {URL})", "{URL}")]
    public void NoSeTragaLaPuntuacionQueRodeaALaUrl(string plantilla, string esperada)
    {
        var url = $"{Dir}/a.jpg";
        var urls = ReferenceImageFetcher.ExtractDirectoryUrls(plantilla.Replace("{URL}", url), Base);

        Assert.Equal(esperada.Replace("{URL}", url), Assert.Single(urls));
    }

    [Fact]
    public void ElNombreDeLaReferenciaSaleLegible()
    {
        Assert.Equal(
            "Main contraste.png",
            ReferenceImageFetcher.FileNameOf($"{Dir}/Pala%20Arenero/Main%20contraste.png"));
    }
}
