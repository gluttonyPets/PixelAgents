namespace Server.Models
{
    /// <summary>
    /// Credencial reutilizable de mensajeria (Telegram). Se define una vez en la
    /// seccion "Mensajeria" y los proyectos la referencian por Id, en lugar de
    /// repetir el bot token y el chat en cada proyecto.
    /// </summary>
    public class MessagingConnection
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = default!;

        /// <summary>Proveedor de mensajeria. Por ahora solo "telegram".</summary>
        public string Provider { get; set; } = default!;

        /// <summary>Bot token de Telegram (obtenido de BotFather).</summary>
        public string BotToken { get; set; } = default!;

        /// <summary>Chat Id de Telegram al que se envian los mensajes.</summary>
        public string ChatId { get; set; } = default!;

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
