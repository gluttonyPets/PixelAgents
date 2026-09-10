using Server.Models;
using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ReglasExcepciones;

/// <summary>
/// Una excepcion se engancha al modulo del CATALOGO (AiModule), no al nodo: saca
/// a ese modulo de la regla en todos los pipelines que lo usen. Aqui se comprueba
/// lo que le queda a cada modulo.
/// </summary>
public class ReglasPorModuloTests
{
    private static Rule Regla(Guid id, string titulo) =>
        new() { Id = id, Title = titulo, Content = $"contenido de {titulo}", IsActive = true };

    private static ExecutionGraph Grafo(
        IReadOnlyList<Rule> reglas,
        Dictionary<Guid, IReadOnlySet<string>>? excepciones = null)
    {
        var graph = new ExecutionGraph
        {
            TenantRules = reglas,
            MandatoryRules = ModuleRules.BuildBlock(reglas),
        };
        if (excepciones is not null) graph.RuleExceptionsByModule = excepciones;
        return graph;
    }

    [Fact]
    public void SinExcepciones_ElModuloRecibeTodasLasReglasPropias()
    {
        var grafo = Grafo([Regla(Guid.NewGuid(), "Idioma"), Regla(Guid.NewGuid(), "Longitud")]);

        var bloque = ModuleRules.MandatoryRulesFor(grafo, Guid.NewGuid());

        Assert.Contains("Idioma", bloque);
        Assert.Contains("Longitud", bloque);
    }

    [Fact]
    public void ConExcepcion_ElModuloDejaDeRecibirEsaReglaYSoloEsa()
    {
        var idioma = Guid.NewGuid();
        var modulo = Guid.NewGuid();
        var grafo = Grafo(
            [Regla(idioma, "Idioma"), Regla(Guid.NewGuid(), "Longitud")],
            new() { [modulo] = new HashSet<string> { BuiltInRules.TenantKey(idioma) } });

        var conExcepcion = ModuleRules.MandatoryRulesFor(grafo, modulo);
        var otroModulo = ModuleRules.MandatoryRulesFor(grafo, Guid.NewGuid());

        Assert.DoesNotContain("Idioma", conExcepcion);
        Assert.Contains("Longitud", conExcepcion);
        Assert.Contains("Idioma", otroModulo);
    }

    [Fact]
    public void ConTodasExcepcionadas_NoSeMandaNiElEncabezado()
    {
        var idioma = Guid.NewGuid();
        var modulo = Guid.NewGuid();
        var grafo = Grafo(
            [Regla(idioma, "Idioma")],
            new() { [modulo] = new HashSet<string> { BuiltInRules.TenantKey(idioma) } });

        Assert.Null(ModuleRules.MandatoryRulesFor(grafo, modulo));
    }

    [Fact]
    public void LaExcepcionDeUnaConstante_NoTocaLasReglasPropias()
    {
        var modulo = Guid.NewGuid();
        var grafo = Grafo(
            [Regla(Guid.NewGuid(), "Idioma")],
            new() { [modulo] = new HashSet<string> { BuiltInRules.FormatKey } });

        Assert.Contains("Idioma", ModuleRules.MandatoryRulesFor(grafo, modulo));
        Assert.Contains(BuiltInRules.FormatKey, ModuleRules.SuppressedFor(grafo, modulo));
    }

    [Fact]
    public void SinReglasPropias_NoHayBloque()
    {
        Assert.Null(ModuleRules.BuildBlock([]));
        Assert.Null(ModuleRules.MandatoryRulesFor(Grafo([]), Guid.NewGuid()));
    }

    [Fact]
    public void SuppressedFor_ModuloSinExcepciones_DevuelveVacio()
    {
        Assert.Empty(ModuleRules.SuppressedFor(Grafo([]), Guid.NewGuid()));
    }
}
