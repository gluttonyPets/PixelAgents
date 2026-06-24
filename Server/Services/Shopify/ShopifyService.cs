using System.Text;
using System.Text.Json;
using Server.Models;

namespace Server.Services.Shopify
{
    /// <summary>
    /// Cliente minimo de la Admin GraphQL API de Shopify. Obtiene el access token
    /// mediante el flujo "client credentials" (Dev Dashboard, 2026+): intercambia
    /// Client ID + Client Secret por un token de 24 h en cada operacion, y luego
    /// llama a la API. Solo cubre lo que el modulo de blog necesita: listar blogs
    /// y crear un articulo.
    /// </summary>
    public class ShopifyService
    {
        private readonly HttpClient _http;

        // Version estable de la Admin API. articleCreate y la query blogs estan
        // disponibles desde 2024-04; mantenemos una version reciente y estable.
        private const string ApiVersion = "2025-07";
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

        public ShopifyService(HttpClient http)
        {
            _http = http;
        }

        /// <summary>Normaliza el dominio: quita protocolo y barras finales.</summary>
        public static string NormalizeDomain(string shopDomain)
        {
            var d = (shopDomain ?? "").Trim();
            d = d.Replace("https://", "").Replace("http://", "");
            return d.TrimEnd('/');
        }

        // Shopify aplica proteccion anti-bot y bloquea (403 "Verifying your connection")
        // las peticiones sin User-Agent. Estas cabeceras la evitan.
        private static void AddDefaultHeaders(HttpRequestMessage request)
        {
            request.Headers.UserAgent.ParseAdd("PixelAgents/1.0 (+https://gluttony.es)");
            request.Headers.Accept.ParseAdd("application/json");
        }

