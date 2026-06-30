namespace Server.Models
{
    /// <summary>
    /// Credencial reutilizable de una tienda Shopify. Usa el flujo OAuth
    /// "client credentials" del nuevo Dev Dashboard (Shopify, 2026+): guarda el
    /// Client ID y el Client Secret de la app, y el access token (de 24 h) se
    /// obtiene bajo demanda. Se define una vez en la seccion "Shopify" y los
    /// proyectos la referencian por Id; el blog destino se elige en cada nodo.
    /// </summary>
    public class ShopifyConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;

        /// <summary>Dominio de la tienda, p. ej. "mitienda.myshopify.com".</summary>
        public string ShopDomain { get; set; } = default!;

        /// <summary>Client ID de la app (en el Dev Dashboard figura como API key / Client ID).</summary>
        public string ClientId { get; set; } = default!;

        /// <summary>Client Secret de la app (API secret key / Client secret).</summary>
        public string ClientSecret { get; set; } = default!;

        /// <summary>
        /// Plantilla JSON reutilizable que define el formato del articulo. Se copia en
        /// el modulo de IA anterior para que genere el articulo estructurado (titulo,
        /// cuerpo, extracto, slug y SEO) en un unico output, sin puertos extra. El nodo
        /// ShopifyBlog parsea ese JSON y reparte cada campo. Opcional.
        /// </summary>
        public string? ArticleTemplate { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
