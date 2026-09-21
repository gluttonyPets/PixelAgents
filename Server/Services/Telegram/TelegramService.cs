using System.Text;
using System.Text.Json;

namespace Server.Services.Telegram
{
    public class TelegramConfig
    {
        public string BotToken { get; set; } = default!;
        public string ChatId { get; set; } = default!;

        /// <summary>
        /// Forum topic (hilo) al que van los mensajes de esta ejecucion. Null = raiz del chat.
        /// Telegram solo admite este campo en supergrupos con "Temas" activados.
        /// </summary>
        public long? MessageThreadId { get; set; }
    }

    /// <summary>
    /// Datos utiles de un update entrante. Ademas del texto y el chat, arrastra las claves
    /// que permiten saber A QUE pregunta responde el usuario cuando hay varias abiertas:
    /// el hilo (<see cref="MessageThreadId"/>) y el mensaje citado (<see cref="ReplyToMessageId"/>).
    /// </summary>
    public record IncomingTelegramUpdate(
        string? Text,
        string? ChatId,
        string? CallbackQueryId,
        DateTime? MessageDate,
        long? MessageId,
        long? ReplyToMessageId,
        long? MessageThreadId);

    public class TelegramService
    {
        private readonly HttpClient _http;
        private const string ApiBase = "https://api.telegram.org";

        // Cache de "este chat admite hilos": getChat no cambia en caliente y se consultaria
        // en cada interaccion. Compartida por proceso, clave botToken:chatId.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> ForumChatCache = new();

        public TelegramService(HttpClient http)
        {
            _http = http;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Telegram API error {(int)response.StatusCode}: {body}");
            }
        }

