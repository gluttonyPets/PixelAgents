using Xunit;

namespace Server.Tests.NoRepetirTemas;

/// <summary>
/// Tests de la funcion BuildExecutionSummary de GraphPipelineExecutor.
///
/// El ExecutionSummary es el texto que se guarda al finalizar cada ejecucion
/// y que se usa en ejecuciones futuras para evitar repetir temas.
/// Estos tests validan la logica de construccion del resumen replicando
/// el metodo privado estatico BuildExecutionSummary (linea 1623 de GraphPipelineExecutor.cs).
/// </summary>
public class ExecutionSummaryTests
{
    /// <summary>
    /// Replica la logica de BuildExecutionSummary.
    /// </summary>
    private static string BuildExecutionSummary(
        IEnumerable<(string StepName, string ModuleType, string? ContentOrSummary, int FileCount)> nodes,
        string? userInput)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(userInput))
            parts.Add($"Input: {Truncate(userInput, 160)}");

        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.ContentOrSummary))
                parts.Add($"{node.StepName} ({node.ModuleType}): {Truncate(node.ContentOrSummary, 160)}");
            else if (node.FileCount > 0)
                parts.Add($"{node.StepName} ({node.ModuleType}): {node.FileCount} archivo(s)");
        }

        return string.Join(" | ", parts.Take(8));
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    [Fact]
    public void BuildExecutionSummary_SoloInput_DevuelveInput()
    {
        // Arrange
        var nodes = Enumerable.Empty<(string, string, string?, int)>();

        // Act
        var result = BuildExecutionSummary(nodes, "Post sobre verano");

        // Assert
        Assert.Equal("Input: Post sobre verano", result);
    }

    [Fact]
    public void BuildExecutionSummary_ConModulos_IncludeModulos()
    {
        // Arrange
        var nodes = new[]
        {
            ("Generador de texto", "Text", "Post sobre viajes en verano 2026", 0),
        };

        // Act
        var result = BuildExecutionSummary(nodes, "verano");

        // Assert
        Assert.Contains("Generador de texto (Text)", result);
        Assert.Contains("Post sobre viajes en verano 2026", result);
    }

    [Fact]
    public void BuildExecutionSummary_TomaMaximo8Partes()
    {
        // Arrange: 10 nodos con contenido
        var nodes = Enumerable.Range(1, 10)
            .Select(i => ($"Nodo{i}", "Text", $"Contenido del nodo {i}", 0))
            .ToArray();

        // Act
        var result = BuildExecutionSummary(nodes, "input");

        // Assert: maximo 8 partes (1 input + 7 nodos)
        var parts = result.Split(" | ");
        Assert.True(parts.Length <= 8, $"Se esperaban maximo 8 partes, se obtuvieron {parts.Length}");
    }

    [Fact]
    public void BuildExecutionSummary_TextoLargo_SeTrunca()
    {
        // Arrange: texto de mas de 160 caracteres
        var textoLargo = new string('a', 200);
        var nodes = new[]
        {
            ("Generador", "Text", textoLargo, 0),
        };

        // Act
        var result = BuildExecutionSummary(nodes, null);

        // Assert: el texto debe estar truncado a 160 chars + "..."
        Assert.Contains("...", result);
        // La parte del nodo no debe superar 160 chars + sufijo
        var nodeSection = result.Replace("Generador (Text): ", "");
        Assert.Equal(163, nodeSection.Length); // 160 + "..."
    }

    [Fact]
    public void BuildExecutionSummary_NodoConArchivos_MuestraConteoArchivos()
    {
        // Arrange: nodo sin texto pero con archivos (ej: imagen generada)
        var nodes = new[]
        {
            ("Generador de imagen", "Image", null, 3),
        };

        // Act
        var result = BuildExecutionSummary(nodes, null);

        // Assert: debe mostrar el conteo de archivos
        Assert.Contains("3 archivo(s)", result);
    }

    [Fact]
    public void BuildExecutionSummary_NodoSinContenidoNiArchivos_SeOmite()
    {
        // Arrange: nodo sin output
        var nodes = new[]
        {
            ("Nodo vacio", "Text", null, 0),
        };

        // Act
        var result = BuildExecutionSummary(nodes, null);

        // Assert: el nodo sin contenido no debe aparecer en el resumen
        Assert.DoesNotContain("Nodo vacio", result);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildExecutionSummary_InputNull_NoAparece()
    {
        // Arrange
        var nodes = new[]
        {
            ("Generador", "Text", "Contenido", 0),
        };

        // Act
        var result = BuildExecutionSummary(nodes, null);

        // Assert: no debe haber "Input:" si el input es null
        Assert.DoesNotContain("Input:", result);
        Assert.Contains("Generador (Text): Contenido", result);
    }

    [Fact]
    public void BuildExecutionSummary_InputVacio_NoAparece()
    {
        // Arrange
        var nodes = Enumerable.Empty<(string, string, string?, int)>();

        // Act: input vacio (whitespace)
        var result = BuildExecutionSummary(nodes, "   ");

        // Assert
        Assert.DoesNotContain("Input:", result);
    }

    [Fact]
    public void BuildExecutionSummary_FormatoSeparadorCorrecto()
    {
        // Arrange: multiples nodos para verificar el separador " | "
        var nodes = new[]
        {
            ("Nodo1", "Text", "Contenido1", 0),
            ("Nodo2", "Text", "Contenido2", 0),
        };

        // Act
        var result = BuildExecutionSummary(nodes, "mi input");

        // Assert: el separador debe ser " | "
        var parts = result.Split(" | ");
        Assert.Equal(3, parts.Length); // Input + Nodo1 + Nodo2
        Assert.StartsWith("Input: mi input", parts[0]);
    }
}
