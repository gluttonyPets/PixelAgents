using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai
{
    public class PexelsProvider : IAiProvider
    {
        private const string BaseUrl = "https://api.pexels.com";

        public string ProviderType => "Pexels";
        public IEnumerable<string> SupportedModuleTypes => new[] { "VideoSearch" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "VideoSearch" => await SearchVideoAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Pexels")
                };
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Pexels: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("Authorization", apiKey);
                var resp = await http.GetAsync($"{BaseUrl}/videos/popular?per_page=1");

                if (resp.IsSuccessStatusCode)
                    return (true, null);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "API Key de Pexels invalida");
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, "API Key de Pexels sin permisos");

                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"Error al validar Pexels (HTTP {(int)resp.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo conectar con Pexels: {ex.Message}");
            }
        }

        private async Task<AiResult> SearchVideoAsync(AiExecutionContext context)
        {
            var query = context.Input;
            if (string.IsNullOrWhiteSpace(query))
                return AiResult.Fail("Pexels: se necesita un texto de busqueda");

            // Read configuration
            var orientation = "landscape";
            var size = "medium";
            var perPage = 15;
            var locale = "es-ES";
            var minDuration = 0;
            var maxDuration = 0;
            var preferredQuality = "hd"; // hd, sd, uhd

            if (context.Configuration.TryGetValue("orientation", out var o))
                orientation = GetStringValue(o);
            if (context.Configuration.TryGetValue("size", out var sz))
                size = GetStringValue(sz);
            if (context.Configuration.TryGetValue("perPage", out var pp))
                perPage = GetIntValue(pp, 15);
            if (context.Configuration.TryGetValue("locale", out var loc))
                locale = GetStringValue(loc);
            if (context.Configuration.TryGetValue("minDuration", out var minD))
                minDuration = GetIntValue(minD, 0);
            if (context.Configuration.TryGetValue("maxDuration", out var maxD))
                maxDuration = GetIntValue(maxD, 0);
            if (context.Configuration.TryGetValue("preferredQuality", out var pq))
                preferredQuality = GetStringValue(pq);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("Authorization", context.ApiKey);

            // Build search URL
            var url = $"{BaseUrl}/videos/search?query={Uri.EscapeDataString(query)}&per_page={perPage}&locale={locale}";
            if (!string.IsNullOrEmpty(orientation))
                url += $"&orientation={orientation}";
            if (!string.IsNullOrEmpty(size))
                url += $"&size={size}";
            if (minDuration > 0)
                url += $"&min_duration={minDuration}";
            if (maxDuration > 0)
                url += $"&max_duration={maxDuration}";

            var resp = await http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync();
                return AiResult.Fail($"Pexels busqueda fallida (HTTP {(int)resp.StatusCode}): {errorBody}");
            }

            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0)
                return AiResult.Fail($"Pexels: no se encontraron videos para '{query}'");

            // Pick the first video (most relevant)
            var video = videos[0];
            var videoId = video.GetProperty("id").GetInt32();
            var videoDuration = video.TryGetProperty("duration", out var dur) ? dur.GetInt32() : 0;
            var videoUrl = video.TryGetProperty("url", out var vu) ? vu.GetString() ?? "" : "";
            var videoUser = "";
            if (video.TryGetProperty("user", out var user) && user.TryGetProperty("name", out var userName))
                videoUser = userName.GetString() ?? "";

            // Find the best video file based on preferred quality
            if (!video.TryGetProperty("video_files", out var files) || files.GetArrayLength() == 0)
                return AiResult.Fail("Pexels: el video no tiene archivos descargables");

            string? downloadUrl = null;
            int bestWidth = 0;
            string? bestQuality = null;

            foreach (var file in files.EnumerateArray())
            {
                var fileType = file.TryGetProperty("file_type", out var ft) ? ft.GetString() : null;
                if (fileType != "video/mp4") continue;

                var width = file.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                var quality = file.TryGetProperty("quality", out var q) ? q.GetString() : null;
                var link = file.TryGetProperty("link", out var l) ? l.GetString() : null;

                if (link is null) continue;

                var isPreferred = quality?.Equals(preferredQuality, StringComparison.OrdinalIgnoreCase) == true;

                if (downloadUrl is null || isPreferred || (!isPreferred && width > bestWidth && bestQuality != preferredQuality))
                {
                    downloadUrl = link;
                    bestWidth = width;
                    bestQuality = quality;
                }
            }

            if (downloadUrl is null)
                return AiResult.Fail("Pexels: no se encontro archivo MP4 descargable");

            // Download the video
            using var dlClient = new HttpClient();
            var dlResp = await dlClient.GetAsync(downloadUrl);
            if (!dlResp.IsSuccessStatusCode)
                return AiResult.Fail($"Pexels: error descargando video (HTTP {(int)dlResp.StatusCode})");

            var videoBytes = await dlResp.Content.ReadAsByteArrayAsync();

            var result = AiResult.OkFile(videoBytes, "video/mp4", new Dictionary<string, object>
            {
                ["source"] = "pexels",
                ["videoId"] = videoId,
                ["duration"] = videoDuration,
                ["quality"] = bestQuality ?? "unknown",
                ["width"] = bestWidth,
                ["photographer"] = videoUser,
                ["pexelsUrl"] = videoUrl,
                ["query"] = query,
                ["totalResults"] = root.TryGetProperty("total_results", out var tr) ? tr.GetInt32() : 0,
            });
            result.EstimatedCost = 0m; // Pexels is free
            return result;
        }

        private static string GetStringValue(object value) =>
            value is JsonElement el ? el.GetString() ?? "" : value?.ToString() ?? "";

        private static int GetIntValue(object value, int fallback)
        {
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var n)) return n;
            }
            if (value is int i) return i;
            if (int.TryParse(value?.ToString(), out var parsed)) return parsed;
            return fallback;
        }
    }
}