        /// <summary>
        /// Envia un mensaje de texto y devuelve su message_id (null si Telegram no lo devuelve).
        /// El message_id es la clave con la que despues se correlaciona una respuesta citada.
        /// </summary>
        public async Task<long?> SendTextMessageAsync(TelegramConfig config, string text)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendMessage";

            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = config.ChatId,
                ["text"] = text,
            };
            if (config.MessageThreadId is { } threadId)
                payload["message_thread_id"] = threadId;

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
            return await ReadMessageIdAsync(response);
        }

        /// <summary>
        /// Sends a text message with inline keyboard buttons.
        /// Each (label, callbackData) becomes a button row.
        /// </summary>
        public async Task<long?> SendTextMessageWithOptionsAsync(TelegramConfig config, string text, List<(string Label, string CallbackData)> options)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendMessage";

            var inlineKeyboard = options.Select(opt => new[]
            {
                new { text = opt.Label, callback_data = opt.CallbackData }
            }).ToArray();

            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = config.ChatId,
                ["text"] = text,
                ["reply_markup"] = new { inline_keyboard = inlineKeyboard },
            };
            if (config.MessageThreadId is { } threadId)
                payload["message_thread_id"] = threadId;

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
            return await ReadMessageIdAsync(response);
        }

        /// <summary>
        /// Answers a callback query (removes the "loading" state from the pressed button).
        /// </summary>
        public async Task AnswerCallbackQueryAsync(string botToken, string callbackQueryId)
        {
            var url = $"{ApiBase}/bot{botToken}/answerCallbackQuery";
            var payload = new { callback_query_id = callbackQueryId };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Quita el teclado inline de un mensaje ya resuelto para que nadie pueda pulsar
        /// botones de una interaccion cerrada (y acabar respondiendo al pipeline equivocado).
        /// </summary>
        public async Task ClearMessageKeyboardAsync(string botToken, string chatId, long messageId)
        {
            var url = $"{ApiBase}/bot{botToken}/editMessageReplyMarkup";
            var payload = new
            {
                chat_id = chatId,
                message_id = messageId,
                reply_markup = new { inline_keyboard = Array.Empty<object[]>() }
            };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Indica si el chat es un supergrupo con "Temas" activados, unico caso en el que
        /// Telegram permite crear hilos (forum topics). El resultado se cachea porque no
        /// cambia en caliente y esto se consulta en cada interaccion.
        /// </summary>
        public async Task<bool> IsForumChatAsync(string botToken, string chatId)
        {
            var cacheKey = $"{botToken}:{chatId}";
            if (ForumChatCache.TryGetValue(cacheKey, out var cached))
                return cached;

            bool isForum;
            try
            {
                var url = $"{ApiBase}/bot{botToken}/getChat?chat_id={Uri.EscapeDataString(chatId)}";
                var response = await _http.GetAsync(url);
                await EnsureSuccessAsync(response);
                var body = await response.Content.ReadAsStringAsync();
                var root = JsonDocument.Parse(body).RootElement;
                isForum = root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("is_forum", out var forumProp)
                    && forumProp.ValueKind == JsonValueKind.True;
            }
            catch (Exception ex)
            {
                // Sin permisos o chat inaccesible: se trabaja sin hilos (fallback por token).
                Console.WriteLine($"[TG] getChat fallo para {chatId}: {ex.Message}");
                isForum = false;
            }

            ForumChatCache[cacheKey] = isForum;
            return isForum;
        }

        /// <summary>
        /// Crea un hilo (forum topic) en el chat y devuelve su message_thread_id.
        /// Devuelve null si el bot no puede crearlo (sin permiso can_manage_topics, por ejemplo);
        /// en ese caso el llamante sigue funcionando sin hilos.
        /// </summary>
        public async Task<long?> CreateForumTopicAsync(string botToken, string chatId, string name)
        {
            try
            {
                var url = $"{ApiBase}/bot{botToken}/createForumTopic";
                // Telegram limita el nombre del topic a 128 caracteres.
                var safeName = name.Length > 128 ? name[..128] : name;
                var payload = new { chat_id = chatId, name = safeName };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(url, content);
                await EnsureSuccessAsync(response);

                var body = await response.Content.ReadAsStringAsync();
                var root = JsonDocument.Parse(body).RootElement;
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("message_thread_id", out var idProp)
                    && idProp.TryGetInt64(out var threadId))
                    return threadId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG] createForumTopic fallo para {chatId}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Cierra el hilo de una ejecucion terminada. Best-effort: si falla, el hilo se queda
        /// abierto pero nada mas se rompe.
        /// </summary>
        public async Task CloseForumTopicAsync(string botToken, string chatId, long messageThreadId)
        {
            try
            {
                var url = $"{ApiBase}/bot{botToken}/closeForumTopic";
                var payload = new { chat_id = chatId, message_thread_id = messageThreadId };
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(url, content);
                await EnsureSuccessAsync(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TG] closeForumTopic fallo para {chatId}/{messageThreadId}: {ex.Message}");
            }
        }

        private static async Task<long?> ReadMessageIdAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync();
                var root = JsonDocument.Parse(body).RootElement;
                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("message_id", out var idProp)
                    && idProp.TryGetInt64(out var messageId))
                    return messageId;
            }
            catch { /* el envio ya fue bien; no tener el id solo degrada la correlacion por cita */ }
            return null;
        }

        public async Task SendPhotoAsync(TelegramConfig config, byte[] photoBytes, string fileName, string? caption = null)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendPhoto";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(config.ChatId), "chat_id");
            if (config.MessageThreadId is { } photoThread)
                form.Add(new StringContent(photoThread.ToString()), "message_thread_id");

            var fileContent = new ByteArrayContent(photoBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "photo", fileName);

            if (!string.IsNullOrWhiteSpace(caption))
                form.Add(new StringContent(caption), "caption");

            var response = await _http.PostAsync(url, form);
            await EnsureSuccessAsync(response);
        }

        public async Task SendVideoAsync(TelegramConfig config, byte[] videoBytes, string fileName, string? caption = null)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendVideo";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(config.ChatId), "chat_id");
            if (config.MessageThreadId is { } videoThread)
                form.Add(new StringContent(videoThread.ToString()), "message_thread_id");

            var fileContent = new ByteArrayContent(videoBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
            form.Add(fileContent, "video", fileName);

            if (!string.IsNullOrWhiteSpace(caption))
                form.Add(new StringContent(caption), "caption");

            var response = await _http.PostAsync(url, form);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Registers a webhook URL with Telegram Bot API.
        /// Call this once when saving Telegram config.
        /// </summary>
        public async Task SetWebhookAsync(string botToken, string webhookUrl)
        {
            var url = $"{ApiBase}/bot{botToken}/setWebhook";
            var payload = new { url = webhookUrl };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Removes the webhook so Telegram allows getUpdates (long polling).
        /// </summary>
        public async Task DeleteWebhookAsync(string botToken)
        {
            var url = $"{ApiBase}/bot{botToken}/deleteWebhook";
            var response = await _http.PostAsync(url, null);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Retrieves the current webhook info from Telegram.
        /// </summary>
        public async Task<JsonElement> GetWebhookInfoAsync(string botToken)
        {
            var url = $"{ApiBase}/bot{botToken}/getWebhookInfo";
            var response = await _http.GetAsync(url);
            await EnsureSuccessAsync(response);
            var body = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(body).RootElement;
        }

        /// <summary>
        /// Extracts the top-level <c>update_id</c> from an incoming Telegram update.
        /// Every Update object carries this field and it is unique (and increasing) per bot,
        /// so it is the natural key for deduplicating retried/redelivered updates.
        /// Returns null when the field is missing or not a number.
        /// </summary>
        public static long? GetUpdateId(JsonElement body)
        {
            if (body.TryGetProperty("update_id", out var idProp)
                && idProp.ValueKind == JsonValueKind.Number
                && idProp.TryGetInt64(out var id))
                return id;
            return null;
        }

        /// <summary>
        /// Parsea un update entrante (mensaje normal o pulsacion de boton).
        /// Ademas del texto conserva las claves de correlacion: el message_id del mensaje del bot
        /// sobre el que se ha pulsado / al que se responde y el hilo (topic) en el que llega.
        /// </summary>
        public static IncomingTelegramUpdate ParseIncomingUpdate(JsonElement body)
        {
            try
            {
                // Handle callback_query (inline keyboard button press)
                if (body.TryGetProperty("callback_query", out var callbackQuery))
                {
                    string? cbData = null;
                    if (callbackQuery.TryGetProperty("data", out var dataProp))
                        cbData = dataProp.GetString();

                    string? cbChatId = null;
                    long? cbMessageId = null;
                    long? cbThreadId = null;
                    if (callbackQuery.TryGetProperty("message", out var cbMsg))
                    {
                        if (cbMsg.TryGetProperty("chat", out var cbChat)
                            && cbChat.TryGetProperty("id", out var cbIdProp))
                            cbChatId = cbIdProp.GetRawText();

                        if (cbMsg.TryGetProperty("message_id", out var cbMsgIdProp)
                            && cbMsgIdProp.TryGetInt64(out var cbMsgId))
                            cbMessageId = cbMsgId;

                        if (cbMsg.TryGetProperty("message_thread_id", out var cbThreadProp)
                            && cbThreadProp.TryGetInt64(out var cbThread))
                            cbThreadId = cbThread;
                    }

                    string? cbId = null;
                    if (callbackQuery.TryGetProperty("id", out var cbIdField))
                        cbId = cbIdField.GetString();

                    // El boton pulsado identifica el mensaje: se usa como "respuesta citada"
                    // para saber a que interaccion pertenece la pulsacion.
                    return new IncomingTelegramUpdate(cbData, cbChatId, cbId, null, cbMessageId, cbMessageId, cbThreadId);
                }

                // Handle regular text message
                if (!body.TryGetProperty("message", out var message))
                    return new IncomingTelegramUpdate(null, null, null, null, null, null, null);

                string? text = null;
                if (message.TryGetProperty("text", out var textProp))
                    text = textProp.GetString();

                string? chatId = null;
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idProp))
                    chatId = idProp.GetRawText();

                // Extract message date (Unix timestamp) to validate timing
                DateTime? messageDate = null;
                if (message.TryGetProperty("date", out var dateProp) && dateProp.TryGetInt64(out var unixDate))
                    messageDate = DateTimeOffset.FromUnixTimeSeconds(unixDate).UtcDateTime;

                long? messageId = null;
                if (message.TryGetProperty("message_id", out var msgIdProp) && msgIdProp.TryGetInt64(out var mid))
                    messageId = mid;

                // Respuesta citada: identifica sin ambiguedad la pregunta del bot que se contesta.
                long? replyToMessageId = null;
                if (message.TryGetProperty("reply_to_message", out var replyTo)
                    && replyTo.TryGetProperty("message_id", out var replyIdProp)
                    && replyIdProp.TryGetInt64(out var replyId))
                    replyToMessageId = replyId;

                // Hilo (forum topic) del que procede el mensaje.
                long? threadId = null;
                if (message.TryGetProperty("message_thread_id", out var threadProp)
                    && threadProp.TryGetInt64(out var thread))
                    threadId = thread;

                // En un topic, Telegram marca el primer mensaje del hilo como reply_to_message
                // aunque el usuario no haya citado nada: eso no es una cita real.
                if (replyToMessageId is not null && replyToMessageId == threadId)
                    replyToMessageId = null;

                return new IncomingTelegramUpdate(text, chatId, null, messageDate, messageId, replyToMessageId, threadId);
            }
            catch
            {
                return new IncomingTelegramUpdate(null, null, null, null, null, null, null);
            }
        }

    }
}
