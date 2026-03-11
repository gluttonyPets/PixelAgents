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
        public async Task<BufferPublishResult> PublishAsync(
            BufferConfig config,
            string text,
            List<ClassifiedMedia>? media = null,
            string publishType = "post")
        {
            // Escape text for GraphQL string literal
            var escapedText = text
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            var dueAt = DateTime.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // Build assets block if media provided, separating images from videos
            var assetsBlock = "";
            if (media is not null && media.Count > 0)
            {
                var images = media.Where(m => m.Kind == MediaKind.Image).ToList();
                var videos = media.Where(m => m.Kind == MediaKind.Video).ToList();
                var parts = new List<string>();

                if (images.Count > 0)
                {
                    var imageEntries = images.Select(m =>
                    {
                        var escapedUrl = m.Url.Replace("\\", "\\\\").Replace("\"", "\\\"");
                        return $"{{ url: \"{escapedUrl}\" }}";
                    });
                    parts.Add($"images: [{string.Join(", ", imageEntries)}]");
                }

                if (videos.Count > 0)
                {
                    var escapedUrl = videos[0].Url.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    parts.Add($"video: {{ url: \"{escapedUrl}\" }}");
                }

                if (parts.Count > 0)
                    assetsBlock = $", assets: {{ {string.Join(", ", parts)} }}";
            }

            // Validate publishType to prevent injection
            var validTypes = new HashSet<string> { "post", "reel", "story" };
            var igType = validTypes.Contains(publishType) ? publishType : "post";

            var mutation = $@"
                mutation {{
                    createPost(input: {{
                        text: ""{escapedText}"",
                        channelId: ""{config.ChannelId}"",
                        metadata: {{ instagram: {{ type: {igType}, shouldShareToFeed: true }} }},
                        schedulingType: automatic,
                        mode: customScheduled,
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
            var requestBody = JsonSerializer.Serialize(body);
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Buffer API error {(int)response.StatusCode}: {json}");
            }

            await EnsureGraphQLSuccessAsync(json);

            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("data").GetProperty("createPost");

            // Check for mutation-level error (MutationError)
            if (result.TryGetProperty("message", out var errMsg))
                throw new HttpRequestException($"Buffer error: {errMsg.GetString()}");

            var postId = "published";
            if (result.TryGetProperty("post", out var post) &&
                post.TryGetProperty("id", out var idProp))
                postId = idProp.GetString() ?? "published";

            return new BufferPublishResult
            {
                PostId = postId,
                RequestBody = requestBody,
                ResponseBody = json,
                StatusCode = (int)response.StatusCode
            };
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

    public enum MediaKind { Image, Video }

    public class ClassifiedMedia
    {
        public string Url { get; set; } = "";
        public MediaKind Kind { get; set; }
    }

    public class BufferPublishResult
    {
        public string PostId { get; set; } = "";
        public string RequestBody { get; set; } = "";
        public string ResponseBody { get; set; } = "";
        public int StatusCode { get; set; }
    }
}
