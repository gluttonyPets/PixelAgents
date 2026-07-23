namespace Server.Models
{
    /// <summary>
    /// Feedback humano sobre una ejecucion concreta. La primera fuente es Telegram:
    /// cuando el usuario aborta un pipeline, el bot le pide un comentario de "que ha
    /// ido mal" y se guarda aqui como feedback negativo. Sirve de base para el bucle
    /// de aprendizaje (ver el historial en la web y, mas adelante, alimentar ejemplos
    /// o ajustes de prompt). Tenant-scoped.
    /// </summary>
    public class ExecutionFeedback
    {
        public Guid Id { get; set; }

        /// <summary>Ejecucion a la que se refiere el feedback.</summary>
        public Guid ExecutionId { get; set; }

        /// <summary>Step concreto senalado, si se conoce. Null = feedback a nivel de ejecucion.</summary>
        public Guid? StepExecutionId { get; set; }

        /// <summary>Modulo del pipeline senalado, si se conoce. Util para agregar feedback por modulo.</summary>
        public Guid? ProjectModuleId { get; set; }

        /// <summary>Valoracion: "negative" o "positive". El abort de Telegram genera "negative".</summary>
        public string Rating { get; set; } = "negative";

        /// <summary>Comentario libre del usuario explicando que ha ido mal (o bien).</summary>
        public string? Comment { get; set; }

        /// <summary>Origen del feedback: "telegram_abort", "web", etc.</summary>
        public string Source { get; set; } = "telegram_abort";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public ProjectExecution? Execution { get; set; }
    }
}
