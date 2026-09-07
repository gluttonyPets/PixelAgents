using System.Text.Json;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// Elegir que documentos entran en cada ejecucion: el nodo Directorio admite una
/// carpeta (<c>folder</c>) que puede escribirse a mano o salir de una variable del
/// pipeline. Sin carpeta se publica el directorio entero; con carpeta, solo esa y
/// sus subcarpetas. Una carpeta que no existe es un error, no un filtro que no
/// filtra: publicar la biblioteca entera cuando se pidio una parte seria peor.
/// </summary>
public class CarpetaPorEjecucionTests
{
    private const string Indice = """
    {
      "baseUrl": "https://cdn.ejemplo.com/marca",
      "folders": ["manuales", "manuales/2024", "legal", "vacia"],
      "files": [
        { "path": "manuales/guia.pdf",        "description": "Guia general" },
        { "path": "manuales/2024/tarifas.pdf","description": "Tarifas 2024" },
        { "path": "legal/condiciones.pdf",    "description": "Condiciones" },
        { "path": "raiz.txt",                 "description": "Fichero en la raiz" }
      ]
    }
    """;

    // ── Lectura de la carpeta configurada ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void SinCarpeta_SePublicaTodoElDirectorio(string? raw)
    {
        Assert.Empty(FileDirectoryIndex.ParseFolderSelection(raw));

        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: FileDirectoryIndex.ParseFolderSelection(raw));

        Assert.True(result.IsValid);
        Assert.Equal(4, result.Entries.Count);
        Assert.Empty(result.SelectedFolders);
    }

    [Theory]
    [InlineData("manuales", "manuales")]
    [InlineData("  /manuales/  ", "manuales")]
    [InlineData("manuales\\2024", "manuales/2024")]
    public void LaCarpetaSeNormalizaComoLasRutasDelIndice(string raw, string esperada)
    {
        Assert.Equal([esperada], FileDirectoryIndex.ParseFolderSelection(raw));
    }

    [Fact]
    public void SePuedenElegirVariasCarpetas()
    {
        var seleccion = FileDirectoryIndex.ParseFolderSelection("manuales, legal");

        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: seleccion);

        Assert.True(result.IsValid);
        Assert.Equal(
            new[] { "legal/condiciones.pdf", "manuales/2024/tarifas.pdf", "manuales/guia.pdf" },
            result.Entries.Select(e => e.Path).OrderBy(p => p));
    }

    [Fact]
    public void UnaCarpetaQueSaleDelDirectorio_SeDescarta()
    {
        Assert.Empty(FileDirectoryIndex.ParseFolderSelection("../otro"));
    }

    // ── Recorte del indice ──

    [Fact]
    public void ConCarpeta_SoloSalenSusFicherosYLosDeSusSubcarpetas()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["manuales"]);

        Assert.True(result.IsValid);
        Assert.Equal(
            new[] { "manuales/2024/tarifas.pdf", "manuales/guia.pdf" },
            result.Entries.Select(e => e.Path).OrderBy(p => p));
        Assert.Equal(["manuales"], result.SelectedFolders);
    }

    [Fact]
    public void ConSubcarpeta_NoSeCuelaLoDeLaCarpetaPadre()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["manuales/2024"]);

        Assert.True(result.IsValid);
        var entrada = Assert.Single(result.Entries);
        Assert.Equal("manuales/2024/tarifas.pdf", entrada.Path);
    }

    [Fact]
    public void LasCarpetasDelResultado_TambienSeRecortan()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["manuales"]);

        // "vacia" y "legal" quedan fuera del alcance de esta ejecucion.
        Assert.Equal(new[] { "manuales", "manuales/2024" }, result.Folders);
    }

    [Fact]
    public void CarpetaQueNoExiste_FallaEnVezDePublicarTodo()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["inventada"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("inventada"));
        // El error lista lo que si hay, para que se vea el nombre correcto.
        Assert.Contains(result.Errors, e => e.Contains("manuales"));
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void CarpetaDeclaradaPeroVacia_LoDiceEnVezDeQuedarseSinIndice()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["vacia"]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("ningun fichero", StringComparison.OrdinalIgnoreCase));
    }

    // ── Salida ──

    [Fact]
    public void ElIndiceRenderizado_AvisaDelAlcanceDeLaEjecucion()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["manuales"]);

        var render = FileDirectoryIndex.Render(result, "markdown");

        // Sin este aviso el modelo cree tener delante la biblioteca completa.
        Assert.Contains("manuales", render);
        Assert.Contains("no esta disponible", render);
        Assert.DoesNotContain("condiciones.pdf", render);
    }

    [Fact]
    public void ElIndiceEnJson_LlevaLasCarpetasElegidas()
    {
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: ["legal"]);

        using var doc = JsonDocument.Parse(FileDirectoryIndex.Render(result, "json"));

        Assert.Equal("legal", doc.RootElement.GetProperty("selectedFolders")[0].GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public void SinCarpeta_ElJsonNoInventaAlcance()
    {
        var result = FileDirectoryIndex.Resolve(Indice);

        using var doc = JsonDocument.Parse(FileDirectoryIndex.Render(result, "json"));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("selectedFolders").ValueKind);
    }

    // ── La carpeta puede venir de una variable ──

    [Fact]
    public void LaVariableDecideLaCarpetaDeCadaEjecucion()
    {
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [FileDirectoryIndex.FolderConfigKey] = "{{carpeta}}",
        };

        // Misma sustitucion que aplica el executor antes de llamar al handler.
        var aplicada = ExecutionVariables.ApplyToConfig(
            config,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["carpeta"] = "legal" });

        var seleccion = FileDirectoryIndex.ParseFolderSelection(
            aplicada[FileDirectoryIndex.FolderConfigKey]?.ToString());
        var result = FileDirectoryIndex.Resolve(Indice, folderSelection: seleccion);

        var entrada = Assert.Single(result.Entries);
        Assert.Equal("legal/condiciones.pdf", entrada.Path);
    }

    [Fact]
    public void VariableSinValor_SeDetectaAntesDeResolverElIndice()
    {
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [FileDirectoryIndex.FolderConfigKey] = "{{carpeta}}",
        };

        // Ejecucion que no da valor a la variable: el marcador llega intacto.
        var aplicada = ExecutionVariables.ApplyToConfig(config, ExecutionVariables.None);
        var valor = aplicada[FileDirectoryIndex.FolderConfigKey]?.ToString();

        // El handler para aqui: buscar una carpeta llamada "{{carpeta}}" no existiria,
        // y publicar el directorio entero seria darle documentos que nadie eligio.
        Assert.Equal(["carpeta"], ExecutionVariables.UnresolvedKeys(valor));
    }

    [Fact]
    public void UnValorNormalNoSeConfundeConUnMarcador()
    {
        Assert.Empty(ExecutionVariables.UnresolvedKeys("manuales/2024"));
        Assert.Empty(ExecutionVariables.UnresolvedKeys(null));
    }

    [Fact]
    public void ElFiltroNoTocaLasUrlPublicas_SoloLoQueVeElModulo()
    {
        // El filtro elige que ve el modulo, no quien puede descargar un fichero: la
        // URL de una entrada es la misma se publique el directorio entero o una carpeta.
        var completo = FileDirectoryIndex.Resolve(Indice);
        var recortado = FileDirectoryIndex.Resolve(Indice, folderSelection: ["legal"]);

        var esperada = completo.Entries.Single(e => e.Path == "legal/condiciones.pdf").Url;
        Assert.Equal(esperada, recortado.Entries.Single().Url);
    }
}