        private static bool LooksLikeHtml(string s)
        {
            var t = s.TrimStart();
            return t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Intercambia Client ID + Client Secret por un access token (valido 24 h).
        /// POST https://{shop}/admin/oauth/access_token con grant_type=client_credentials.
        /// </summary>
        private async Task<string> GetAccessTokenAsync(
            string shopDomain, string clientId, string clientSecret, CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var url = $"https://{NormalizeDomain(shopDomain)}/admin/oauth/access_token";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = (clientId ?? "").Trim(),
                    ["client_secret"] = (clientSecret ?? "").Trim(),
                })
            };
            AddDefaultHeaders(request);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Shopify no respondio al pedir el token.");
            }

            var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
            if (!response.IsSuccessStatusCode || LooksLikeHtml(json))
            {
                if (LooksLikeHtml(json))
                    throw new HttpRequestException(
                        "Shopify devolvio una pagina de verificacion anti-bot al pedir el token. " +
                        "Asegurate de usar el dominio .myshopify.com (no el dominio personalizado).");
                throw new HttpRequestException(
                    $"No se pudo obtener el token de Shopify ({(int)response.StatusCode}). " +
                    $"Revisa el dominio (.myshopify.com), el Client ID y el Client Secret. {Truncate(json)}");
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) ||
                tokenEl.GetString() is not { Length: > 0 } token)
                throw new HttpRequestException("Shopify no devolvio un access token.");

            return token;
        }

        private async Task<JsonDocument> PostGraphQlAsync(
            string shopDomain, string token, object payload, CancellationToken ct,
            bool throwOnGraphQlErrors = true)
        {
            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var endpoint = $"https://{NormalizeDomain(shopDomain)}/admin/api/{ApiVersion}/graphql.json";
            var body = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("X-Shopify-Access-Token", token);
            AddDefaultHeaders(request);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Shopify no respondio dentro del tiempo limite.");
            }

            var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Shopify API error {(int)response.StatusCode}: {Truncate(json)}");

            var doc = JsonDocument.Parse(json);
            if (throwOnGraphQlErrors &&
                doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var messages = string.Join("; ", errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                    .Where(m => !string.IsNullOrWhiteSpace(m)));
                doc.Dispose();
                throw new HttpRequestException($"Shopify GraphQL error: {messages}");
            }

            return doc;
        }

        /// <summary>Lista los blogs de la tienda (id GID + titulo + handle).</summary>
        public async Task<List<ShopifyBlogDto>> ListBlogsAsync(
            string shopDomain, string clientId, string clientSecret, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(shopDomain, clientId, clientSecret, ct);
            // Pedimos tambien los scopes concedidos: si blogs sale denegado, el error
            // dira exactamente que permisos tiene el token (para diagnosticar).
            const string query = @"query {
  currentAppInstallation { accessScopes { handle } }
  blogs(first: 100) { nodes { id title handle } }
}";
            using var doc = await PostGraphQlAsync(shopDomain, token, new { query }, ct, throwOnGraphQlErrors: false);
            var root = doc.RootElement;

            var result = new List<ShopifyBlogDto>();
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("blogs", out var blogs) &&
                blogs.ValueKind == JsonValueKind.Object &&
                blogs.TryGetProperty("nodes", out var nodes) &&
                nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in nodes.EnumerateArray())
                {
                    var id = n.GetProperty("id").GetString() ?? "";
                    var title = n.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var handle = n.TryGetProperty("handle", out var h) ? h.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                        result.Add(new ShopifyBlogDto(id, title, handle));
                }
                return result;
            }

            // blogs no resolvio -> construir un error con el motivo + scopes concedidos.
            var errorMsg = "Access denied for blogs field.";
            if (root.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                errorMsg = string.Join("; ", errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                    .Where(m => !string.IsNullOrWhiteSpace(m)));
            }

            var grantedScopes = ExtractScopes(root);
            var scopesText = grantedScopes.Count > 0
                ? string.Join(", ", grantedScopes)
                : "(ninguno detectado)";

            throw new HttpRequestException(
                $"{errorMsg} | Permisos concedidos al token: {scopesText}. " +
                $"Para listar blogs hace falta 'read_content'. Si no aparece, anade ese scope a la app y REINSTALALA.");
        }

        private static List<string> ExtractScopes(JsonElement root)
        {
            var scopes = new List<string>();
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("currentAppInstallation", out var install) &&
                install.ValueKind == JsonValueKind.Object &&
                install.TryGetProperty("accessScopes", out var accessScopes) &&
                accessScopes.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in accessScopes.EnumerateArray())
                    if (s.TryGetProperty("handle", out var h) && h.GetString() is { Length: > 0 } handle)
                        scopes.Add(handle);
            }
            return scopes;
        }

        /// <summary>Crea un articulo de blog. Devuelve el id/handle o un error legible.</summary>
        public async Task<ShopifyArticleResult> CreateArticleAsync(
            string shopDomain, string clientId, string clientSecret, string blogId,
            string title, string bodyHtml, string? authorName, bool isPublished,
            IEnumerable<string>? tags, CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(shopDomain, clientId, clientSecret, ct);

            const string mutation = @"mutation CreateArticle($article: ArticleCreateInput!) {
  articleCreate(article: $article) {
    article { id title handle }
    userErrors { field message }
  }
}";

            var article = new Dictionary<string, object?>
            {
                ["blogId"] = blogId,
                ["title"] = title,
                ["body"] = bodyHtml,
                ["isPublished"] = isPublished,
                // Shopify exige un autor no nulo en ArticleCreateInput.
                ["author"] = new { name = string.IsNullOrWhiteSpace(authorName) ? "Equipo" : authorName.Trim() },
            };
            var tagList = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (tagList is { Count: > 0 })
                article["tags"] = tagList;

            var payload = new { query = mutation, variables = new { article } };
            using var doc = await PostGraphQlAsync(shopDomain, token, payload, ct);

            var createNode = doc.RootElement.GetProperty("data").GetProperty("articleCreate");

            if (createNode.TryGetProperty("userErrors", out var userErrors) &&
                userErrors.ValueKind == JsonValueKind.Array && userErrors.GetArrayLength() > 0)
            {
                var messages = userErrors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                return new ShopifyArticleResult(false, null, null, string.Join("; ", messages));
            }

            if (createNode.TryGetProperty("article", out var articleNode) &&
                articleNode.ValueKind == JsonValueKind.Object)
            {
                var id = articleNode.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var handle = articleNode.TryGetProperty("handle", out var hEl) ? hEl.GetString() : null;
                return new ShopifyArticleResult(true, id, handle, null);
            }

            return new ShopifyArticleResult(false, null, null, "Shopify no devolvio el articulo creado.");
        }

        private static string Truncate(string s, int max = 300) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
    }

    public record ShopifyArticleResult(bool Success, string? ArticleId, string? Handle, string? Error);
}
