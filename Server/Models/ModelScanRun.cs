namespace Server.Models
{
    /// <summary>
    /// Una ejecucion del servicio de deteccion de cambios del catalogo. Guarda el
    /// resultado aunque no haya encontrado nada: "se miro y no habia cambios" es
    /// informacion, y sin ella no se puede saber si el servicio se esta lanzando.
    /// Tenant-scoped.
    /// </summary>
    public class ModelScanRun
    {
        public Guid Id { get; set; }

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }

        /// <summary>"ok" o "error".</summary>
        public string Status { get; set; } = "ok";

        /// <summary>Quien lo lanzo: "manual" (boton de la pantalla) o "scheduled".</summary>
        public string Trigger { get; set; } = "manual";

        public int ModelsScanned { get; set; }
        public int ChangesDetected { get; set; }
        public int NewModels { get; set; }
        public int PriceChanges { get; set; }

        /// <summary>Proveedores a los que se les pudo preguntar, separados por coma.</summary>
        public string? ProvidersQueried { get; set; }

        /// <summary>
        /// Primera ejecucion del tenant: no genera historico porque todo seria "nuevo".
        /// Solo deja la foto inicial contra la que compararan los escaneos siguientes.
        /// </summary>
        public bool IsBaseline { get; set; }

        public string? Error { get; set; }
    }
}
