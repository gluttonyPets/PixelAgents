using Server.Services.Ai;
using Xunit;

namespace Server.Tests.ModuloCondicional;

/// <summary>
/// Gramatica del modulo Condicional en modo expresion. Lo importante de estos
/// tests: que una condicion entendida decida bien (Parsed = true) y que una que
/// no encaja se marque como no entendida (Parsed = false) en vez de devolver
/// "false" a secas, porque el handler usa esa señal para delegar en la IA.
/// </summary>
public class ConditionEvaluatorTests
{
    [Theory]
    // Texto contenido / no contenido.
    [InlineData("contiene \"aprobado\"", "El pedido esta aprobado", true)]
    [InlineData("contiene \"aprobado\"", "El pedido esta pendiente", false)]
    [InlineData("no contiene \"error\"", "todo correcto", true)]
    [InlineData("no contiene \"error\"", "hubo un error grave", false)]
    // Mayusculas y acentos se ignoran en las comparaciones de texto.
    [InlineData("contiene \"exito\"", "Terminado con ÉXITO", true)]
    [InlineData("contiene \"ANO\"", "año", true)]
    // Principio y final.
    [InlineData("empieza por \"OK\"", "ok: todo listo", true)]
    [InlineData("empieza por \"OK\"", "fallo: revisar", false)]
    [InlineData("termina en \".\"", "Frase final.", true)]
    // Igualdad exacta (tras recortar espacios).
    [InlineData("es igual a \"aprobado\"", " Aprobado ", true)]
    [InlineData("es igual a \"aprobado\"", "aprobado por el equipo", false)]
    [InlineData("distinto de \"no\"", "si", true)]
    // Vacio.
    [InlineData("esta vacio", "   ", true)]
    [InlineData("no esta vacio", "algo", true)]
    [InlineData("no esta vacio", "", false)]
    // Numericas sobre longitud, palabras y numero de la entrada.
    [InlineData("longitud > 5", "123456", true)]
    [InlineData("longitud > 5", "1234", false)]
    [InlineData("longitud mayor que 5", "123456", true)]
    [InlineData("longitud al menos 4", "1234", true)]
    [InlineData("palabras >= 3", "uno dos tres", true)]
    [InlineData("palabras >= 3", "uno dos", false)]
    [InlineData("numero > 10", "el total es 42 unidades", true)]
    [InlineData("numero > 10", "el total es 4 unidades", false)]
    // Expresion regular.
    [InlineData("coincide con /^[0-9]{4}$/", "2026", true)]
    [InlineData("coincide con /^[0-9]{4}$/", "20261", false)]
    // Sujeto explicito y "si" inicial: se ignoran, no cambian el significado.
    [InlineData("si el texto contiene \"hola\"", "hola mundo", true)]
    [InlineData("el resultado es igual a \"ok\"", "ok", true)]
    public void CondicionEntendida_DevuelveElResultadoEsperado(string condition, string input, bool expected)
    {
        var result = ConditionEvaluator.Evaluate(condition, input);

        Assert.True(result.Parsed, $"No se entendio la condicion: {result.Explanation}");
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    // "y": deben cumplirse todos los terminos.
    [InlineData("contiene \"ok\" y longitud > 3", "ok listo", true)]
    [InlineData("contiene \"ok\" y longitud > 30", "ok listo", false)]
    // "o": basta con uno.
    [InlineData("contiene \"ok\" o contiene \"listo\"", "todo listo", true)]
    [InlineData("contiene \"ok\" o contiene \"listo\"", "nada", false)]
    // Disyuncion de conjunciones: (A y B) o (C).
    [InlineData("contiene \"a\" y contiene \"b\" o contiene \"z\"", "solo z", true)]
    [InlineData("contiene \"a\" y contiene \"b\" o contiene \"z\"", "solo a", false)]
    public void TerminosCombinados_SeEvaluanComoDisyuncionDeConjunciones(
        string condition, string input, bool expected)
    {
        var result = ConditionEvaluator.Evaluate(condition, input);

        Assert.True(result.Parsed, $"No se entendio la condicion: {result.Explanation}");
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void SeparadorDePalabra_NoParteDentroDeUnaPalabra()
    {
        // La "y" de "yogur" no puede partir el termino en dos.
        var result = ConditionEvaluator.Evaluate("contiene \"yogur\"", "compra yogur natural");

        Assert.True(result.Parsed);
        Assert.True(result.Value);
    }

    [Theory]
    // Lenguaje natural: no encaja con la gramatica, se delega en la IA.
    [InlineData("el texto habla de gatos con tono positivo")]
    [InlineData("parece un buen articulo para publicar")]
    // Operador sin valor con el que comparar.
    [InlineData("contiene")]
    // Condicion vacia.
    [InlineData("   ")]
    public void CondicionNoEntendida_SeMarcaComoNoInterpretada(string condition)
    {
        var result = ConditionEvaluator.Evaluate(condition, "texto cualquiera");

        Assert.False(result.Parsed);
        Assert.False(string.IsNullOrWhiteSpace(result.Explanation));
    }

    [Fact]
    public void RegexInvalida_NoSeInterpretaEnVezDeReventar()
    {
        var result = ConditionEvaluator.Evaluate("coincide con /[a-/", "texto");

        Assert.False(result.Parsed);
    }

    [Fact]
    public void Explicacion_DiceQueTerminoSeCumplioYCual()
    {
        var result = ConditionEvaluator.Evaluate("contiene \"ok\" y longitud > 100", "ok");

        Assert.True(result.Parsed);
        Assert.False(result.Value);
        Assert.Contains("contiene", result.Explanation);
        Assert.Contains("longitud", result.Explanation);
    }
}
