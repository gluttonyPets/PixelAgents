namespace Server.Models
{
    /// <summary>
    /// Texto que el usuario ha enviado al chat sin que se pueda saber a que interaccion
    /// responde (ni cita, ni hilo, y hay varias preguntas abiertas). Se guarda mientras el
    /// bot le pregunta "¿a cual respondes?" y se aplica a la correlacion que elija.
    /// Solo hay uno vivo por chat: un texto nuevo sustituye al anterior.
    /// </summary>
    public class TelegramPendingReply
    {
        /// <summary>Chat de Telegram. Clave primaria: un pendiente por chat.</summary>
        public string ChatId { get; set; } = default!;
        public string Text { get; set; } = default!;
        public DateTime CreatedAt { get; set; }
    }
}
