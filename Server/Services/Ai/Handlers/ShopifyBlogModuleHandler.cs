using System.Text;
using System.Text.Json;
using Server.Services.Shopify;

namespace Server.Services.Ai.Handlers;

/// <summary>
/// Publica un articulo de blog en Shopify usando la conexion Shopify asignada al
/// proyecto. Toma el titulo y el cuerpo de los modulos anteriores (p. ej. un
/// modulo de Texto) y el blog destino + opciones de la config del nodo.
/// </summary>
public class ShopifyBlogModuleHandler : IModuleHandler
{
    private readonly ShopifyService _shopify;
    public string ModuleType => "ShopifyBlog";

    public ShopifyBlogModuleHandler(ShopifyService shopify) => _shopify = shopify;

    public async Task<ModuleResult> ExecuteAsync(ModuleExecutionContext ctx)
    {
        var connection = ctx.Project.ShopifyConnection;
        if (connection is null)
            return ModuleResult.Failed("El proyecto no tiene una conexion de Shopify asignada");

        var blogId = ctx.GetConfig("blogId");
        if (string.IsNullOrWhiteSpace(blogId))
            return ModuleResult.Failed("No se ha seleccionado un blog de Shopify en el nodo");

        // Texto de entrada (concatenado si hay fan-in).
        var rawInput = ctx.GetInputText("input_content");
        if (string.IsNullOrWhiteSpace(rawInput))
            return ModuleResult.Failed("Sin contenido de entrada para el articulo");

        // Si el modulo anterior genero el articulo estructurado en JSON (siguiendo la
        // plantilla de la conexion), repartimos cada campo. Si no es JSON valido, todo
        // el texto se trata como cuerpo (comportamiento clasico, retrocompatible).
        var structured = StructuredArticle.TryParse(rawInput);
        var bodyText = string.IsNullOrWhiteSpace(structured?.Body) ? rawInput : structured!.Body!;

        // Precedencia general: override del nodo -> JSON del modulo anterior -> autogenerado.
        string FromNodeOr(string configKey, string? fromJson) =>
            ctx.GetConfig(configKey) is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v)
                ? v
                : (fromJson ?? "");

        // Titulo: config -> JSON -> titulo del output anterior -> primera linea.
        var title = FromNodeOr("title", structured?.Title);
        if (string.IsNullOrWhiteSpace(title))
            title = FirstInputTitle(ctx);
        if (string.IsNullOrWhiteSpace(title))
            title = FirstLine(bodyText);
        if (string.IsNullOrWhiteSpace(title))
            title = "Sin titulo";

        var author = FromNodeOr("author", structured?.Author);
        var isPublished = ctx.GetConfigBool("published", false);

        // Tags: config (coma-separados) -> JSON.
        var tagsConfig = ctx.GetConfig("tags");
        var tags = !string.IsNullOrWhiteSpace(tagsConfig)
            ? tagsConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : (structured?.Tags ?? Array.Empty<string>());

        var bodyHtml = ToHtml(bodyText);

        // Metadatos del articulo. Si no llegan ni por config ni por JSON, se generan
        // automaticamente a partir del titulo y del cuerpo.
        var plainBody = StripHtml(bodyText);

        var excerpt = FromNodeOr("excerpt", structured?.Excerpt);
        if (string.IsNullOrWhiteSpace(excerpt))
            excerpt = Summarize(plainBody, 300);

        var seoTitle = FromNodeOr("seoTitle", structured?.SeoTitle);
        if (string.IsNullOrWhiteSpace(seoTitle))
            seoTitle = title;

        var metaDescription = FromNodeOr("metaDescription", structured?.MetaDescription);
        if (string.IsNullOrWhiteSpace(metaDescription))
            metaDescription = Summarize(string.IsNullOrWhiteSpace(excerpt) ? plainBody : excerpt, 155);

        // Identificador URL (slug). Config -> JSON. Si va vacio se deja en null para
        // que Shopify lo genere a partir del titulo; si llega algo, se normaliza.
        var handleSource = FromNodeOr("handle", structured?.Slug);
        var handle = string.IsNullOrWhiteSpace(handleSource) ? null : Slugify(handleSource);

