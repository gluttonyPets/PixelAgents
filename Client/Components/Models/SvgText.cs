using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Client.Components.Models;

/// <summary>
/// Un &lt;text&gt; de SVG.
///
/// Existe porque Razor se reserva la etiqueta <c>&lt;text&gt;</c> como marca para
/// volver a HTML dentro de un bloque de codigo, asi que escribirla a mano en un
/// <c>@foreach</c> no compila ("tags cannot contain attributes"). Montar el elemento
/// desde el arbol de render la deja pasar tal cual.
/// </summary>
public class SvgText : ComponentBase
{
    [Parameter] public double X { get; set; }
    [Parameter] public double Y { get; set; }

    /// <summary>Clase CSS; el estilo real vive en app.css, como el del resto de graficas.</summary>
    [Parameter] public string? Class { get; set; }

    /// <summary>"start", "middle" o "end".</summary>
    [Parameter] public string Anchor { get; set; } = "start";

    [Parameter] public string? Text { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "text");
        builder.AddAttribute(1, "x", Fmt(X));
        builder.AddAttribute(2, "y", Fmt(Y));
        builder.AddAttribute(3, "text-anchor", Anchor);

        if (!string.IsNullOrEmpty(Class))
            builder.AddAttribute(4, "class", Class);

        builder.AddContent(5, Text);
        builder.CloseElement();
    }

    /// <summary>
    /// Coordenadas siempre con punto decimal: en un SVG un "12,5" son dos numeros, y
    /// con la cultura del navegador puesta en español el grafico se descuadra entero.
    /// </summary>
    private static string Fmt(double value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
