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

        public static (string? Text, string? ChatId) ParseIncomingMessage(JsonElement body)
        {
            try
            {
                if (!body.TryGetProperty("message", out var message))
                    return (null, null);

                string? text = null;
                if (message.TryGetProperty("text", out var textProp))
                    text = textProp.GetString();

                string? chatId = null;
                if (message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var idProp))
                    chatId = idProp.GetRawText(); // numeric, but we store as string

                return (text, chatId);
            }
            catch
            {
                return (null, null);
            }
        }
    }
}
