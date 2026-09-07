using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.DirectorioArchivos;

/// <summary>
/// La biblioteca entrega la carpeta que trae la variable de esa ejecucion. La variable
/// declara de que biblioteca se elige; el valor lo pone siempre la ejecucion (o la
/// planificacion), como el de cualquier otra variable. Lo escrito en el nodo manda
/// sobre la variable, y una variable de carpeta sin valor no publica el directorio
/// entero: para la ejecucion.
/// </summary>
public class CarpetaDesdeVariableTests
{
    private static readonly Guid Biblioteca = Guid.NewGuid();
    private static readonly Guid OtraBiblioteca = Guid.NewGuid();

    // ── El valor viene de la ejecucion, no de la definicion ──

    [Fact]
    public void SinValorEnLaEjecucion_LaVariableNoAportaCarpeta()
    {
        var definiciones = new[] { CarpetaVar("carpeta") };

        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Empty(valores);
        Assert.Empty(VariableFolders.ForModule(definiciones, valores, Biblioteca));
    }

    [Fact]
    public void LaEjecucionElijeLaCarpeta()
    {
        var definiciones = new[] { CarpetaVar("carpeta") };

        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "legal")));

        Assert.Equal(["legal"], VariableFolders.ForModule(definiciones, valores, Biblioteca));
    }

    [Fact]
    public void UnaVariableDeCarpetaNoTieneValorDeRespaldo()
    {
        // Igual que el resto: la definicion dice de donde se elige, no que se elige.
        var definiciones = new[] { CarpetaVar("carpeta") };

        var valores = ExecutionVariables.Resolve(definiciones, ExecutionVariables.None);

        Assert.Equal(["carpeta"], ExecutionVariables.MissingKeys(definiciones, valores));
    }

    // ── Que variables mandan sobre cada nodo Directorio ──

    [Fact]
    public void LaVariableRecortaElDirectorioSinTocarSuConfiguracion()
    {
        var definiciones = new[] { CarpetaVar("carpeta") };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, Biblioteca));
    }

    [Fact]
    public void UnaVariableAtadaAOtraBiblioteca_NoRecortaEsta()
    {
        // Su carpeta no tiene por que existir en este directorio: aplicarla lo dejaria
        // vacio (o fallando) por una eleccion que no era suya.
        var definiciones = new[] { CarpetaVar("carpeta", source: OtraBiblioteca) };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

        Assert.Empty(VariableFolders.ForModule(definiciones, valores, Biblioteca));
        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, OtraBiblioteca));
    }

    [Fact]
    public void UnaVariableSinBiblioteca_AplicaATodosLosDirectorios()
    {
        var definiciones = new[] { CarpetaVar("carpeta") };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, Biblioteca));
        Assert.Equal(["manuales"], VariableFolders.ForModule(definiciones, valores, OtraBiblioteca));
    }

    [Fact]
    public void VariasVariablesDeCarpeta_SumanSusCarpetas()
    {
        var definiciones = new[]
        {
            CarpetaVar("carpeta"),
            CarpetaVar("extra", source: Biblioteca),
        };
        var valores = ExecutionVariables.Resolve(
            definiciones, Valores(("carpeta", "manuales"), ("extra", "legal")));

        Assert.Equal(new[] { "legal", "manuales" },
            VariableFolders.ForModule(definiciones, valores, Biblioteca).OrderBy(f => f));
    }

    [Fact]
    public void LasVariablesDeTexto_NoRecortanNada()
    {
        var definiciones = new[] { new ProjectVariable { Key = "tematica", Type = ProjectVariableTypes.Text } };
        var valores = Valores(("tematica", "gatos"));

        Assert.Empty(VariableFolders.ForModule(definiciones, valores, Biblioteca));
        Assert.Empty(VariableFolders.Applicable(definiciones, Biblioteca));
    }

    [Fact]
    public void HayVariableDeCarpetaAunqueLaEjecucionNoLaRellene()
    {
        // Applicable no mira valores: es lo que permite al modulo distinguir "no hay
        // variable" (publica todo) de "hay variable sin valor" (para y avisa).
        var definiciones = new[] { CarpetaVar("carpeta") };

        Assert.Single(VariableFolders.Applicable(definiciones, Biblioteca));
        Assert.Empty(VariableFolders.ForModule(definiciones, ExecutionVariables.None, Biblioteca));
    }

    [Fact]
    public void ElLogDiceDeQueVariableSaleLaCarpeta()
    {
        var definiciones = new[] { CarpetaVar("carpeta") };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

        Assert.Equal(["carpeta"], VariableFolders.KeysForModule(definiciones, valores, Biblioteca));
    }

    // ── Precedencia y efecto sobre el indice ──

    [Fact]
    public void LaCarpetaEscritaEnElNodo_GanaALaVariable()
    {
        // Misma precedencia que aplica el handler: solo se miran las variables cuando
        // el nodo no trae carpeta escrita.
        var definiciones = new[] { CarpetaVar("carpeta") };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

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
        var definiciones = new[] { CarpetaVar("carpeta") };
        var valores = ExecutionVariables.Resolve(definiciones, Valores(("carpeta", "manuales")));

        var result = FileDirectoryIndex.Resolve(
            Indice,
            folderSelection: VariableFolders.ForModule(definiciones, valores, Biblioteca));

        var entrada = Assert.Single(result.Entries);
        Assert.Equal("manuales/guia.pdf", entrada.Path);
    }

    // ── Carpetas que se ofrecen para elegir ──

    [Fact]
    public void LasCarpetasDelIndiceSeLeenSinResolverUrls()
    {
        // El desplegable y el planificador solo necesitan saber que carpetas hay: un
        // indice con entradas sin ruta accesible seguiria ofreciendolas.
        var carpetas = FileDirectoryIndex.ReadFolders("""
        {
          "folders": ["vacia"],
          "files": [
            { "path": "manuales/guia.pdf", "description": "Guia" },
            { "path": "legal/roto.pdf" }
          ]
        }
        """);

        Assert.Equal(new[] { "legal", "manuales", "vacia" }, carpetas);
    }

    [Fact]
    public void UnIndiceIlegibleNoOfreceCarpetas()
    {
        Assert.Empty(FileDirectoryIndex.ReadFolders("{ esto no es json "));
        Assert.Empty(FileDirectoryIndex.ReadFolders(null));
    }

    private const string Indice = """
    {
      "baseUrl": "https://cdn.ejemplo.com/marca",
      "files": [
        { "path": "manuales/guia.pdf", "description": "Guia" },
        { "path": "legal/aviso.pdf",   "description": "Aviso" }
      ]
    }
    """;

    private static ProjectVariable CarpetaVar(string key, Guid? source = null) => new()
    {
        Id = Guid.NewGuid(),
        Key = key,
        Type = ProjectVariableTypes.Folder,
        SourceModuleId = source,
    };

    private static Dictionary<string, string> Valores(params (string Key, string Value)[] pares) =>
        pares.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}
