namespace Server.Models
{
    public class TelegramCorrelation
    {
        public Guid Id { get; set; }
        public Guid ExecutionId { get; set; }
        public Guid ProjectModuleId { get; set; }

        /// <summary>
        /// Project this correlation belongs to. Only set for correlations that are not tied
        /// to a running execution — currently the "awaiting_planning" flow, where a scheduled
        /// run found no prompt to use and asks the user (via Telegram) to plan new topics.
        /// </summary>
        public Guid? ProjectId { get; set; }

        public string TenantDbName { get; set; } = default!;
        public string ChatId { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
        public bool IsResolved { get; set; }
        /// <summary>
        /// Tracks the correlation state: "waiting" (default), "awaiting_restart" (waiting for restart clarification text),
        /// "edit_select_model" (waiting for the user to pick a model for an edit),
        /// "edit_awaiting_prompt" (waiting for the user's edit prompt text),
        /// "awaiting_abort_feedback" (pipeline abortado; esperando el comentario del usuario sobre qué ha ido mal),
        /// "awaiting_planning" (a scheduled run had no prompt and is waiting for the user to describe a new planning),
        /// "queued" (message not yet sent — waiting for a prior interaction to resolve first).
        /// </summary>
        public string State { get; set; } = "waiting";

        /// <summary>
        /// JSON-serialized message data for queued interactions (State="queued").
        /// Contains message text, image file paths, button config, etc.
        /// Null when the message has already been sent.
        /// </summary>
        public string? QueuedMessageData { get; set; }

        /// <summary>
        /// JSON-serialized intermediate state for the out-of-band "Edit" loop (State starts with "edit_").
        /// Stores the detected output type (image/text) and the AiModule selected by the user.
        /// Cleared when the edit cycle finishes and the correlation goes back to "waiting".
        /// </summary>
        public string? EditStateData { get; set; }

        /// <summary>
        /// Token corto (8 hex) que identifica esta interaccion en el chat. Viaja dentro del
        /// callback_data de los botones y se muestra al usuario como etiqueta (#A3F2), de modo
        /// que una respuesta siempre se pueda atribuir a la pregunta correcta aunque haya
        /// varios pipelines esperando en el mismo chat. Null en correlaciones antiguas.
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// message_id del mensaje que el bot envio con esta pregunta. Si el usuario responde
        /// citandolo, el update trae reply_to_message.message_id y la correlacion es exacta.
        /// Tambien sirve para retirar el teclado cuando la interaccion se cierra.
        /// </summary>
        public long? BotMessageId { get; set; }

        /// <summary>
        /// Hilo (forum topic) de la ejecucion, cuando el chat es un supergrupo con Temas.
        /// Todos los mensajes de la ejecucion van a este hilo y todo lo que llegue por el
        /// pertenece a ella, sin ambiguedad posible.
        /// </summary>
        public long? MessageThreadId { get; set; }

        /// <summary>
        /// Etiqueta legible ("Proyecto · Paso") para que el usuario sepa a que esta respondiendo
        /// y para los botones de desambiguacion.
        /// </summary>
        public string? Label { get; set; }
    }
}
