namespace Server.Models
{
    /// <summary>
    /// Una version guardada del prompt de un modulo. Cada vez que el prompt de un
    /// modulo (systemPrompt o imagePrompt de su Configuration) cambia al guardar,
    /// se registra una fila con el nuevo contenido, formando un historial editable
    /// y restaurable por modulo y campo. Tenant-scoped.
    /// </summary>
    public class PromptVersion
    {
        public Guid Id { get; set; }
        public Guid AiModuleId { get; set; }

        /// <summary>Campo del prompt: "systemPrompt" o "imagePrompt".</summary>
        public string Field { get; set; } = "";

        /// <summary>Contenido del prompt en ese momento.</summary>
        public string Content { get; set; } = "";

        /// <summary>Origen del cambio: "edit" (guardado normal) o "baseline"
        /// (valor previo capturado la primera vez que se registra historial).</summary>
        public string Source { get; set; } = "edit";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public AiModule? AiModule { get; set; }
    }
}
