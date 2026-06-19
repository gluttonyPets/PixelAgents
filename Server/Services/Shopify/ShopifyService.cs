using System.Text;
using System.Text.Json;
using Server.Models;

namespace Server.Services.Shopify
{
    /// <summary>
    /// Cliente minimo de la Admin GraphQL API de Shopify. Solo cubre lo que el
    /// modulo de artículos de blog necesita: listar blogs y crear un articulo.
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

        private string Endpoint(string shopDomain) =>
            $"https://{NormalizeDomain(shopDomain)}/admin/api/{ApiVersion}/graphql.json";

        private async Task<JsonDocument> PostAsync(
            string shopDomain, string token, object payload, CancellationToken ct)
        {
            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var body = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(shopDomain));
            request.Headers.Add("X-Shopify-Access-Token", (token ?? "").Trim());
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
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                doc.Dispose();
                throw new HttpRequestException($"Shopify GraphQL error: {string.Join("; ", messages)}");
            }

            return doc;
        }

        /// <summary>Lista los blogs de la tienda (id GID + titulo + handle).</summary>
        public async Task<List<ShopifyBlogDto>> ListBlogsAsync(
            string shopDomain, string token, CancellationToken ct = default)
        {
            const string query = "query { blogs(first: 100) { nodes { id title handle } } }";
            using var doc = await PostAsync(shopDomain, token, new { query }, ct);

            var result = new List<ShopifyBlogDto>();
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("blogs", out var blogs) &&
                blogs.TryGetProperty("nodes", out var nodes))
            {
                foreach (var n in nodes.EnumerateArray())
                {
                    var id = n.GetProperty("id").GetString() ?? "";
                    var title = n.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    var handle = n.TryGetProperty("handle", out var h) ? h.GetString() : null;
                    if (!string.IsNullOrEmpty(id))
                        result.Add(new ShopifyBlogDto(id, title, handle));
                }
            }
            return result;
        }

        /// <summary>Crea un articulo de blog. Devuelve el id/handle o un error legible.</summary>
        public async Task<ShopifyArticleResult> CreateArticleAsync(
            string shopDomain, string token, string blogId, string title, string bodyHtml,
            string? authorName, bool isPublished, IEnumerable<string>? tags,
            CancellationToken ct = default)
        {
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
            };
            if (!string.IsNullOrWhiteSpace(authorName))
                article["author"] = new { name = authorName };
            var tagList = tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            if (tagList is { Count: > 0 })
                article["tags"] = tagList;

            var payload = new { query = mutation, variables = new { article } };
            using var doc = await PostAsync(shopDomain, token, payload, ct);

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
