using System.Text;
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

        // Cuerpo del articulo: texto de entrada (concatenado si hay fan-in).
        var bodyText = ctx.GetInputText("input_content");
        if (string.IsNullOrWhiteSpace(bodyText))
            return ModuleResult.Failed("Sin contenido de entrada para el articulo");

        // Titulo: override en config -> titulo del output anterior -> primera linea.
        var title = ctx.GetConfig("title");
        if (string.IsNullOrWhiteSpace(title))
            title = FirstInputTitle(ctx);
        if (string.IsNullOrWhiteSpace(title))
            title = FirstLine(bodyText);
        if (string.IsNullOrWhiteSpace(title))
            title = "Sin titulo";

        var author = ctx.GetConfig("author");
        var isPublished = ctx.GetConfigBool("published", false);
        var tags = ctx.GetConfig("tags")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var bodyHtml = ToHtml(bodyText);

        await ctx.LogInfoAsync($"Publicando articulo en Shopify ({connection.ShopDomain}) — \"{title}\" ({(isPublished ? "publicado" : "borrador")})");

        ShopifyArticleResult result;
        try
        {
            result = await _shopify.CreateArticleAsync(
                connection.ShopDomain, connection.AccessToken, blogId,
                title, bodyHtml, string.IsNullOrWhiteSpace(author) ? null : author,
                isPublished, tags, ctx.CancellationToken);
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
}
