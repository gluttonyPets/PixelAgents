using System.Text.Json;

namespace Client.Models;

/// <summary>
/// Regla del modo "Modificar un articulo existente" del nodo ShopifyBlog: el
/// articulo a modificar lo elige la entrada, asi que la conexion de entrada tiene
/// que llevar un "Formato de la conexion" con, como minimo, el campo "url". Misma
/// regla que en el servidor (Server/Services/Shopify/ShopifyUpdateFormat.cs).
/// </summary>
public static class ShopifyUpdateFormat
{
    public const string UrlField = "url";
    public const string InputPort = "input_content";

    public const string HelpText =
        "En modo Modificar, el articulo que se cambia lo elige la entrada, no este nodo. " +
        "La conexion de entrada (Contenido) tiene que tener un Formato de la conexion con, como minimo, " +
        "el campo \"url\": el modulo anterior lo rellena con la URL del articulo del blog que hay que modificar " +
        "(p. ej. https://tienda.com/blogs/noticias/mi-articulo). El resto de campos (titulo, cuerpo, extracto, " +
        "seo_titulo, seo_descripcion, tags) son opcionales: los que lleguen vacios no se tocan.";

    /// <summary>True si el formato es un objeto JSON con un campo "url" de primer nivel.</summary>
    public static bool HasUrlField(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return false;
        try
        {
            using var doc = JsonDocument.Parse(format);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.EnumerateObject().Any(p => p.Name.Trim().Equals(UrlField, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
