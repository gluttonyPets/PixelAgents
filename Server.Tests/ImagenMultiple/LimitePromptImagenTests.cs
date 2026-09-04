using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ImagenMultiple;

/// <summary>
/// Limite de prompt por modelo. Vive en el catalogo (lo que se ve en la pantalla
/// de modelos) y es contra lo que se recorta antes de llamar. Estaba fijado a
/// 4.000 para toda la familia gpt-image, que es el limite de DALL-E 3: recortaba
/// prompts validos por la mitad sin motivo.
/// </summary>
public class LimitePromptImagenTests
{
    [Theory]
    [InlineData("gpt-image-2", 32_000)]
    [InlineData("gpt-image-1.5", 32_000)]
    [InlineData("gpt-image-1", 32_000)]
    [InlineData("gpt-image-1-mini", 32_000)]
    [InlineData("dall-e-3", 4_000)]
    [InlineData("dall-e-2", 1_000)]
    [InlineData("leonardo-phoenix", 1_500)]
    public void ElLimiteSaleDelCatalogo(string modelId, int expected)
    {
        Assert.Equal(expected, ModelCatalog.Find(modelId)?.PromptChars);
        Assert.Equal(expected, InputAdapter.GetMaxPromptLength(modelId));
    }

    [Fact]
    public void TodoModeloDeImagenDelCatalogoDeclaraSuLimite()
    {
        // Si se da de alta uno nuevo sin limite, cae en el fallback por familia
        // y nadie se entera hasta que un prompt sale recortado a medias.
        var sinLimite = ModelCatalog.GetByModuleType("Image")
            .Where(m => m.PromptChars is null or <= 0)
            .Select(m => m.Id)
            .ToList();

        Assert.Empty(sinLimite);
    }

    [Fact]
    public void UnIdConSufijoDeSnapshot_HeredaElLimiteDeSuModelo()
    {
        Assert.Equal(32_000, InputAdapter.GetMaxPromptLength("gpt-image-1-2025-04-23"));
    }

    [Fact]
    public void UnModeloDesconocido_CaeEnElLimitePorDefecto()
    {
        Assert.Equal(4_000, InputAdapter.GetMaxPromptLength("modelo-que-no-existe"));
        Assert.Equal(4_000, InputAdapter.GetMaxPromptLength(null));
    }

    [Fact]
    public void ElAvisoDeRecorte_SugiereModelosConMasCapacidadYSuLimite()
    {
        var aviso = InputAdapter.BuildTruncationWarning("dall-e-3", 6_000, 4_000);

        Assert.Contains("GPT Image", aviso);
        Assert.Contains("32,000", aviso.Replace(".", ","));
        Assert.DoesNotContain("DALL-E 3 (OpenAI)", aviso);
    }

    [Fact]
    public void ElAvisoDelModeloMasCapaz_NoSugiereNada()
    {
        var aviso = InputAdapter.BuildTruncationWarning("gpt-image-2", 40_000, 32_000);

        Assert.Contains("No hay modelos de imagen con mayor", aviso);
    }
}
