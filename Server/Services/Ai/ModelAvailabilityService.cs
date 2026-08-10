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
/// Es la unica parte del ciclo de vida que se puede automatizar: OpenAI no publica
/// ningun endpoint de tarifas ni de fechas de retirada, pero GET /v1/models si dice
/// que ids responden hoy. Sirve para detectar tres cosas que la tabla local no ve:
/// modelos ya apagados, modelos nuevos que aun no estan en el catalogo, y modelos que
/// existen pero a los que esa cuenta concreta no tiene acceso.
///
/// El resultado se cachea: la lista cambia como mucho unas pocas veces al mes y no
/// tiene sentido pagar un round-trip cada vez que se pinta un desplegable.
/// </summary>
public class ModelAvailabilityService : IModelAvailabilityService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private static readonly Dictionary<string, string> ListEndpoints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] = "https://api.openai.com/v1/models",
        };

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
        if (!ListEndpoints.TryGetValue(providerType, out var endpoint)) return null;

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

            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _log?.LogWarning("No se pudo listar modelos de {Provider}: HTTP {Status}",
                    providerType, (int)res.StatusCode);
                return null;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                return null;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    ids.Add(s);
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