        // Imagen destacada: si hay un modulo de imagen (u otro) conectado al puerto
        // "input_image", tomamos el primer archivo y enviamos sus BYTES a Shopify, que los
        // sube a su CDN (staged upload). La URL publica queda solo como respaldo.
        // El alt text sale del nodo -> JSON -> titulo.
        string? imageUrl = null;
        string? imageAlt = null;
        byte[]? imageBytes = null;
        string? imageFileName = null;
        string? imageContentType = null;

        var imageFile = ctx.GetInputFiles("input_image").FirstOrDefault();
        if (imageFile is not null)
        {
            imageAlt = FromNodeOr("imageAlt", structured?.ImageAlt);
            if (string.IsNullOrWhiteSpace(imageAlt))
                imageAlt = title;

            // Via preferente: subir los bytes a Shopify (staged upload). No depende de que
            // nuestro servidor sea accesible desde internet, que es justo lo que rompia la
            // publicacion con "Image upload failed. Invalid URL provided.".
            imageBytes = await ctx.ReadOutputFileBytesAsync(imageFile);
            if (imageBytes is { Length: > 0 })
            {
                imageFileName = imageFile.FileName;
                imageContentType = imageFile.ContentType;
                await ctx.LogInfoAsync(
                    $"Imagen destacada: {imageFile.FileName} ({imageFile.ContentType}, {imageBytes.Length} bytes) " +
                    "se subira directamente a Shopify.");
            }
            else
            {
                await ctx.LogWarningAsync(
                    $"No se pudieron leer los bytes de la imagen conectada ({imageFile.FileName}); " +
                    "se intentara con su URL publica.");
            }

            // Respaldo: solo sirve si Shopify puede descargarla desde internet.
            var candidateUrl = ctx.GetPublicFileUrl(imageFile);
            if (IsPubliclyReachableImageUrl(candidateUrl))
                imageUrl = candidateUrl;
            else if (imageBytes is null or { Length: 0 })
                await ctx.LogWarningAsync(
                    $"La URL publica de la imagen no es descargable por Shopify ({candidateUrl ?? "vacia"}); " +
                    "el articulo se publicara sin imagen destacada. Configura 'BaseUrl' con una URL https publica.");
        }

        await ctx.LogInfoAsync($"Publicando articulo en Shopify ({connection.ShopDomain}) — \"{title}\" ({(isPublished ? "publicado" : "borrador")})");

        ShopifyArticleResult result;
        try
        {
            result = await _shopify.CreateArticleAsync(
                connection.ShopDomain, connection.ClientId, connection.ClientSecret, blogId,
                title, bodyHtml, string.IsNullOrWhiteSpace(author) ? null : author,
                isPublished, tags,
                summary: excerpt, handle: handle,
                seoTitle: seoTitle, metaDescription: metaDescription,
                imageUrl: imageUrl, imageAltText: imageAlt,
                imageBytes: imageBytes, imageFileName: imageFileName,
                imageContentType: imageContentType,
                ct: ctx.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ModuleResult.Failed($"Error Shopify: {ex.Message}");
        }

        if (!result.Success)
            return ModuleResult.Failed($"Shopify rechazo el articulo: {result.Error}");

        if (!string.IsNullOrWhiteSpace(result.Warning))
            await ctx.LogWarningAsync(result.Warning);

        var output = new StepOutput
        {
            Type = "text",
            Title = title,
            Content = $"Articulo creado en Shopify: {result.Handle ?? result.ArticleId}",
            Summary = isPublished ? "Articulo publicado" : "Articulo guardado como borrador",
        };
        if (!string.IsNullOrWhiteSpace(result.ArticleId))
            output.Metadata["articleId"] = result.ArticleId;
        if (!string.IsNullOrWhiteSpace(result.Handle))
            output.Metadata["handle"] = result.Handle;

        return ModuleResult.Completed(output);
    }

