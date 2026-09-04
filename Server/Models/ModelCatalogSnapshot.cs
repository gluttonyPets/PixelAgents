namespace Server.Models
{
    /// <summary>
    /// Ultimo estado conocido de un modelo, tal y como lo vio el servicio de deteccion
    /// de cambios la ultima vez que se lanzo. Una fila por modelo.
    ///
    /// Es la foto contra la que se compara: sin ella no habria forma de saber si un
    /// precio ha cambiado, porque el catalogo compilado solo dice cuanto cuesta hoy,
    /// nunca cuanto costaba ayer. El historico (<see cref="ModelCatalogChange"/>) sale
    /// de la diferencia entre esta foto y el estado actual. Tenant-scoped.
    /// </summary>
    public class ModelCatalogSnapshot
    {
        public Guid Id { get; set; }

        /// <summary>Id del modelo tal y como lo pide la API del proveedor.</summary>
        public string ModelId { get; set; } = "";

        public string Provider { get; set; } = "";
        public string DisplayName { get; set; } = "";

        /// <summary>"text", "image" u "other", igual que en la pantalla de modelos.</summary>
        public string Kind { get; set; } = "text";

        public decimal? InputPerMTok { get; set; }
        public decimal? OutputPerMTok { get; set; }
        public decimal? ImageLow { get; set; }
        public decimal? ImageMedium { get; set; }
        public decimal? ImageHigh { get; set; }
        public decimal? AuxAmount { get; set; }
        public string? AuxUnit { get; set; }

        /// <summary>"active", "deprecated" o "retired".</summary>
        public string LifecycleStatus { get; set; } = "active";

        /// <summary>
        /// De donde salio la fila:
        /// <list type="bullet">
        /// <item><c>catalog</c> — esta en el catalogo del repo, con ficha y tarifa.</item>
        /// <item><c>provider</c> — el proveedor lo lista pero el catalogo no lo conoce.
        /// Se guarda para no volver a anunciarlo como nuevo en cada escaneo.</item>
        /// </list>
        /// </summary>
        public string Source { get; set; } = "catalog";

        /// <summary>
        /// Si el proveedor lo listaba la ultima vez que se le pudo preguntar. null es
        /// "no se sabe" (sin key, sin red, o proveedor sin endpoint de listado), que no
        /// es lo mismo que "no esta": solo se avisa cuando cambia de true a false.
        /// </summary>
        public bool? AvailableUpstream { get; set; }

        public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    }
}
