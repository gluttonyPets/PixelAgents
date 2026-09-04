namespace Server.Models
{
    /// <summary>
    /// Un cambio detectado por el servicio de deteccion sobre el catalogo de modelos.
    /// Append-only: es el historico que responde a "cuando subio el precio de esto" y
    /// "desde cuando existe este modelo". Tenant-scoped.
    /// </summary>
    public class ModelCatalogChange
    {
        public Guid Id { get; set; }

        /// <summary>Ejecucion del escaneo que lo detecto (<see cref="ModelScanRun"/>).</summary>
        public Guid ScanId { get; set; }

        public string ModelId { get; set; } = "";
        public string Provider { get; set; } = "";

        /// <summary>
        /// Que ha pasado:
        /// <list type="bullet">
        /// <item><c>new_model</c> — modelo nuevo en el catalogo del repo.</item>
        /// <item><c>provider_new_model</c> — el proveedor lo lista y el catalogo no lo tiene.</item>
        /// <item><c>price_change</c> — cambio de tarifa de un modelo ya conocido.</item>
        /// <item><c>lifecycle_change</c> — pasa a deprecated o a retired.</item>
        /// <item><c>removed_model</c> — desaparece del catalogo del repo.</item>
        /// </list>
        /// </summary>
        public string ChangeType { get; set; } = "";

        /// <summary>Campo concreto que cambio ("InputPerMTok", "ImageHigh"...). null en altas y bajas.</summary>
        public string? Field { get; set; }

        public string? OldValue { get; set; }
        public string? NewValue { get; set; }

        /// <summary>Explicacion legible para la pantalla, ya montada por el servicio.</summary>
        public string? Note { get; set; }

        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    }
}
