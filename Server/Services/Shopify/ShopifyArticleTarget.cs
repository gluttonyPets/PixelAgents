namespace Server.Services.Shopify
{
    /// <summary>
    /// Articulo existente que el nodo ShopifyBlog tiene que modificar, tal y como lo
    /// escribe el usuario (o lo emite el modulo anterior): la URL publica del
    /// articulo, su handle, su GID o su id numerico del admin.
    /// </summary>
    /// <param name="ArticleGid">GID del articulo ("gid://shopify/Article/123") si se conoce.</param>
    /// <param name="BlogHandle">Handle del blog, cuando viene en la URL (/blogs/{blog}/{articulo}).</param>
    /// <param name="Handle">Handle del articulo, cuando no se conoce el GID.</param>
    public sealed record ShopifyArticleTarget(string? ArticleGid, string? BlogHandle, string? Handle)
    {
        /// <summary>
        /// Interpreta el texto. Admite:
        /// "https://tienda.com/blogs/noticias/mi-articulo" (tambien con ?query o #ancla),
        /// "/blogs/noticias/mi-articulo", "https://admin.shopify.com/store/x/articles/123",
        /// "gid://shopify/Article/123", "123" y "mi-articulo". Devuelve null si no hay
        /// nada utilizable.
        /// </summary>
        public static ShopifyArticleTarget? Parse(string? raw)
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) return null;

            if (text.StartsWith("gid://shopify/Article/", StringComparison.OrdinalIgnoreCase))
                return new ShopifyArticleTarget(text, null, null);

            if (text.All(char.IsAsciiDigit))
                return new ShopifyArticleTarget($"gid://shopify/Article/{text}", null, null);

            // Quitar esquema, query y ancla para quedarnos con la ruta.
            var path = text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                path = uri.AbsolutePath;
            else
                path = path.Split('?', '#')[0];

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToList();

            // URL del admin: .../articles/123
            var articlesIdx = segments.FindIndex(s => s.Equals("articles", StringComparison.OrdinalIgnoreCase));
            if (articlesIdx >= 0 && articlesIdx + 1 < segments.Count && segments[articlesIdx + 1].All(char.IsAsciiDigit))
                return new ShopifyArticleTarget($"gid://shopify/Article/{segments[articlesIdx + 1]}", null, null);

            // URL publica: [/{idioma}]/blogs/{blog}/{articulo}
            var blogsIdx = segments.FindIndex(s => s.Equals("blogs", StringComparison.OrdinalIgnoreCase));
            if (blogsIdx >= 0)
            {
                if (blogsIdx + 2 < segments.Count)
                    return new ShopifyArticleTarget(null, segments[blogsIdx + 1], segments[blogsIdx + 2]);
                return null; // portada del blog, no es un articulo
            }

            // Handle suelto ("mi-articulo").
            return segments.Count == 1 ? new ShopifyArticleTarget(null, null, segments[0]) : null;
        }

        public override string ToString() =>
            ArticleGid ?? (BlogHandle is null ? Handle ?? "" : $"/blogs/{BlogHandle}/{Handle}");
    }
}
