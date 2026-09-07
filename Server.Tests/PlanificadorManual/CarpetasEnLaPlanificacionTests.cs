using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.PlanificadorManual;

/// <summary>
/// A las variables se les da valor en la ejecucion o al planificarla, asi que el
/// planificador tambien tiene que rellenar las de tipo carpeta. Se le dan las carpetas
/// que existen de verdad y se descarta lo que se invente: una carpeta inventada
/// tumbaria la ejecucion cuando llegase a la biblioteca.
/// </summary>
public class CarpetasEnLaPlanificacionTests
{
    private static readonly ProjectVariable Carpeta = new()
    {
        Key = "carpeta",
        Type = ProjectVariableTypes.Folder,
    };

    private static readonly ProjectVariable Tematica = new()
    {
        Key = "tematica",
        Type = ProjectVariableTypes.Text,
        Description = "sobre que va la ejecucion",
    };

    private static readonly Dictionary<string, List<string>> Opciones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["carpeta"] = ["legal", "manuales"],
    };

    [Fact]
    public void ElPlanificadorRecibeLasCarpetasQueExisten()
    {
        var instruccion = PlannerSchema.BuildInstruction(2, [Carpeta, Tematica], Opciones);

        Assert.Contains("\"legal\"", instruccion);
        Assert.Contains("\"manuales\"", instruccion);
        Assert.Contains("EXACTAMENTE una", instruccion);
        // La de texto sigue describiendose, no se le ofrece lista.
        Assert.Contains("sobre que va la ejecucion", instruccion);
    }

    [Fact]
    public void SinCarpetasEnLaBiblioteca_SeLePideQueLaDejeVacia()
    {
        var vacio = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["carpeta"] = [],
        };

        var instruccion = PlannerSchema.BuildInstruction(1, [Carpeta], vacio);

        Assert.Contains("no hay carpetas", instruccion);
    }

    [Fact]
    public void UnaCarpetaDeLaLista_SeGuarda()
    {
        var raw = """
        { "prompts": [ { "prompt": "Genera el post", "variables": { "carpeta": "manuales" } } ] }
        """;

        var prompts = PlannerSchema.ParsePrompts(raw, 1, [Carpeta], Opciones);

        Assert.Equal("manuales", Assert.Single(prompts).Variables["carpeta"]);
    }

    [Fact]
    public void UnaCarpetaInventada_SeDescarta()
    {
        // Mejor que la fila de la cola se vea sin carpeta (y se elija en su desplegable)
        // a que la ejecucion salga con una carpeta que la biblioteca no tiene.
        var raw = """
        { "prompts": [ { "prompt": "Genera el post", "variables": { "carpeta": "inventada" } } ] }
        """;

        var prompts = PlannerSchema.ParsePrompts(raw, 1, [Carpeta], Opciones);

        Assert.Empty(Assert.Single(prompts).Variables);
    }

    [Fact]
    public void LaCarpetaNoDistingueMayusculas()
    {
        var raw = """
        { "prompts": [ { "prompt": "Genera el post", "variables": { "carpeta": "Manuales" } } ] }
        """;

        var prompts = PlannerSchema.ParsePrompts(raw, 1, [Carpeta], Opciones);

        Assert.Equal("Manuales", Assert.Single(prompts).Variables["carpeta"]);
    }

    [Fact]
    public void SinListaDeCarpetas_NoSeFiltraNada()
    {
        // Si la biblioteca no ha podido ofrecer carpetas, no se puede juzgar el valor:
        // se guarda y el modulo dira lo que falla si no existe.
        var raw = """
        { "prompts": [ { "prompt": "Genera el post", "variables": { "carpeta": "loquesea" } } ] }
        """;

        var prompts = PlannerSchema.ParsePrompts(raw, 1, [Carpeta]);

        Assert.Equal("loquesea", Assert.Single(prompts).Variables["carpeta"]);
    }

    [Fact]
    public void LasVariablesDeTexto_NoSeFiltranPorCarpetas()
    {
        var raw = """
        { "prompts": [ { "prompt": "Genera el post", "variables": { "tematica": "gatos" } } ] }
        """;

        var prompts = PlannerSchema.ParsePrompts(raw, 1, [Carpeta, Tematica], Opciones);

        Assert.Equal("gatos", Assert.Single(prompts).Variables["tematica"]);
    }
}