    /// <summary>
    /// Comprueba que la URL sirva como respaldo para la imagen destacada: Shopify tiene
    /// que poder descargarla desde sus servidores. Exige https absoluto con host publico.
    /// Se descartan las URLs relativas (PublicBaseUrl sin configurar), http plano,
    /// localhost y las redes privadas: en todos esos casos Shopify responde
    /// "Image upload failed. Invalid URL provided.". La via principal no usa esto: los
    /// bytes se suben directamente a Shopify con un staged upload.
    /// </summary>
    private static bool IsPubliclyReachableImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        // http plano: Shopify no lo descarga de forma fiable.
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host)) return false;

        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                // IdnHost devuelve la IP sin corchetes ([::1] -> ::1), como espera IPAddress.
                if (!System.Net.IPAddress.TryParse(uri.IdnHost, out var ip)) return false;
                if (System.Net.IPAddress.IsLoopback(ip)) return false;
                if (ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) return false;
                return !IsPrivateOrLinkLocal(ip);
            case UriHostNameType.Dns:
                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
                // Hostname sin punto (p. ej. "servidor-interno") no es resoluble desde internet.
                return host.Contains('.');
            default:
                return false;
        }
    }

    private static bool IsPrivateOrLinkLocal(System.Net.IPAddress ip)
    {
        switch (ip.AddressFamily)
        {
            case System.Net.Sockets.AddressFamily.InterNetwork:
                var b = ip.GetAddressBytes();
                return b[0] == 10                                   // 10.0.0.0/8
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                    || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                    || (b[0] == 169 && b[1] == 254);                // 169.254.0.0/16 link-local
            case System.Net.Sockets.AddressFamily.InterNetworkV6:
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
            default:
                return false;
        }
    }

    private static string? FirstInputTitle(ModuleExecutionContext ctx)
    {
        if (ctx.InputsByPort.TryGetValue("input_content", out var list))
            return list.Select(d => d.FullOutput?.Title)
                       .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return null;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] : line;
    }

    /// <summary>
    /// Si el texto ya parece HTML lo deja tal cual (incluyendo estilos CSS inline,
    /// bloques &lt;style&gt; y cualquier etiqueta HTML valida); si es texto plano lo
    /// convierte en parrafos (doble salto de linea = parrafo, salto simple = &lt;br&gt;).
    /// </summary>
    private static string ToHtml(string text)
    {
        var trimmed = text.Trim();
        if (LooksLikeHtml(trimmed))
            return trimmed;

        var paragraphs = trimmed.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var p in paragraphs)
        {
            var escaped = Escape(p.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");
            sb.Append("<p>").Append(escaped).Append("</p>");
        }
        return sb.ToString();
    }

    // Detecta cualquier etiqueta HTML de apertura, cierre o auto-cerrada con un nombre
    // de etiqueta valido (<p>, <div>, <style>, <table>, <span style="...">, <br/>, ...).
    // Exige un nombre de etiqueta que empiece por letra, por lo que expresiones de
    // texto plano como "5 < 10" no se confunden con HTML. Basta con una sola etiqueta
    // para tratar TODO el contenido como HTML y enviarlo a Shopify sin escapar, de modo
    // que el CSS (inline o en <style>) llega intacto.
    private static readonly System.Text.RegularExpressions.Regex HtmlTagRegex = new(
        @"<\s*/?\s*[a-zA-Z][a-zA-Z0-9]*(\s[^<>]*?)?/?>",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.Singleline);

    private static bool LooksLikeHtml(string s) => HtmlTagRegex.IsMatch(s);

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Quita etiquetas HTML y normaliza espacios para generar texto plano.</summary>
    private static string StripHtml(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var noTags = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        noTags = System.Net.WebUtility.HtmlDecode(noTags);
        return System.Text.RegularExpressions.Regex.Replace(noTags, "\\s+", " ").Trim();
    }

    /// <summary>
    /// Recorta el texto a <paramref name="maxChars"/> caracteres sin partir palabras,
    /// anadiendo puntos suspensivos si se trunca.
    /// </summary>
    private static string Summarize(string text, int maxChars)
    {
        var t = (text ?? "").Trim();
        if (t.Length <= maxChars) return t;
        var cut = t[..maxChars];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > maxChars / 2)
            cut = cut[..lastSpace];
        return cut.TrimEnd() + "…";
    }

    /// <summary>Convierte un texto en un slug valido para la URL (handle de Shopify).</summary>
    private static string Slugify(string text)
    {
        var normalized = (text ?? "").Trim().ToLowerInvariant();
        // Descomponer acentos (á -> a) y descartar los diacriticos.
        normalized = normalized.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_' or '.') sb.Append('-');
        }
        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return slug.Length > 0 ? slug : "articulo";
    }
}

