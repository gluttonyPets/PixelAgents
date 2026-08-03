using System.Reflection;
using System.Text;
using Server.Services.Shopify;
using Xunit;

namespace Server.Tests.ShopifyBlogHtml;

/// <summary>
/// Verifica el cuerpo multipart del "staged upload" de la imagen destacada. El destino
/// que devuelve Shopify es Google Cloud Storage y su parser es estricto: con el boundary
/// entrecomillado que genera MultipartFormDataContent por defecto responde
/// 400 "Malformed multipart body", y si el campo "file" no va el ultimo responde
/// SignatureDoesNotMatch. Se invoca el metodo privado por reflexion (misma tecnica que el
/// resto de tests del proyecto).
/// </summary>
public class ShopifyStagedUploadTests
{
    private static MultipartFormDataContent Build(
        IEnumerable<(string Name, string Value)> formParams,
        byte[] data, string fileName, string mimeType)
    {
        var method = typeof(ShopifyService)
            .GetMethod("BuildStagedUploadContent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (MultipartFormDataContent)method!.Invoke(
            null, new object?[] { formParams, data, fileName, mimeType })!;
    }

    private static readonly (string Name, string Value)[] SignedParams =
    {
        ("key", "tmp/123/output.png"),
        ("policy", "eyJleHBpcmF0aW9uIjoi"),
        ("signature", "abc123"),
        ("content_type", "image/png"),
    };

    [Fact]
    public void BoundaryNoVaEntrecomillado()
    {
        using var form = Build(SignedParams, new byte[] { 1, 2, 3 }, "output.png", "image/png");

        var contentType = form.Headers.ContentType!.ToString();
        Assert.StartsWith("multipart/form-data; boundary=", contentType);
        Assert.DoesNotContain("\"", contentType);
    }

    [Fact]
    public async Task ElBoundaryDeLaCabeceraEsElQueSeparaLasPartes()
    {
        using var form = Build(SignedParams, new byte[] { 1, 2, 3 }, "output.png", "image/png");

        var boundary = form.Headers.ContentType!.Parameters
            .First(p => p.Name == "boundary").Value!;
        var body = await form.ReadAsStringAsync();

        Assert.Contains($"--{boundary}\r\n", body);
        Assert.EndsWith($"--{boundary}--\r\n", body);
    }

    [Fact]
    public async Task ElCampoFileVaDespuesDeLosParametrosFirmados()
    {
        using var form = Build(SignedParams, Encoding.UTF8.GetBytes("PNGDATA"), "output.png", "image/png");

        var body = await form.ReadAsStringAsync();
        var filePos = body.IndexOf("name=\"file\"", StringComparison.Ordinal);

        Assert.True(filePos > 0, "falta el campo file");
        foreach (var (name, _) in SignedParams)
        {
            var pos = body.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
            Assert.True(pos > 0, $"falta el parametro firmado {name}");
            Assert.True(pos < filePos, $"el parametro firmado {name} debe ir antes de file");
        }

        Assert.Contains("filename=\"output.png\"", body);
        Assert.Contains("Content-Type: image/png", body);
        Assert.Contains("PNGDATA", body);
    }

    [Fact]
    public async Task LosParametrosFirmadosNoLlevanContentType()
    {
        using var form = Build(SignedParams, new byte[] { 1 }, "output.png", "image/png");

        var body = await form.ReadAsStringAsync();
        // Solo la parte del fichero declara Content-Type; los campos de texto van "pelados"
        // igual que con `curl -F`, sin el text/plain que anade StringContent.
        Assert.DoesNotContain("text/plain", body);
    }

    [Fact]
    public async Task LosParametrosSinNombreSeIgnoran()
    {
        var pars = new (string Name, string Value)[] { ("", "vacio"), ("key", "tmp/1/output.png") };
        using var form = Build(pars, new byte[] { 1 }, "output.png", "image/png");

        var body = await form.ReadAsStringAsync();
        Assert.DoesNotContain("vacio", body);
        Assert.Contains("name=\"key\"", body);
    }
}
