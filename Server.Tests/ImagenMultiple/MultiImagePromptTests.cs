using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ImagenMultiple;

/// <summary>
/// Reparto del prompt en varias imagenes: que se reconozcan las marcas que
/// escribe el planificador, que el texto sin marcas no se parta por su cuenta y
/// que el numero de imagenes se lea de las tres claves que ha usado el editor.
/// </summary>
public class MultiImagePromptTests
{
    [Fact]
    public void TextoConMarcas_SeRepartePorPartes()
    {
        var texto = """
            Estilo comun: fotografia luminosa.

            ===IMAGEN 1===
            Perro en el sofa lleno de pelos.

            ===IMAGEN 2===
            El mismo sofa limpio con el cepillo al lado.
            """;

        var result = MultiImagePrompt.Split([texto]);

        Assert.Equal(2, result.Segments.Count);
        Assert.Contains("Perro en el sofa", result.Segments[0]);
        Assert.DoesNotContain("sofa limpio", result.Segments[0]);
        Assert.Contains("sofa limpio", result.Segments[1]);
        Assert.Equal("Estilo comun: fotografia luminosa.", result.Common);
    }

    [Theory]
    // Variantes que devuelven los modelos cuando se les pide el separador.
    [InlineData("===IMAGEN 1===\nuno\n===IMAGEN 2===\ndos")]
    [InlineData("IMAGEN 1: uno\nIMAGEN 2: dos")]
    [InlineData("--- Imagen 1 ---\nuno\n--- Imagen 2 ---\ndos")]
    [InlineData("ESCENA 1\nuno\nESCENA 2\ndos")]
    [InlineData("SLIDE 1:\nuno\n\nSLIDE 2:\ndos")]
    public void LasMarcasSeReconocenEnSusFormatosHabituales(string texto)
    {
        var result = MultiImagePrompt.Split([texto]);

        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("uno", result.Segments[0]);
        Assert.Equal("dos", result.Segments[1]);
    }

    [Fact]
    public void TextoSinMarcas_NoSeParte()
    {
        var texto = "Un cartel con la parte de arriba en negro y la de abajo en blanco.";

        var result = MultiImagePrompt.Split([texto]);

        Assert.Empty(result.Segments);
        Assert.Equal(texto, result.Common);
    }

    [Fact]
    public void UnaSolaMarca_NoSeParte()
    {
        var result = MultiImagePrompt.Split(["===IMAGEN 1===\nsolo una cosa"]);

        Assert.Empty(result.Segments);
    }

    [Fact]
    public void MarcasDesordenadas_NoSeInterpretanComoReparto()
    {
        // "Imagen 3" sin la 2 delante no abre una parte: seria partir el prompt
        // por una frase cualquiera que empiece por esa palabra.
        var result = MultiImagePrompt.Split(["===IMAGEN 1===\nuno\n===IMAGEN 3===\ntres"]);

        Assert.Empty(result.Segments);
    }

    [Fact]
    public void MencionAImagenDentroDeUnaFrase_NoAbreParte()
    {
        var texto = "Cartel unico. La imagen 1 de referencia sirve de guia de color.";

        var result = MultiImagePrompt.Split([texto]);

        Assert.Empty(result.Segments);
    }

    [Fact]
    public void ElTextoSinMarcas_DeOtraConexion_QuedaComoContextoComun()
    {
        // Caso real: el indice del modulo Directorio entra por el mismo puerto
        // que el texto del planificador. No es una escena: acompaña a todas.
        var indice = "Biblioteca: /files/cepillo.png (cepillo verde)";
        var plan = "===IMAGEN 1===\nprimera\n===IMAGEN 2===\nsegunda";

        var result = MultiImagePrompt.Split([indice, plan]);

        Assert.Equal(2, result.Segments.Count);
        Assert.Contains("Biblioteca", result.Common);
    }

    [Fact]
    public void ParteVacia_InvalidaElReparto()
    {
        var result = MultiImagePrompt.Split(["===IMAGEN 1===\n\n===IMAGEN 2===\ndos"]);

        Assert.Empty(result.Segments);
    }

    [Theory]
    [InlineData("n", 3, 3)]
    [InlineData("numberOfImages", 2, 2)]
    [InlineData("sceneCount", 4, 4)]
    // Los numeros los guarda el editor unas veces como numero y otras como cadena.
    [InlineData("n", "2", 2)]
    [InlineData("n", 0, 1)]
    [InlineData("n", 99, MultiImagePrompt.MaxImages)]
    public void ElNumeroDeImagenesSeLeeDeCualquieraDeLasClaves(string key, object value, int expected)
    {
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { [key] = value };

        Assert.Equal(expected, MultiImagePrompt.ReadImageCount(config));
    }

    [Fact]
    public void SinConfiguracion_EsUnaImagen()
    {
        Assert.Equal(1, MultiImagePrompt.ReadImageCount(new Dictionary<string, object>()));
        Assert.Equal(1, MultiImagePrompt.ReadImageCount(null));
    }

    [Fact]
    public void ElNumeroDeImagenesSeLeeDelJsonDelModulo()
    {
        // La config del paso pisa a la del catalogo, como en el executor.
        var count = MultiImagePrompt.ReadImageCountFromJson("{\"n\":1}", "{\"n\":2}");

        Assert.Equal(2, count);
    }

    [Fact]
    public void JsonMalformado_NoRompe()
    {
        Assert.Equal(1, MultiImagePrompt.ReadImageCountFromJson("{no es json", null));
    }

    [Fact]
    public void LaInstruccionAlPlanificador_PideUnPromptPorImagen()
    {
        var instruccion = MultiImagePrompt.BuildPlannerInstruction(2);

        Assert.Contains("2", instruccion);
        Assert.Contains(MultiImagePrompt.BuildMarker(1), instruccion);
        Assert.Contains(MultiImagePrompt.BuildMarker(2), instruccion);

        // Y lo que escriba tiene que volver a repartirse igual.
        var texto = instruccion.Replace($"(prompt completo y autocontenido de la imagen 1)", "uno")
                               .Replace($"(prompt completo y autocontenido de la imagen 2)", "dos");
        Assert.Equal(2, MultiImagePrompt.Split([texto]).Segments.Count);
    }
}
