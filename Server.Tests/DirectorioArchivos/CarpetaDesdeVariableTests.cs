using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// La carpeta se elige al declarar la variable y la biblioteca la aplica sola: si el
/// pipeline tiene una variable de tipo carpeta, el nodo Directorio entrega solo esa
/// carpeta sin que haya que escribir el marcador en su configuracion. Lo escrito en el
/// nodo manda sobre la variable, y cada ejecucion puede cambiar la carpeta.
/// </summary>
public class CarpetaDesdeVariableTests
{
    private static readonly Guid Biblioteca = Guid.NewGuid();
    private static readonly Guid OtraBiblioteca = Guid.NewGuid();

    // ── La carpeta configurada es lo que corre si la ejecucion no dice otra cosa ──

    [Fact]
    public void SinValorEnLaEjecucion_ValeLaCarpetaElegidaAlDeclararla()
    {
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };

        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Equal("manuales", valores["carpeta"]);
    }

    [Fact]
    public void LaEjecucionPuedeCambiarLaCarpeta()
    {
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };

        var valores = ExecutionVariables.Resolve(
            definiciones,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["carpeta"] = "legal" });

        Assert.Equal("legal", valores["carpeta"]);
    }

    [Fact]
    public void UnaVariableDeTextoNoTieneValorDeRespaldo()
    {
        // El resto de variables sigue sin valor fijo: si la ejecucion no lo trae, no
        // se sustituye. La carpeta es la excepcion porque no es contenido del prompt.
        var definiciones = new[]
        {
            new ProjectVariable { Key = "tematica", Type = ProjectVariableTypes.Text, FolderPath = "manuales" },
        };

        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Empty(valores);
    }

    [Fact]
    public void UnaVariableDeCarpetaSinCarpeta_SigueSinValor()
    {
        var definiciones = new[] { CarpetaVar("carpeta", folder: null) };

        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Empty(valores);
        Assert.Equal(["carpeta"], ExecutionVariables.MissingKeys(definiciones, valores));
    }

    // ── Que carpetas aplican a cada nodo Directorio ──

    [Fact]
    public void LaVariableRecortaElDirectorioSinTocarSuConfiguracion()
    {
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        var carpetas = VariableFolders.ForModule(definiciones, valores, Biblioteca);

        Assert.Equal(["manuales"], carpetas);
    }

    [Fact]
    public void UnaVariableAtadaAOtraBiblioteca_NoRecortaEsta()
    {
        // Su carpeta no tiene por que existir en este directorio: aplicarla dejaria el
        // nodo vacio (o fallando) por una eleccion que no era suya.
        var definiciones = new[] { CarpetaVar("carpeta", "manuales", source: OtraBiblioteca) };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Empty(VariableFolders.ForModule(definiciones, valores, Biblioteca));
        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, OtraBiblioteca));
    }

    [Fact]
    public void UnaVariableSinBiblioteca_AplicaATodosLosDirectorios()
    {
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, Biblioteca));
        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, OtraBiblioteca));
    }

    [Fact]
    public void VariasVariablesDeCarpeta_SumanSusCarpetas()
    {
        var definiciones = new[]
        {
            CarpetaVar("carpeta", "manuales"),
            CarpetaVar("extra", "legal", source: Biblioteca),
        };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Equal(new[] { "legal", "manuales" },
            VariableFolders.ForModule(definiciones, valores, Biblioteca).OrderBy(f => f));
    }

    [Fact]
    public void LasVariablesDeTexto_NoRecortanNada()
    {
        var definiciones = new[]
        {
            new ProjectVariable { Key = "tematica", Type = ProjectVariableTypes.Text },
        };
        var valores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tematica"] = "gatos",
        };

        Assert.Empty(VariableFolders.ForModule(definiciones, valores, Biblioteca));
    }

    [Fact]
    public void ElLogDiceDeQueVariableSaleLaCarpeta()
    {
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Equal(["carpeta"], VariableFolders.KeysForModule(definiciones, valores, Biblioteca));
    }

    // ── Lo escrito en el nodo manda ──

    [Fact]
    public void LaCarpetaEscritaEnElNodo_GanaALaVariable()
    {
        // Misma precedencia que aplica el handler: solo se miran las variables cuando
        // el nodo no trae carpeta escrita.
        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        var delNodo = FileDirectoryIndex.ParseFolderSelection("legal");
        var efectiva = delNodo.Count > 0
            ? delNodo
            : VariableFolders.ForModule(definiciones, valores, Biblioteca);

        Assert.Equal(["legal"], efectiva);
    }

    [Fact]
    public void SinVariablesNiCarpetaEnElNodo_SePublicaTodo()
    {
        var efectiva = FileDirectoryIndex.ParseFolderSelection("");
        if (efectiva.Count == 0)
            efectiva = VariableFolders.ForModule([], ExecutionVariables.None, Biblioteca);

        Assert.Empty(efectiva);
    }

    [Fact]
    public void LaCarpetaDeLaVariableRecortaElIndiceIgualQueLaEscrita()
    {
        var indice = """
        {
          "baseUrl": "https://cdn.ejemplo.com/marca",
          "files": [
            { "path": "manuales/guia.pdf", "description": "Guia" },
            { "path": "legal/aviso.pdf",   "description": "Aviso" }
          ]
        }
        """;

        var definiciones = new[] { CarpetaVar("carpeta", "manuales") };
        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        var result = FileDirectoryIndex.Resolve(
            indice,
            folderSelection: VariableFolders.ForModule(definiciones, valores, Biblioteca));

        var entrada = Assert.Single(result.Entries);
        Assert.Equal("manuales/guia.pdf", entrada.Path);
    }

    private static ProjectVariable CarpetaVar(string key, string? folder, Guid? source = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Type = ProjectVariableTypes.Folder,
        FolderPath = folder,
        SourceModuleId = source,
    };
}
