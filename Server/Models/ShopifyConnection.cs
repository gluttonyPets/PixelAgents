namespace Server.Models
{
    /// <summary>
    /// Credencial reutilizable de una tienda Shopify. Guarda el dominio de la tienda
    /// y el Admin API access token (de una custom app). Se define una vez en la
    /// seccion "Shopify" y los proyectos la referencian por Id; el blog destino se
    /// elige en cada nodo del pipeline, no en la conexion.
    /// </summary>
    public class ShopifyConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;

        /// <summary>Dominio de la tienda, p. ej. "mitienda.myshopify.com".</summary>
        public string ShopDomain { get; set; } = default!;

        /// <summary>Admin API access token (scope write_content para artículos de blog).</summary>
        public string AccessToken { get; set; } = default!;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
