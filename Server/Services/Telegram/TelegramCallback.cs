namespace Server.Services.Telegram
{
    /// <summary>
    /// Formato del <c>callback_data</c> de los botones: <c>accion|token|payload</c>.
    /// El token identifica la correlacion (la pregunta concreta) a la que pertenece el boton,
    /// de modo que pulsar "Continuar" en un mensaje antiguo no puede resolver la interaccion
    /// de otro pipeline que estuviera abierta en el mismo chat.
    ///
    /// Telegram limita callback_data a 64 bytes, de ahi los prefijos cortos y el token de 8.
    /// Los callbacks antiguos (sin separador) se siguen entendiendo: token = null y se cae al
    /// emparejamiento por chat, como antes.
    /// </summary>
    public record TelegramCallback(string Action, string? Token, string? Payload)
    {
        public const string Continue = "cont";
        public const string Abort = "abort";
        public const string Restart = "restart";
        public const string Edit = "edit";
        public const string NextExecution = "next";
        public const string Pick = "pick";
        public const string EditProvider = "eprov";
        public const string EditModel = "emod";

        private const char Separator = '|';

        public static string Build(string action, string? token, string? payload = null)
        {
            var data = string.IsNullOrEmpty(payload)
                ? $"{action}{Separator}{token}"
                : $"{action}{Separator}{token}{Separator}{payload}";
            return data;
        }

        /// <summary>
        /// Parsea el callback_data. Si no lleva separador se devuelve tal cual como accion
        /// (compatibilidad con mensajes enviados antes de este cambio) y sin token.
        /// </summary>
        public static TelegramCallback Parse(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return new TelegramCallback("", null, null);

            var parts = data.Split(Separator, 3);
            if (parts.Length == 1)
                return new TelegramCallback(parts[0], null, null);

            var token = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
            var payload = parts.Length > 2 ? parts[2] : null;
            return new TelegramCallback(parts[0], token, payload);
        }

        /// <summary>
        /// Token corto e imprevisible para identificar una interaccion en el chat y en los botones.
        /// 8 hex (32 bits) es de sobra: solo tiene que ser unico entre las interacciones abiertas.
        /// </summary>
        public static string NewToken() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }
}
