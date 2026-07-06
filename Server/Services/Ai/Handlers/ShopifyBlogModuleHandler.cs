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
    /// Si el texto ya parece HTML lo deja tal cual; si es texto plano lo convierte
    /// en parrafos (doble salto de linea = parrafo, salto simple = &lt;br&gt;).
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

    private static bool LooksLikeHtml(string s) =>
        s.Contains("<p", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<h1", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<h2", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<br", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<div", StringComparison.OrdinalIgnoreCase)
        || s.Contains("<ul", StringComparison.OrdinalIgnoreCase);

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
    public string[]? Tags { get; init; }

    private static readonly string[] TitleKeys = ["titulo", "title", "titulo_articulo"];
    private static readonly string[] BodyKeys = ["cuerpo", "contenido", "body", "content", "html"];
    private static readonly string[] ExcerptKeys = ["extracto", "resumen", "summary", "excerpt"];
    private static readonly string[] SlugKeys = ["slug", "handle", "identificador_url", "url"];
    private static readonly string[] SeoTitleKeys = ["seo_titulo", "titulo_seo", "titulo_pagina", "seo_title", "page_title", "meta_title"];
    private static readonly string[] MetaDescKeys = ["seo_descripcion", "metadescripcion", "meta_descripcion", "meta_description", "seo_description"];
    private static readonly string[] AuthorKeys = ["autor", "author"];
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
