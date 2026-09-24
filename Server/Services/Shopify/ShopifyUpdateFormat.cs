using System.Text.Json;

namespace Server.Services.Shopify
{
    /// <summary>
    /// Regla del modo "Modificar un articulo existente" del nodo ShopifyBlog: el
    /// articulo a modificar llega siempre por la entrada, asi que la conexion de
    /// entrada tiene que llevar un "Formato de la conexion" (JSON) con, como minimo,
    /// el campo "url". Ese formato se inyecta en el prompt del modulo origen, que
    /// queda obligado a emitir la URL del articulo. Misma regla en el cliente
    /// (Client/Models/ShopifyUpdateFormat.cs).
    /// </summary>
    public static class ShopifyUpdateFormat
    {
        public const string UrlField = "url";

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
}
