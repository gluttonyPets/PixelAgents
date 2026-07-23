namespace Server.Models
{
    /// <summary>
    /// Registro histórico (append-only) de cada análisis de aprendizaje: una fila por
    /// ejecución abortada que el analista procesa. Guarda a quién culpó, qué concluyó y
    /// qué hizo con el documento vivo, para tener trazabilidad completa de cómo se llegó
    /// al estado actual del <see cref="ProjectLearningDoc"/>. Tenant-scoped.
    /// </summary>
    public class LearningEntry
    {
        public Guid Id { get; set; }

        public Guid ProjectId { get; set; }
        public Guid ExecutionId { get; set; }
        public Guid? FeedbackId { get; set; }

        /// <summary>Modelo analista usado (proveedor/modelo) para este análisis.</summary>
        public string AnalystModel { get; set; } = "";

        /// <summary>Comentario original del usuario que disparó el análisis.</summary>
        public string? UserComment { get; set; }

        /// <summary>
        /// Atribución de culpa en JSON: lista de { "module": "...", "reason": "...", "confidence": 0.0-1.0 }.
        /// </summary>
        public string? AttributionsJson { get; set; }

        /// <summary>Crítica visual (solo cuando había imágenes implicadas).</summary>
        public string? ImageCritique { get; set; }

        /// <summary>Conclusión destilada por el analista.</summary>
        public string? Conclusion { get; set; }

        /// <summary>Qué hizo con el documento: added / reinforced / updated / skipped_duplicate / resolved_contradiction / none.</summary>
        public string DocAction { get; set; } = "none";

        /// <summary>Detalle de qué se añadió/cambió en el documento, o por qué se descartó.</summary>
        public string? DocChange { get; set; }

        /// <summary>Estado del análisis: "ok" o "error".</summary>
        public string Status { get; set; } = "ok";

        /// <summary>Mensaje de error si el análisis falló.</summary>
        public string? Error { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
