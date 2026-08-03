using System.Net.Http.Headers;
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

        /// <summary>
        /// Sube los bytes de una imagen a Shopify mediante "staged upload" y devuelve la
        /// resourceUrl resultante, valida como imagen destacada del articulo.
        ///
        /// Es el camino fiable: Shopify NO tiene que descargar nada de nuestro servidor,
        /// asi que funciona aunque el servidor no sea accesible desde internet o publique
        /// en http/IP/puerto no estandar (caso en el que Shopify responde
        /// "Image upload failed. Invalid URL provided.").
        /// </summary>
        private async Task<string> StageImageUploadAsync(
            string shopDomain, string token,
            byte[] data, string fileName, string mimeType, CancellationToken ct)
        {
            const string mutation = @"mutation StageUpload($input: [StagedUploadInput!]!) {
  stagedUploadsCreate(input: $input) {
    stagedTargets { url resourceUrl parameters { name value } }
    userErrors { field message }
  }
}";
            // fileSize solo es obligatorio para VIDEO y MODEL_3D. En imagenes se omite a
            // proposito: al enviarlo, el destino firma una politica con content-length-range
            // y cualquier desajuste responde SignatureDoesNotMatch.
            var input = new[]
            {
                new
                {
                    filename = fileName,
                    mimeType,
                    resource = "IMAGE",
                    httpMethod = "POST",
                }
            };

            string uploadUrl;
            string resourceUrl;
            var formParams = new List<(string Name, string Value)>();

            using (var doc = await PostGraphQlAsync(
                shopDomain, token, new { query = mutation, variables = new { input } }, ct))
            {
                var node = doc.RootElement.GetProperty("data").GetProperty("stagedUploadsCreate");

                if (node.TryGetProperty("userErrors", out var errs) &&
                    errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                {
                    var messages = string.Join("; ", errs.EnumerateArray()
                        .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                        .Where(m => !string.IsNullOrWhiteSpace(m)));
                    throw new HttpRequestException($"stagedUploadsCreate: {messages}");
                }

                if (!node.TryGetProperty("stagedTargets", out var targets) ||
                    targets.ValueKind != JsonValueKind.Array || targets.GetArrayLength() == 0)
                    throw new HttpRequestException("Shopify no devolvio un destino de subida (stagedTargets vacio).");

                var target = targets[0];
                uploadUrl = target.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                resourceUrl = target.TryGetProperty("resourceUrl", out var r) ? r.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(resourceUrl))
                    throw new HttpRequestException("Shopify devolvio un destino de subida incompleto.");

                if (target.TryGetProperty("parameters", out var pars) && pars.ValueKind == JsonValueKind.Array)
                    foreach (var p in pars.EnumerateArray())
                        formParams.Add((
                            p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                            p.TryGetProperty("value", out var v) ? v.GetString() ?? "" : ""));
            }

            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            // El destino (Google Cloud Storage) exige que los parametros firmados vayan
            // ANTES del campo "file"; si se altera el orden responde SignatureDoesNotMatch.
            using var form = new MultipartFormDataContent();
            foreach (var (name, value) in formParams)
                if (!string.IsNullOrEmpty(name))
                    form.Add(new StringContent(value), name);

            var fileContent = new ByteArrayContent(data);
            if (MediaTypeHeaderValue.TryParse(mimeType, out var parsedType))
                fileContent.Headers.ContentType = parsedType;
            form.Add(fileContent, "file", fileName);

            // Peticion sin la cabecera de Shopify: el destino de subida es externo.
            HttpResponseMessage uploadResponse;
            try
            {
                uploadResponse = await _http.PostAsync(uploadUrl, form, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("La subida de la imagen a Shopify supero el tiempo limite.");
            }

            if (!uploadResponse.IsSuccessStatusCode)
            {
                var body = await uploadResponse.Content.ReadAsStringAsync(linkedCts.Token);
                throw new HttpRequestException(
                    $"Fallo la subida de la imagen ({(int)uploadResponse.StatusCode}): {Truncate(body)}");
            }

            return resourceUrl;
        }

        /// <summary>Crea un articulo de blog. Devuelve el id/handle o un error legible.</summary>
        /// <param name="summary">Extracto del articulo (campo nativo "summary"). Opcional.</param>
        /// <param name="handle">Identificador URL / slug (campo nativo "handle"). Si va vacio, Shopify lo genera a partir del titulo.</param>
        /// <param name="seoTitle">Titulo de la pagina para SEO. Se guarda como metafield global.title_tag. Opcional.</param>
        /// <param name="metaDescription">Metadescripcion para SEO. Se guarda como metafield global.description_tag. Opcional.</param>
        /// <param name="imageUrl">URL publica de la imagen destacada. Solo se usa como respaldo si no se pasan <paramref name="imageBytes"/>; Shopify tiene que poder descargarla desde internet. Opcional.</param>
        /// <param name="imageAltText">Texto alternativo de la imagen destacada (accesibilidad/SEO). Opcional.</param>
        /// <param name="imageBytes">Bytes de la imagen destacada. Via preferente: se suben a Shopify con un "staged upload", sin depender de que nuestro servidor sea accesible desde internet. Opcional.</param>
        /// <param name="imageFileName">Nombre de archivo de la imagen destacada (para el staged upload). Opcional.</param>
        /// <param name="imageContentType">Content-Type de la imagen destacada (para el staged upload). Opcional.</param>
        public async Task<ShopifyArticleResult> CreateArticleAsync(
            string shopDomain, string clientId, string clientSecret, string blogId,
            string title, string bodyHtml, string? authorName, bool isPublished,
            IEnumerable<string>? tags,
            string? summary = null, string? handle = null,
            string? seoTitle = null, string? metaDescription = null,
            string? imageUrl = null, string? imageAltText = null,
            byte[]? imageBytes = null, string? imageFileName = null, string? imageContentType = null,
            CancellationToken ct = default)
        {
            var token = await GetAccessTokenAsync(shopDomain, clientId, clientSecret, ct);

            // Resolucion de la imagen destacada. Preferimos SIEMPRE subir los bytes a
            // Shopify: es lo unico que no depende de que nuestro servidor sea alcanzable
            // desde internet (con http/IP/puerto raro Shopify responde
            // "Image upload failed. Invalid URL provided.").
            string? warning = null;
            var resolvedImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl.Trim();

            if (imageBytes is { Length: > 0 })
            {
                try
                {
                    resolvedImageUrl = await StageImageUploadAsync(
                        shopDomain, token, imageBytes,
                        string.IsNullOrWhiteSpace(imageFileName) ? "imagen.png" : imageFileName.Trim(),
                        string.IsNullOrWhiteSpace(imageContentType) ? "image/png" : imageContentType.Trim(),
                        ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    warning = $"No se pudo subir la imagen destacada a Shopify: {ex.Message}";
                    if (ex.Message.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("not approved", StringComparison.OrdinalIgnoreCase))
                        warning += ". Anade el scope 'write_files' a la app de Shopify y reinstalala.";
                    // Se intentara con la URL publica si se aporto una.
                }
            }

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

            // Extracto e identificador URL son campos nativos del ArticleCreateInput.
            if (!string.IsNullOrWhiteSpace(summary))
                article["summary"] = summary.Trim();
            if (!string.IsNullOrWhiteSpace(handle))
                article["handle"] = handle.Trim();

            // Imagen destacada: campo nativo "image" (ArticleImageInput { url, altText }).
            // resolvedImageUrl es la resourceUrl del staged upload (via preferente) o, en
            // su defecto, la URL publica que Shopify tendra que descargar.
            if (!string.IsNullOrWhiteSpace(resolvedImageUrl))
            {
                var image = new Dictionary<string, object?> { ["url"] = resolvedImageUrl };
                if (!string.IsNullOrWhiteSpace(imageAltText))
                    image["altText"] = imageAltText.Trim();
                article["image"] = image;
            }

            // El titulo de pagina y la metadescripcion SEO no son campos nativos del
            // articulo: Shopify los expone como metafields del namespace "global"
            // (title_tag / description_tag). Requiere el scope write_content.
            var metafields = new List<object>();
            if (!string.IsNullOrWhiteSpace(seoTitle))
                metafields.Add(new
                {
                    @namespace = "global",
                    key = "title_tag",
                    type = "single_line_text_field",
                    value = seoTitle.Trim(),
                });
            if (!string.IsNullOrWhiteSpace(metaDescription))
                metafields.Add(new
                {
                    @namespace = "global",
                    key = "description_tag",
                    type = "single_line_text_field",
                    value = metaDescription.Trim(),
                });
            if (metafields.Count > 0)
                article["metafields"] = metafields;

            var (result, imageError) = await TryCreateAsync(article);

            // Si lo unico que Shopify rechaza es la imagen, no tiramos el articulo entero:
            // reintentamos sin imagen destacada y avisamos.
            if (imageError is not null && article.Remove("image"))
            {
                warning = warning is null
                    ? $"Shopify rechazo la imagen destacada ({imageError}); el articulo se publico SIN imagen."
                    : $"{warning}. Ademas Shopify rechazo la imagen ({imageError}); el articulo se publico SIN imagen.";
                (result, _) = await TryCreateAsync(article);
            }

            return result with { Warning = warning };

            // Envia la mutacion y separa el caso "solo falla la imagen" del resto.
            async Task<(ShopifyArticleResult Result, string? ImageError)> TryCreateAsync(
                Dictionary<string, object?> articleInput)
            {
                var payload = new { query = mutation, variables = new { article = articleInput } };
                using var doc = await PostGraphQlAsync(shopDomain, token, payload, ct);

                var createNode = doc.RootElement.GetProperty("data").GetProperty("articleCreate");

                if (createNode.TryGetProperty("userErrors", out var userErrors) &&
                    userErrors.ValueKind == JsonValueKind.Array && userErrors.GetArrayLength() > 0)
                {
                    var errorList = userErrors.EnumerateArray()
                        .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : null)
                        .Where(m => !string.IsNullOrWhiteSpace(m))
                        .ToList();
                    var joined = string.Join("; ", errorList);

                    var onlyImageFailed = articleInput.ContainsKey("image")
                        && errorList.Count > 0
                        && errorList.All(m => m!.Contains("image", StringComparison.OrdinalIgnoreCase));

                    return (new ShopifyArticleResult(false, null, null, joined),
                            onlyImageFailed ? joined : null);
                }

                if (createNode.TryGetProperty("article", out var articleNode) &&
                    articleNode.ValueKind == JsonValueKind.Object)
                {
                    var id = articleNode.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var createdHandle = articleNode.TryGetProperty("handle", out var hEl) ? hEl.GetString() : null;
                    return (new ShopifyArticleResult(true, id, createdHandle, null), null);
                }

                return (new ShopifyArticleResult(false, null, null, "Shopify no devolvio el articulo creado."), null);
            }
        }

        private static string Truncate(string s, int max = 300) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
    }

    /// <summary><paramref name="Warning"/> reporta incidencias no fatales (p. ej. el articulo se publico sin imagen destacada).</summary>
    public record ShopifyArticleResult(
        bool Success, string? ArticleId, string? Handle, string? Error, string? Warning = null);
}
