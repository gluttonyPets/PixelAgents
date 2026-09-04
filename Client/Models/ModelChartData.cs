namespace Client.Models;

/// <summary>Un valor dentro de una barra, con su texto ya formateado.</summary>
public record ChartValue(decimal Value, string Text);

/// <summary>
/// Una fila de un grafico de barras. Puede llevar varias series (entrada y salida,
/// o las tres calidades de imagen): todas comparten escala, que es lo que permite
/// leer de un vistazo cual es la cara.
/// </summary>
public record ChartBar(
    string Label,
    string? Sublabel,
    string Provider,
    IReadOnlyList<ChartValue> Values);

/// <summary>Un punto de la nube precio/capacidad.</summary>
public record ChartPoint(string Label, double X, double Y, string Provider, string Tooltip);

/// <summary>
/// Color de cada proveedor. Es el mismo que usan las pastillas de filtro y las
/// insignias del resto de la app: cambiarlo aqui sin cambiarlo en el CSS haria que
/// el mismo proveedor tuviese dos colores en la misma pantalla.
/// </summary>
public static class ProviderPalette
{
    public static string Color(string provider) => provider switch
    {
        "OpenAI"     => "#10a37f",
        "Anthropic"  => "#e8830c",
        "Google"     => "#4285f4",
        "xAI"        => "#9ca3af",
        "LeonardoAI" => "#a78bfa",
        "Canva"      => "#00c4cc",
        _            => "#6c63ff",
    };

    /// <summary>Clase CSS de la pastilla de filtro del proveedor.</summary>
    public static string PillClass(string provider) => provider switch
    {
        "OpenAI"     => "badge-openai",
        "Anthropic"  => "badge-anthropic",
        "Google"     => "badge-google",
        "xAI"        => "badge-xai",
        "LeonardoAI" => "badge-leonardo",
        _            => "",
    };
}