/// <summary>
/// Articulo estructurado que el modulo anterior puede generar como un unico JSON
/// (segun la plantilla de la conexion). Acepta nombres de campo en español e ingles
/// para tolerar variaciones del modelo. Si el texto no es JSON valido, TryParse
/// devuelve null y el nodo trata todo el contenido como cuerpo.
/// </summary>
internal sealed class StructuredArticle
{
    public string? Title { get; init; }
    public string? Body { get; init; }
    public string? Excerpt { get; init; }
    public string? Slug { get; init; }
    public string? SeoTitle { get; init; }
    public string? MetaDescription { get; init; }
    public string? Author { get; init; }
    public string? ImageAlt { get; init; }
    public string[]? Tags { get; init; }

    private static readonly string[] TitleKeys = ["titulo", "title", "titulo_articulo"];
    private static readonly string[] BodyKeys = ["cuerpo", "contenido", "body", "content", "html"];
    private static readonly string[] ExcerptKeys = ["extracto", "resumen", "summary", "excerpt"];
    private static readonly string[] SlugKeys = ["slug", "handle", "identificador_url", "url"];
    private static readonly string[] SeoTitleKeys = ["seo_titulo", "titulo_seo", "titulo_pagina", "seo_title", "page_title", "meta_title"];
    private static readonly string[] MetaDescKeys = ["seo_descripcion", "metadescripcion", "meta_descripcion", "meta_description", "seo_description"];
    private static readonly string[] AuthorKeys = ["autor", "author"];
    private static readonly string[] ImageAltKeys = ["imagen_alt", "image_alt", "alt", "alt_text", "texto_alternativo"];
    private static readonly string[] TagsKeys = ["tags", "etiquetas"];

    public static StructuredArticle? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = StripCodeFences(raw.Trim());
        if (text.Length == 0 || text[0] != '{') return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return null; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // Indexar las propiedades por nombre normalizado (minusculas) una sola vez.
            var props = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                props[p.Name.Trim().ToLowerInvariant()] = p.Value;

            return new StructuredArticle
            {
                Title = GetString(props, TitleKeys),
                Body = GetString(props, BodyKeys),
                Excerpt = GetString(props, ExcerptKeys),
                Slug = GetString(props, SlugKeys),
                SeoTitle = GetString(props, SeoTitleKeys),
                MetaDescription = GetString(props, MetaDescKeys),
                Author = GetString(props, AuthorKeys),
                ImageAlt = GetString(props, ImageAltKeys),
                Tags = GetTags(props, TagsKeys),
            };
        }
    }

    private static string? GetString(Dictionary<string, JsonElement> props, string[] keys)
    {
        foreach (var k in keys)
            if (props.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var v = el.GetString();
                if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            }
        return null;
    }

    private static string[]? GetTags(Dictionary<string, JsonElement> props, string[] keys)
    {
        foreach (var k in keys)
        {
            if (!props.TryGetValue(k, out var el)) continue;
            if (el.ValueKind == JsonValueKind.Array)
            {
                var list = el.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();
                if (list.Length > 0) return list;
            }
            else if (el.ValueKind == JsonValueKind.String)
            {
                var list = (el.GetString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (list.Length > 0) return list;
            }
        }
        return null;
    }

    /// <summary>Quita las vallas de codigo ```json ... ``` que a veces añade el modelo.</summary>
    private static string StripCodeFences(string s)
    {
        if (!s.StartsWith("```")) return s;
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        var inner = s[(firstNewline + 1)..];
        var lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) inner = inner[..lastFence];
        return inner.Trim();
    }
}
