using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Server.Services.Instagram
{
    public class MetricoolConfig
    {
        public string UserToken { get; set; } = default!;
        public string UserId { get; set; } = default!;
        public string BlogId { get; set; } = default!;
        public string Timezone { get; set; } = "Europe/Madrid";
    }

    public class MetricoolService
    {
        private readonly HttpClient _http;
        private const string ApiBase = "https://app.metricool.com/api";

        public MetricoolService(HttpClient http)
        {
            _http = http;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new HttpRequestException(
                    $"Metricool API error {(int)response.StatusCode}: {body}");
            }
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url, MetricoolConfig config)
        {
            var separator = url.Contains('?') ? "&" : "?";
            var fullUrl = $"{url}{separator}userId={config.UserId}&blogId={config.BlogId}";
            var request = new HttpRequestMessage(method, fullUrl);
            request.Headers.Add("X-Mc-Auth", config.UserToken);
            return request;
        }

        /// <summary>
        /// Normalizes a media URL so it's hosted on Metricool servers.
        /// Returns the normalized URL to use in post creation.
        /// </summary>
        public async Task<string> NormalizeMediaUrlAsync(MetricoolConfig config, string mediaUrl)
        {
            var url = $"{ApiBase}/actions/normalize/image/url?url={Uri.EscapeDataString(mediaUrl)}";
            var request = CreateRequest(HttpMethod.Get, url, config);

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // The API returns the normalized URL
            if (doc.RootElement.TryGetProperty("url", out var urlProp))
                return urlProp.GetString()!;

            return json.Trim('"');
        }

        /// <summary>
        /// Publishes a post to Instagram via Metricool scheduler.
        /// </summary>
        /// <param name="config">Metricool credentials</param>
        /// <param name="text">Caption/text for the post</param>
        /// <param name="mediaUrls">List of normalized media URLs</param>
        /// <param name="postType">Type: "post", "reel", "story"</param>
        /// <param name="publishNow">If true, publishes immediately; if false, schedules for scheduledDate</param>
        /// <param name="scheduledDate">Optional scheduled date (ignored if publishNow)</param>
        public async Task<string> PublishAsync(
            MetricoolConfig config,
            string text,
            List<string>? mediaUrls = null,
            string postType = "post",
            bool publishNow = true,
            DateTime? scheduledDate = null)
        {
            var url = $"{ApiBase}/v2/scheduler/posts";
            var request = CreateRequest(HttpMethod.Post, url, config);

            var now = DateTime.UtcNow;
            var pubDate = publishNow
                ? now.AddMinutes(1) // Schedule 1 minute from now for "immediate" publish
                : (scheduledDate ?? now.AddHours(1));

            var body = new Dictionary<string, object>
            {
                ["publicationDate"] = new
                {
                    dateTime = pubDate.ToString("yyyy-MM-ddTHH:mm:ss"),
                    timezone = config.Timezone
                },
                ["creationDate"] = new
                {
                    dateTime = now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    timezone = config.Timezone
                },
                ["text"] = text,
                ["firstCommentText"] = "",
                ["providers"] = new[] { new { network = "instagram" } },
                ["autoPublish"] = true,
                ["saveExternalMediaFiles"] = false,
                ["shortener"] = false,
                ["draft"] = false,
                ["instagramData"] = new { autoPublish = true },
                ["hasNotReadNotes"] = false,
                ["creatorUserId"] = config.UserId
            };

            // Add media if provided
            if (mediaUrls is not null && mediaUrls.Count > 0)
            {
                body["media"] = mediaUrls.Select(u => new { url = u }).ToList();
            }

            // Set Instagram-specific type based on postType
            var igData = new Dictionary<string, object> { ["autoPublish"] = true };
            if (postType == "reel")
                igData["type"] = "REELS";
            else if (postType == "story")
                igData["type"] = "STORIES";
            else
                igData["type"] = "POST";

            body["instagramData"] = igData;

            var jsonContent = JsonSerializer.Serialize(body);
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);

            var responseBody = await response.Content.ReadAsStringAsync();
            return responseBody;
        }

        /// <summary>
        /// Uploads a local file to a temporary public URL, normalizes it via Metricool,
        /// and returns the normalized URL ready for publishing.
        /// </summary>
        public async Task<string> UploadAndNormalizeAsync(
            MetricoolConfig config, byte[] fileBytes, string contentType, string fileName)
        {
            // Upload to Metricool's normalize endpoint using multipart form
            var url = $"{ApiBase}/actions/normalize/image/upload";
            var request = CreateRequest(HttpMethod.Post, url, config);

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "file", fileName);

            request.Content = form;

            var response = await _http.SendAsync(request);
            await EnsureSuccessAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("url", out var urlProp))
                return urlProp.GetString()!;

            return json.Trim('"');
        }
    }
}
