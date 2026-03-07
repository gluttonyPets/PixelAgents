using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.Services.WhatsApp
{
    public class WhatsAppConfig
    {
        public string PhoneNumberId { get; set; } = default!;
        public string AccessToken { get; set; } = default!;
        public string RecipientNumber { get; set; } = default!;
        public string WebhookVerifyToken { get; set; } = default!;
    }

    public class WhatsAppService
    {
        private readonly HttpClient _http;
        private const string GraphApiBase = "https://graph.facebook.com/v21.0";

        public WhatsAppService(HttpClient http)
        {
            _http = http;
        }

        /// <summary>Strips +, spaces, dashes from phone number so it's digits only.</summary>
        private static string NormalizePhone(string phone) =>
            Regex.Replace(phone, @"[^\d]", "");

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"WhatsApp API error {(int)response.StatusCode}: {body}");
            }
        }

        public async Task SendTextMessageAsync(WhatsAppConfig config, string text)
        {
            var url = $"{GraphApiBase}/{config.PhoneNumberId}/messages";

            var payload = new
            {
                messaging_product = "whatsapp",
                to = NormalizePhone(config.RecipientNumber),
                type = "text",
                text = new { body = text }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);
        }

        public async Task<string> UploadMediaAsync(WhatsAppConfig config, byte[] fileBytes, string contentType, string fileName)
        {
            var url = $"{GraphApiBase}/{config.PhoneNumberId}/media";

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("whatsapp"), "messaging_product");
            form.Add(new StringContent(contentType), "type");

            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "file", fileName);

            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("id").GetString()!;
        }

        public async Task SendImageMessageAsync(WhatsAppConfig config, string mediaId, string? caption = null)
        {
            var url = $"{GraphApiBase}/{config.PhoneNumberId}/messages";

            var imageObj = new Dictionary<string, object> { ["id"] = mediaId };
            if (!string.IsNullOrWhiteSpace(caption))
                imageObj["caption"] = caption;

            var payload = new
            {
                messaging_product = "whatsapp",
                to = NormalizePhone(config.RecipientNumber),
                type = "image",
                image = imageObj
            };

            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken);

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);
        }

        public static (bool Valid, string? Challenge) VerifyWebhook(
            string expectedVerifyToken, string? hubMode, string? hubVerifyToken, string? hubChallenge)
        {
            if (hubMode == "subscribe" && hubVerifyToken == expectedVerifyToken)
                return (true, hubChallenge);
            return (false, null);
        }

        public static (string? Text, string? SenderPhone) ParseIncomingMessage(JsonElement body)
        {
            try
            {
                var entry = body.GetProperty("entry")[0];
                var changes = entry.GetProperty("changes")[0];
                var value = changes.GetProperty("value");

                if (!value.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
                    return (null, null);

                var msg = messages[0];
                var senderPhone = msg.GetProperty("from").GetString();

                string? text = null;
                if (msg.TryGetProperty("text", out var textObj))
                    text = textObj.GetProperty("body").GetString();

                return (text, senderPhone);
            }
            catch
            {
                return (null, null);
            }
        }
    }
}
