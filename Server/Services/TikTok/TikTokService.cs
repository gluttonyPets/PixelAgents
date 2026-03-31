using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.Services.TikTok;

// ── Config stored per-project as JSON in Project.TikTokConfig ──

public class TikTokDirectConfig
{
    public string ClientKey { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string OpenId { get; set; } = "";
    public DateTime TokenExpiresAt { get; set; }
}

// ── Creator Info response ──

public class TikTokCreatorInfo
{
    public string CreatorAvatarUrl { get; set; } = "";
    public string CreatorUsername { get; set; } = "";
    public string CreatorNickname { get; set; } = "";
    public List<string> PrivacyLevelOptions { get; set; } = new();
    public bool CommentDisabled { get; set; }
    public bool DuetDisabled { get; set; }
    public bool StitchDisabled { get; set; }
    public int MaxVideoPostDurationSec { get; set; }
}

// ── Publish result ──

public class TikTokPublishResult
{
    public bool IsSuccess { get; set; }
    public string? PublishId { get; set; }
    public string? Error { get; set; }
    public string? PostId { get; set; }
}

// ── Post options ──

public class TikTokPostOptions
{
    public string PrivacyLevel { get; set; } = "SELF_ONLY";
    public bool DisableComment { get; set; }
    public bool DisableDuet { get; set; }
    public bool DisableStitch { get; set; }
    public bool BrandContentToggle { get; set; }
    public bool BrandOrganicToggle { get; set; }
    public bool IsAigc { get; set; }
    public int? VideoCoverTimestampMs { get; set; }
}

public class TikTokService
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private const string AuthBaseUrl = "https://www.tiktok.com/v2/auth/authorize/";
    private const string TokenUrl = "https://open.tiktokapis.com/v2/oauth/token/";
    private const string ApiBase = "https://open.tiktokapis.com/v2";

    // ── OAuth ──

