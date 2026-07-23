namespace Server.Models
{
    /// <summary>
    /// Documento vivo de aprendizaje de un proyecto. Es la memoria destilada que el
    /// analista mantiene a partir de los abortos con comentario: un texto plano
    /// (markdown) legible y descargable, más una versión estructurada de los
    /// "aprendizajes activos" que se inyecta —etiquetada— en las ejecuciones.
    /// Una fila por proyecto. Tenant-scoped.
    /// </summary>
    public class ProjectLearningDoc
    {
        public Guid Id { get; set; }

        /// <summary>Proyecto al que pertenece (único).</summary>
        public Guid ProjectId { get; set; }

        /// <summary>Documento markdown consolidado, legible y descargable por el usuario.</summary>
        public string Content { get; set; } = "";

        /// <summary>
        /// Aprendizajes activos en JSON: lista de { "module": "&lt;nombre&gt;|general", "text": "..." }.
        /// Es lo que se inyecta en el prompt (filtrado por módulo). Se mantiene acotado por
        /// consolidación del analista para no engordar el prompt.
        /// </summary>
        public string? ActiveLearningsJson { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
