using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Server.Services.Ai;

public interface IModelAvailabilityService
{
    /// <summary>
    /// Ids de modelo que la API key puede usar de verdad, o null si no se ha podido
    /// comprobar (sin key, sin red, key invalida). Null significa "no lo se", que no
    /// es lo mismo que un conjunto vacio.
    /// </summary>
    Task<IReadOnlySet<string>?> GetAvailableModelIdsAsync(
        string providerType, string apiKey, CancellationToken ct = default);
}

/// <summary>
/// Comprueba contra el proveedor que modelos existen realmente para una API key.
///
/// Es la unica parte del ciclo de vida que se puede automatizar: ningun proveedor
/// publica un endpoint de tarifas ni de fechas de retirada, pero todos tienen un
/// listado de modelos. Sirve para detectar tres cosas que la tabla local no ve:
/// modelos ya apagados, modelos nuevos que aun no estan en el catalogo, y modelos que
/// existen pero a los que esa cuenta concreta no tiene acceso.
///
/// El resultado se cachea: la lista cambia como mucho unas pocas veces al mes y no
/// tiene sentido pagar un round-trip cada vez que se pinta un desplegable.
/// </summary>
public class ModelAvailabilityService : IModelAvailabilityService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    /// <param name="AuthMode">
    /// Como viaja la key: cabecera <c>Authorization: Bearer</c>, cabecera propia del
    /// proveedor (<c>x-api-key</c>) o parametro de query. Cada uno eligio la suya.
    /// </param>
    /// <param name="ArrayProperty">Propiedad raiz que contiene la lista ("data" o "models").</param>
    /// <param name="IdProperty">Campo con el id dentro de cada elemento ("id" o "name").</param>
    /// <param name="StripPrefix">
    /// Prefijo que trae el id y que hay que quitar para compararlo con el catalogo.
    /// Gemini devuelve "models/gemini-2.5-flash", no "gemini-2.5-flash".
    /// </param>
    private record ProviderApi(
        string Url,
        string AuthMode,
        string ArrayProperty,
        string IdProperty,
        string? StripPrefix = null,
        IReadOnlyDictionary<string, string>? ExtraHeaders = null);

    private static readonly Dictionary<string, ProviderApi> ListEndpoints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = new("https://api.openai.com/v1/models", "bearer", "data", "id"),

            // Anthropic usa cabecera propia y exige versionar la API en cada llamada.
            ["Anthropic"] = new(
                "https://api.anthropic.com/v1/models?limit=1000", "header", "data", "id",
                ExtraHeaders: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" }),

            ["xAI"] = new("https://api.x.ai/v1/models", "bearer", "data", "id"),

            // Gemini no acepta Bearer en esta ruta: la key va en la query.
            ["Google"] = new(
                "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000",
                "query", "models", "name", StripPrefix: "models/"),
        };

    /// <summary>
    /// Proveedores con listado de modelos. Leonardo y Canva no lo tienen: sus modelos
    /// solo salen del catalogo local, y preguntarles no devolveria nada util.
    /// </summary>
    public static IReadOnlyCollection<string> QueryableProviders => ListEndpoints.Keys;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ModelAvailabilityService>? _log;

    public ModelAvailabilityService(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ILogger<ModelAvailabilityService>? log = null)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _log = log;
    }

    public async Task<IReadOnlySet<string>?> GetAvailableModelIdsAsync(
        string providerType, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        if (!ListEndpoints.TryGetValue(providerType, out var api)) return null;

        // La key no se guarda en la clave de cache en claro: solo su hash, para no
        // dejar secretos en memoria mas alla de lo que ya vive en la request.
        var cacheKey = $"models:{providerType}:{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(apiKey)))[..16]}";

        if (_cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached))
            return cached;

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);

            // La key en la query no se registra en ningun log: el mensaje de error de
            // mas abajo nombra el proveedor, nunca la URL.
            var url = api.AuthMode == "query"
                ? $"{api.Url}&key={Uri.EscapeDataString(apiKey)}"
                : api.Url;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            if (api.AuthMode == "bearer")
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else if (api.AuthMode == "header")
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey);

            if (api.ExtraHeaders is not null)
                foreach (var (name, value) in api.ExtraHeaders)
                    req.Headers.TryAddWithoutValidation(name, value);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log?.LogWarning("No se pudo listar modelos de {Provider}: HTTP {Status}",
                    providerType, (int)res.StatusCode);
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty(api.ArrayProperty, out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty(api.IdProperty, out var id)
                    || id.GetString() is not { Length: > 0 } s)
                    continue;

                if (api.StripPrefix is not null && s.StartsWith(api.StripPrefix, StringComparison.Ordinal))
                    s = s[api.StripPrefix.Length..];

                if (s.Length > 0) ids.Add(s);
            }

            // Una lista vacia es casi siempre un fallo de permisos disfrazado de 200:
            // tratarla como "ningun modelo disponible" marcaria todo el catalogo en rojo.
            if (ids.Count == 0) return null;

            _cache.Set<IReadOnlySet<string>>(cacheKey, ids, CacheDuration);
            return ids;
        }
        catch (Exception ex)
        {
            // La disponibilidad es informativa: si falla, la UI se queda con la tabla
            // local en vez de romper la pantalla de modelos.
            _log?.LogWarning(ex, "Fallo consultando modelos disponibles de {Provider}", providerType);
            return null;
        }
    }
}