    public string BuildAuthorizationUrl(string clientKey, string redirectUri, string state)
    {
        var scopes = "user.info.basic,video.publish,video.upload";
        return $"{AuthBaseUrl}?client_key={Uri.EscapeDataString(clientKey)}" +
               $"&scope={Uri.EscapeDataString(scopes)}" +
               $"&response_type=code" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<TikTokDirectConfig?> ExchangeCodeForTokenAsync(
        string clientKey, string clientSecret, string code, string redirectUri)
    {
        var body = new Dictionary<string, string>
        {
            ["client_key"] = clientKey,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };

        var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(body));
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
            return null;

        var accessToken = root.GetProperty("access_token").GetString() ?? "";
        var refreshToken = root.GetProperty("refresh_token").GetString() ?? "";
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        var openId = root.GetProperty("open_id").GetString() ?? "";

        return new TikTokDirectConfig
        {
            ClientKey = clientKey,
            ClientSecret = clientSecret,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            OpenId = openId,
            TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60), // 60s buffer
        };
    }

    public async Task<TikTokDirectConfig?> RefreshTokenAsync(TikTokDirectConfig config)
    {
        var body = new Dictionary<string, string>
        {
            ["client_key"] = config.ClientKey,
            ["client_secret"] = config.ClientSecret,
            ["refresh_token"] = config.RefreshToken,
            ["grant_type"] = "refresh_token",
        };

        var resp = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(body));
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        config.AccessToken = root.GetProperty("access_token").GetString() ?? "";
        config.RefreshToken = root.GetProperty("refresh_token").GetString() ?? "";
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        config.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn - 60);

        return config;
    }

    /// <summary>Ensure access token is fresh, refreshing if needed.</summary>
    public async Task<TikTokDirectConfig> EnsureFreshTokenAsync(TikTokDirectConfig config)
    {
        if (DateTime.UtcNow < config.TokenExpiresAt)
            return config;

        var refreshed = await RefreshTokenAsync(config);
        return refreshed ?? throw new InvalidOperationException(
            "No se pudo refrescar el token de TikTok. Reconecta la cuenta en los ajustes del proyecto.");
    }

    // ── Creator Info ──

    public async Task<TikTokCreatorInfo?> QueryCreatorInfoAsync(string accessToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/post/publish/creator_info/query/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return null;

        var info = new TikTokCreatorInfo();
        if (data.TryGetProperty("creator_avatar_url", out var av)) info.CreatorAvatarUrl = av.GetString() ?? "";
        if (data.TryGetProperty("creator_username", out var un)) info.CreatorUsername = un.GetString() ?? "";
        if (data.TryGetProperty("creator_nickname", out var nn)) info.CreatorNickname = nn.GetString() ?? "";
        if (data.TryGetProperty("privacy_level_options", out var pl))
        {
            foreach (var opt in pl.EnumerateArray())
                info.PrivacyLevelOptions.Add(opt.GetString() ?? "");
        }
        if (data.TryGetProperty("comment_disabled", out var cd)) info.CommentDisabled = cd.GetBoolean();
        if (data.TryGetProperty("duet_disabled", out var dd)) info.DuetDisabled = dd.GetBoolean();
        if (data.TryGetProperty("stitch_disabled", out var sd)) info.StitchDisabled = sd.GetBoolean();
        if (data.TryGetProperty("max_video_post_duration_sec", out var mv)) info.MaxVideoPostDurationSec = mv.GetInt32();

        return info;
    }

    // ── Publish Video (PULL_FROM_URL) ──

    public async Task<TikTokPublishResult> PublishVideoAsync(
        string accessToken, string videoUrl, string title, TikTokPostOptions options)
    {
        var payload = new
        {
            post_info = new
            {
                title,
                privacy_level = options.PrivacyLevel,
                disable_comment = options.DisableComment,
                disable_duet = options.DisableDuet,
                disable_stitch = options.DisableStitch,
                brand_content_toggle = options.BrandContentToggle,
                brand_organic_toggle = options.BrandOrganicToggle,
                is_aigc = options.IsAigc,
                video_cover_timestamp_ms = options.VideoCoverTimestampMs ?? 1000,
            },
            source_info = new
            {
                source = "PULL_FROM_URL",
                video_url = videoUrl,
            }
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/post/publish/video/init/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = "Error desconocido";
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : "";
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "";
                errMsg = $"{code}: {msg}";
            }
            return new TikTokPublishResult { IsSuccess = false, Error = errMsg };
        }

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("publish_id", out var pid))
        {
            return new TikTokPublishResult { IsSuccess = true, PublishId = pid.GetString() };
        }

        return new TikTokPublishResult { IsSuccess = false, Error = "Respuesta inesperada de TikTok" };
    }

    // ── Publish Photo Carousel (PULL_FROM_URL) ──

    public async Task<TikTokPublishResult> PublishPhotosAsync(
        string accessToken, List<string> imageUrls, string title, TikTokPostOptions options, int coverIndex = 0)
    {
        var payload = new
        {
            post_info = new
            {
                title,
                description = title,
                privacy_level = options.PrivacyLevel,
                disable_comment = options.DisableComment,
                brand_content_toggle = options.BrandContentToggle,
                brand_organic_toggle = options.BrandOrganicToggle,
                is_aigc = options.IsAigc,
            },
            source_info = new
            {
                source = "PULL_FROM_URL",
                photo_cover_index = coverIndex,
                photo_images = imageUrls.ToArray(),
            },
            post_mode = "DIRECT_POST",
            media_type = "PHOTO",
        };

        var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/post/publish/content/init/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = "Error desconocido";
            if (root.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : "";
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "";
                errMsg = $"{code}: {msg}";
            }
            return new TikTokPublishResult { IsSuccess = false, Error = errMsg };
        }

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("publish_id", out var pid))
        {
            return new TikTokPublishResult { IsSuccess = true, PublishId = pid.GetString() };
        }

        return new TikTokPublishResult { IsSuccess = false, Error = "Respuesta inesperada de TikTok" };
    }

    // ── Poll Status ──

    public async Task<TikTokPublishResult> PollPublishStatusAsync(
        string accessToken, string publishId, int maxAttempts = 15, int initialDelayMs = 5000)
    {
        var delay = initialDelayMs;
        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(delay);

            var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/post/publish/status/fetch/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { publish_id = publishId }),
                Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                continue;

            var status = data.TryGetProperty("status", out var s) ? s.GetString() : "";

            switch (status)
            {
                case "PUBLISH_COMPLETE":
                    string? postId = null;
                    if (data.TryGetProperty("publicaly_available_post_id", out var ids))
                    {
                        foreach (var id in ids.EnumerateArray())
                        {
                            postId = id.GetString();
                            break;
                        }
                    }
                    return new TikTokPublishResult
                    {
                        IsSuccess = true,
                        PublishId = publishId,
                        PostId = postId,
                    };

                case "FAILED":
                    var reason = data.TryGetProperty("fail_reason", out var fr) ? fr.GetString() : "unknown";
                    return new TikTokPublishResult
                    {
                        IsSuccess = false,
                        PublishId = publishId,
                        Error = $"TikTok publish failed: {reason}",
                    };

                default: // PROCESSING_UPLOAD, PROCESSING_DOWNLOAD, SENDING_TO_USER_INBOX, etc.
                    delay = Math.Min(delay * 2, 30000); // exponential backoff, max 30s
                    break;
            }
        }

        return new TikTokPublishResult
        {
            IsSuccess = false,
            PublishId = publishId,
            Error = "Timeout: TikTok aun esta procesando el contenido",
        };
    }
}
