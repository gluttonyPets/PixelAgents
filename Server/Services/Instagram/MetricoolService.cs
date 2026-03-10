using System.Text;
using System.Text.Json;

namespace Server.Services.Instagram
{
    public class BufferConfig
    {
        public string ApiKey { get; set; } = default!;
        public string ChannelId { get; set; } = default!;
    }

    public class BufferService
    {
        private readonly HttpClient _http;
        private const string ApiUrl = "https://api.buffer.com";

        public BufferService(HttpClient http)
        {
            _http = http;
        }

        private static async Task EnsureGraphQLSuccessAsync(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.GetArrayLength() > 0)
            {
                var messages = new List<string>();
                foreach (var err in errors.EnumerateArray())
                {
                    if (err.TryGetProperty("message", out var msg))
                        messages.Add(msg.GetString() ?? "Unknown error");
                }
                throw new HttpRequestException(
                    $"Buffer GraphQL error: {string.Join("; ", messages)}");
            }
        }

        /// <summary>
        /// Creates and schedules a post on Buffer using inline GraphQL (no variables).
        /// Buffer's schema uses enums without quotes for schedulingType and mode.
        /// </summary>
        public async Task<string> PublishAsync(
            BufferConfig config,
            string text,
            List<string>? mediaUrls = null)
        {
            // Escape text for GraphQL string literal
            var escapedText = text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            var dueAt = DateTime.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // Build assets block if media provided
            var assetsBlock = "";
            if (mediaUrls is not null && mediaUrls.Count > 0)
            {
                var imageEntries = mediaUrls.Select(url =>
                {
                    var escapedUrl = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    return $"{{ url: \"{escapedUrl}\" }}";
                });
                assetsBlock = $", assets: {{ images: [{string.Join(", ", imageEntries)}] }}";
            }

            var mutation = $@"
                mutation {{
                    createPost(input: {{
                        text: ""{escapedText}"",
                        channelId: ""{config.ChannelId}"",
                        schedulingType: automatic,
                        mode: customSchedule,
                        dueAt: ""{dueAt}""
                        {assetsBlock}
                    }}) {{
                        ... on PostActionSuccess {{
                            post {{
                                id
                                text
                                assets {{
                                    id
                                    mimeType
                                }}
                            }}
                        }}
                        ... on MutationError {{
                            message
                        }}
                    }}
                }}";

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {config.ApiKey}");

            var body = new Dictionary<string, object> { ["query"] = mutation };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Buffer API error {(int)response.StatusCode}: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            await EnsureGraphQLSuccessAsync(json);

            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("data").GetProperty("createPost");

            // Check for mutation-level error (MutationError)
            if (result.TryGetProperty("message", out var errMsg))
                throw new HttpRequestException($"Buffer error: {errMsg.GetString()}");

            if (result.TryGetProperty("post", out var post) &&
                post.TryGetProperty("id", out var idProp))
                return idProp.GetString() ?? "published";

            return "published";
        }

        /// <summary>
        /// Lists connected channels (profiles) from Buffer account.
        /// </summary>
        public async Task<List<BufferChannel>> GetChannelsAsync(string apiKey)
        {
            var mutation = @"
                query {
                    channels {
                        id
                        name
                        service
                    }
                }";

            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var body = new Dictionary<string, object> { ["query"] = mutation };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Buffer API error {(int)response.StatusCode}: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            await EnsureGraphQLSuccessAsync(json);

            using var doc = JsonDocument.Parse(json);
            var channels = new List<BufferChannel>();

            if (doc.RootElement.GetProperty("data").TryGetProperty("channels", out var arr))
            {
                foreach (var ch in arr.EnumerateArray())
                {
                    channels.Add(new BufferChannel
                    {
                        Id = ch.GetProperty("id").GetString() ?? "",
                        Name = ch.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Service = ch.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "",
                    });
                }
            }

            return channels;
        }
    }

    public class BufferChannel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Service { get; set; } = "";
    }
}
