using System.Text;
using System.Text.Json;

namespace Server.Services.Telegram
{
    public class TelegramConfig
    {
        public string BotToken { get; set; } = default!;
        public string ChatId { get; set; } = default!;
    }

    public class TelegramService
    {
        private readonly HttpClient _http;
        private const string ApiBase = "https://api.telegram.org";

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

        public async Task SendTextMessageAsync(TelegramConfig config, string text)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendMessage";

            var payload = new
            {
                chat_id = config.ChatId,
                text
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
        }

        /// <summary>
        /// Sends a text message with inline keyboard buttons (options for the user to select).
        /// Each option becomes a button row. callback_data = option text.
        /// </summary>
        public async Task SendTextMessageWithOptionsAsync(TelegramConfig config, string text, List<string> options)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendMessage";

            var inlineKeyboard = options.Select(opt => new[]
            {
                new { text = opt, callback_data = opt }
            }).ToArray();

            var payload = new
            {
                chat_id = config.ChatId,
                text,
                reply_markup = new { inline_keyboard = inlineKeyboard }
            };

            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            await EnsureSuccessAsync(response);
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

        public async Task SendPhotoAsync(TelegramConfig config, byte[] photoBytes, string fileName, string? caption = null)
        {
            var url = $"{ApiBase}/bot{config.BotToken}/sendPhoto";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(config.ChatId), "chat_id");

            var fileContent = new ByteArrayContent(photoBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "photo", fileName);

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
        /// Parses an incoming Telegram update. Handles both regular messages and callback_query (button presses).
        /// Returns (Text, ChatId, CallbackQueryId). CallbackQueryId is non-null for button presses.
        /// </summary>
        public static (string? Text, string? ChatId, string? CallbackQueryId) ParseIncomingUpdate(JsonElement body)
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
                    if (callbackQuery.TryGetProperty("message", out var cbMsg)
                        && cbMsg.TryGetProperty("chat", out var cbChat)
                        && cbChat.TryGetProperty("id", out var cbIdProp))
                        cbChatId = cbIdProp.GetRawText();

                    string? cbId = null;
                    if (callbackQuery.TryGetProperty("id", out var cbIdField))
                        cbId = cbIdField.GetString();

                    return (cbData, cbChatId, cbId);
                }

                // Handle regular text message
                if (!body.TryGetProperty("message", out var message))
                    return (null, null, null);

                string? text = null;
                if (message.TryGetProperty("text", out var textProp))
                    text = textProp.GetString();

                string? chatId = null;
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idProp))
                    chatId = idProp.GetRawText();

                return (text, chatId, null);
            }
            catch
            {
                return (null, null, null);
            }
        }
    }
}
