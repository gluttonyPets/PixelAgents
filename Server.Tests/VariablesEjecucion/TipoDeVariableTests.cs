using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.VariablesEjecucion;

/// <summary>
/// Tipo de una variable del pipeline. El tipo NO cambia lo que reciben los modulos
/// (el valor siempre viaja como texto): cambia como se pide ese valor al lanzar la
/// ejecucion. "text" se escribe a mano y "folder" se elige de las carpetas que la
/// biblioteca tiene de verdad, que es donde una errata cuesta una ejecucion fallida.
/// </summary>
public class TipoDeVariableTests
{
    [Fact]
    public void UnaVariableNueva_EsDeTexto()
    {
        var variable = new ProjectVariable { Key = "tematica" };

        Assert.Equal(ProjectVariableTypes.Text, variable.Type);
        Assert.Null(variable.SourceModuleId);
    }

    [Fact]
    public void ProjectVariableResponse_MantieneElTipoPorDefecto()
    {
        // Las pantallas que no envian tipo siguen creando variables de texto.
        var resp = new ProjectVariableResponse(
            Guid.NewGuid(), Guid.NewGuid(), "keyword", null, 0, DateTime.UtcNow, DateTime.UtcNow);

        Assert.Equal(ProjectVariableTypes.Text, resp.Type);
        Assert.Null(resp.SourceModuleId);
    }

    [Theory]
    [InlineData(null, "text")]
    [InlineData("", "text")]
    [InlineData("text", "text")]
    [InlineData("folder", "folder")]
    [InlineData("Folder", "folder")]
    [InlineData("  FOLDER  ", "folder")]
    public void TiposReconocidos_SeNormalizan(string? raw, string esperado)
    {
        Assert.Equal(esperado, ProjectVariableTypes.Normalize(raw));
    }

    [Theory]
    [InlineData("carpeta")]
    [InlineData("numero")]
    [InlineData("directorio")]
    public void TipoDesconocido_SeRechaza(string raw)
    {
        // null = el endpoint responde 400 en vez de guardar un tipo que nadie entiende
        // y que dejaria el desplegable sin decidir.
        Assert.Null(ProjectVariableTypes.Normalize(raw));
    }

    [Fact]
    public void SoloLasVariablesDeCarpeta_LlevanDirectorio()
    {
        Assert.True(ProjectVariableTypes.IsFolder(ProjectVariableTypes.Folder));
        Assert.False(ProjectVariableTypes.IsFolder(ProjectVariableTypes.Text));
        Assert.False(ProjectVariableTypes.IsFolder(null));
    }

    [Fact]
    public void ElValorDeUnaVariableDeCarpeta_SeSustituyeComoCualquierOtra()
    {
        // El tipo es cosa de la interfaz: al modulo le llega el mismo texto, asi que
        // el marcador de una variable de carpeta se resuelve como el de una de texto.
        var config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [FileDirectoryIndex.FolderConfigKey] = "{{carpeta}}",
        };

        var aplicada = ExecutionVariables.ApplyToConfig(
            config,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["carpeta"] = "manuales" });

        Assert.Equal("manuales", aplicada[FileDirectoryIndex.FolderConfigKey]);
    }

    [Fact]
    public void ElTipoNoCambiaLaResolucionDeValores()
    {
        // Resolve solo mira la clave y el valor de la ejecucion: una variable de
        // carpeta sin valor se queda sin sustituir, igual que una de texto.
        var definiciones = new[]
        {
            new ProjectVariable { Key = "carpeta", Type = ProjectVariableTypes.Folder },
            new ProjectVariable { Key = "tematica", Type = ProjectVariableTypes.Text },
        };

        var resuelto = ExecutionVariables.Resolve(
            definiciones,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["carpeta"] = "legal" });

        Assert.Equal("legal", resuelto["carpeta"]);
        Assert.Equal(["tematica"], ExecutionVariables.MissingKeys(definiciones, resuelto));
    }

    [Fact]
    public void LasOpcionesDeUnaVariableDeCarpeta_SonLasCarpetasDelIndice()
    {
        // Mismo origen que alimenta el desplegable: GET /api/projects/{id}/directory-folders
        // resuelve el indice de cada nodo Directorio y devuelve sus carpetas.
        var indice = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "folders": ["vacia"],
          "files": [
            { "path": "manuales/guia.pdf", "description": "Guia" },
            { "path": "legal/aviso.pdf",   "description": "Aviso" }
          ]
        }
        """;

        var opciones = new DirectoryFoldersResponse(
            Guid.NewGuid(), "Biblioteca", FileDirectoryIndex.Resolve(indice).Folders.ToList());

        // Las carpetas declaradas sin ficheros tambien se ofrecen: existen en el
        // directorio y el usuario puede querer apuntar a una recien creada.
        Assert.Equal(new[] { "legal", "manuales", "vacia" }, opciones.Folders);
    }
}
