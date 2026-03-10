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

        private static async Task<JsonElement> SendGraphQLAsync(HttpClient http, string apiKey, string query, object? variables = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var body = new Dictionary<string, object?> { ["query"] = query };
            if (variables is not null)
                body["variables"] = variables;

            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Buffer API error {(int)response.StatusCode}: {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // Check for GraphQL errors
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.GetArrayLength() > 0)
            {
                var firstError = errors[0].GetProperty("message").GetString();
                throw new HttpRequestException($"Buffer GraphQL error: {firstError}");
            }

            return doc.RootElement.GetProperty("data").Clone();
        }

        /// <summary>
        /// Creates and schedules a post on Buffer.
        /// </summary>
        public async Task<string> PublishAsync(
            BufferConfig config,
            string text,
            List<string>? mediaUrls = null,
            string schedulingType = "automatic")
        {
            // Build the mutation with media if provided
            string mutation;
            object variables;

            if (mediaUrls is not null && mediaUrls.Count > 0)
            {
                mutation = @"
                    mutation CreatePost($input: CreatePostInput!) {
                        createPost(input: $input) {
                            ... on PostActionSuccess {
                                post { id text }
                            }
                            ... on MutationError {
                                message
                            }
                        }
                    }";

                variables = new
                {
                    input = new
                    {
                        text,
                        channelIds = new[] { config.ChannelId },
                        schedulingType,
                        assets = mediaUrls.Select(url => new { url, type = "image" }).ToArray()
                    }
                };
            }
            else
            {
                mutation = @"
                    mutation CreatePost($input: CreatePostInput!) {
                        createPost(input: $input) {
                            ... on PostActionSuccess {
                                post { id text }
                            }
                            ... on MutationError {
                                message
                            }
                        }
                    }";

                variables = new
                {
                    input = new
                    {
                        text,
                        channelIds = new[] { config.ChannelId },
                        schedulingType
                    }
                };
            }

            var data = await SendGraphQLAsync(_http, config.ApiKey, mutation, variables);
            var result = data.GetProperty("createPost");

            // Check for mutation-level error
            if (result.TryGetProperty("message", out var errMsg))
                throw new HttpRequestException($"Buffer error: {errMsg.GetString()}");

            var postId = result.GetProperty("post").GetProperty("id").GetString();
            return postId ?? "published";
        }

        /// <summary>
        /// Lists connected channels (profiles) from Buffer account.
        /// Useful for finding the channelId for config.
        /// </summary>
        public async Task<List<BufferChannel>> GetChannelsAsync(string apiKey)
        {
            var query = @"
                query GetChannels {
                    channels {
                        id
                        name
                        service
                        avatar
                    }
                }";

            var data = await SendGraphQLAsync(_http, apiKey, query);
            var channels = new List<BufferChannel>();

            if (data.TryGetProperty("channels", out var arr))
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
