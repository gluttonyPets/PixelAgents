using System.Text.RegularExpressions;

namespace Server.Services.Ai;

/// <summary>
/// Recupera las imagenes que un modulo cita por URL en su texto de entrada.
///
/// Es la pieza que cierra el circulo del modulo Directorio: al modelo se le da
/// el indice (barato en tokens) y el decide que ficheros necesita; para que esa
/// eleccion sirva de algo, alguien tiene que bajarse los bytes antes de llamar
/// a la API de imagen, que solo acepta ficheros y no sabe descargar URLs.
///
/// Solo se descargan URLs del directorio publico de este mismo servidor. Es una
/// lista blanca deliberada: el texto lo escribe un modelo, y seguir cualquier
/// URL que aparezca en el convertiria el servidor en un proxy de peticiones
/// hacia donde diga el prompt.
/// </summary>
public static class ReferenceImageFetcher
{
    /// <summary>Referencias distintas que se bajan como maximo si no se configura otra cosa.</summary>
    public const int DefaultMaxImages = 6;

    /// <summary>Tope por fichero. Una referencia mas grande que esto no la acepta la API.</summary>
    public const long MaxBytesPerImage = 25L * 1024 * 1024;

    /// <summary>Una referencia ya descargada.</summary>
    public sealed record Fetched(string Url, byte[] Data, string ContentType);

    // Corta en el primer caracter que no puede formar parte de una URL. Los
    // parentesis se excluyen porque el indice puede citarlas dentro de un texto
    // y un ")" final se colaria en la ruta.
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""'`\)\]]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extrae del texto las URLs del directorio publico de este servidor, sin
    /// repetidas y en el orden en que aparecen (el modelo suele citar primero la
    /// que considera principal).
    /// </summary>
    public static List<string> ExtractDirectoryUrls(string? text, string? publicBaseUrl, int max = DefaultMaxImages)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || max <= 0) return found;

        // Sin URL publica configurada no hay con que comparar, y aceptar
        // cualquier host seria justo lo que la lista blanca evita.
        var prefix = BuildDirectoryPrefix(publicBaseUrl);
        if (prefix is null) return found;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlPattern.Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':');
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(url)) continue;

            found.Add(url);
            if (found.Count >= max) break;
        }

        return found;
    }

    /// <summary>
    /// Cuantas URLs del directorio hay en el texto, sin tope. Sirve para avisar
    /// de que se han ignorado referencias por encima del maximo.
    /// </summary>
    public static int CountDirectoryUrls(string? text, string? publicBaseUrl) =>
        ExtractDirectoryUrls(text, publicBaseUrl, int.MaxValue).Count;

    /// <summary>Prefijo que debe tener una URL para considerarse del directorio de este servidor.</summary>
    private static string? BuildDirectoryPrefix(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl)) return null;
        if (!Uri.TryCreate(publicBaseUrl.Trim(), UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;

        return $"{publicBaseUrl.TrimEnd('/')}{FileDirectoryIndex.PublicRoute}/";
    }

    /// <summary>
    /// Descarga las referencias. Una que falle se salta: es preferible generar
    /// la imagen con las que si han bajado que tumbar la ejecucion entera.
    /// </summary>
    public static async Task<List<Fetched>> DownloadAsync(
        HttpClient http,
        IEnumerable<string> urls,
        CancellationToken ct = default)
    {
        var results = new List<Fetched>();

        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) continue;

                if (response.Content.Headers.ContentLength is > MaxBytesPerImage) continue;

                var data = await response.Content.ReadAsByteArrayAsync(ct);
                if (data.Length == 0 || data.Length > MaxBytesPerImage) continue;

                results.Add(new Fetched(url, data, contentType));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Referencia que no responde: se ignora y se sigue con el resto.
            }
        }

        return results;
    }

    /// <summary>Nombre legible de una referencia, para los logs y la trazabilidad.</summary>
    public static string FileNameOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : url;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return string.IsNullOrWhiteSpace(name) ? "referencia" : Uri.UnescapeDataString(name);
    }
}
