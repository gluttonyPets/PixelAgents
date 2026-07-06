namespace Server.Services.Telegram
{
    /// <summary>
    /// Guarda los <c>update_id</c> de Telegram procesados recientemente para garantizar
    /// que cada update se maneje una sola vez. Telegram entrega el mismo update mas de una
    /// vez en dos situaciones que esto evita:
    ///   - Modo webhook: si el endpoint no devuelve 200 OK con suficiente rapidez (reanudar
    ///     el pipeline puede tardar), Telegram reintenta el mismo update.
    ///   - Modo polling: un update recibido pero aun no confirmado antes de un reinicio es
    ///     devuelto de nuevo por getUpdates.
    /// Sin esta proteccion el mismo boton "Continuar" se procesa dos veces y la publicacion
    /// se reenvia al chat.
    ///
    /// Las entradas caducan tras un periodo corto porque Telegram nunca reintenta un update
    /// antiguo, por lo que el conjunto se mantiene pequeno sin crecer de forma ilimitada.
    /// </summary>
    public class TelegramUpdateDeduplicator
    {
        private readonly TimeSpan _retention;
        private readonly Dictionary<string, DateTime> _seen = new();
        private readonly object _gate = new();

        public TelegramUpdateDeduplicator() : this(TimeSpan.FromMinutes(10)) { }

        public TelegramUpdateDeduplicator(TimeSpan retention) => _retention = retention;

        /// <summary>
        /// Registra la clave del update de forma atomica. Devuelve <c>true</c> cuando el update
        /// es nuevo (el llamador debe procesarlo) y <c>false</c> cuando ya se proceso (el llamador
        /// debe ignorarlo para no duplicar el envio).
        /// </summary>
        public bool TryMarkProcessed(string updateKey) => TryMarkProcessed(updateKey, DateTime.UtcNow);

        /// <summary>
        /// Sobrecarga con marca de tiempo explicita para pruebas deterministas.
        /// </summary>
        public bool TryMarkProcessed(string updateKey, DateTime nowUtc)
        {
            if (string.IsNullOrEmpty(updateKey))
                return true; // sin id sobre el que deduplicar: se deja pasar

            lock (_gate)
            {
                EvictExpired(nowUtc);
                if (_seen.ContainsKey(updateKey))
                    return false;
                _seen[updateKey] = nowUtc;
                return true;
            }
        }

        private void EvictExpired(DateTime nowUtc)
        {
            if (_seen.Count == 0) return;

            var cutoff = nowUtc - _retention;
            List<string>? stale = null;
            foreach (var kv in _seen)
            {
                if (kv.Value < cutoff)
                    (stale ??= new List<string>()).Add(kv.Key);
            }

            if (stale is null) return;
            foreach (var key in stale)
                _seen.Remove(key);
        }
    }
}
