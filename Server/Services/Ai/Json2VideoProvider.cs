using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai
{
    public class Json2VideoProvider : IAiProvider
    {
        private const string BaseUrl = "https://api.json2video.com/v2";

        public string ProviderType => "Json2Video";
        public IEnumerable<string> SupportedModuleTypes => new[] { "VideoEdit" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "VideoEdit" => await EditVideoAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Json2Video")
                };
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Json2Video: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("x-api-key", apiKey);

                // Create a minimal movie to validate the key
                var testPayload = new
                {
                    resolution = "sd",
                    scenes = new[]
                    {
                        new
                        {
                            elements = new object[]
                            {
                                new { type = "text", text = "test", duration = 1 }
                            }
                        }
                    }
                };

                var json = JsonSerializer.Serialize(testPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"{BaseUrl}/movies", content);

                if (resp.IsSuccessStatusCode)
                    return (true, null);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, "API Key de Json2Video invalida");

                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"Error al validar Json2Video (HTTP {(int)resp.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo conectar con Json2Video: {ex.Message}");
            }
        }

        private async Task<AiResult> EditVideoAsync(AiExecutionContext context)
        {
            var input = context.Input;
            if (string.IsNullOrWhiteSpace(input))
                return AiResult.Fail("Json2Video: se necesita un input con la configuracion del video");

            // Parse the input as JSON instruction from the orchestrator/previous step
            // The input can be:
            // 1. A JSON object with explicit scenes configuration
            // 2. Plain text that we use as voiceover script with video URLs from config

            var config = context.Configuration;

            // Read all configuration options into a settings record
            var settings = new VideoSettings
            {
                // Movie-level
                Resolution = GetStringValue(config, "resolution", "full-hd"),
                Quality = GetStringValue(config, "quality", "high"),
                Cache = GetBoolValue(config, "cache", true),
                // Transitions
                TransitionStyle = GetStringValue(config, "transitionStyle", "none"),
                TransitionDuration = GetDoubleValue(config, "transitionDuration", 1.0),
                // Voice
                EnableVoice = GetBoolValue(config, "enableVoice", true),
                VoiceModel = GetStringValue(config, "voiceModel", "azure"),
                VoiceName = GetStringValue(config, "voiceName", "es-ES-AlvaroNeural"),
                VoiceSpeed = GetDoubleValue(config, "voiceSpeed", 1.0),
                // Subtitles
                EnableSubtitles = GetBoolValue(config, "enableSubtitles", true),
                SubtitleLanguage = GetStringValue(config, "subtitleLanguage", "es"),
                SubtitleModel = GetStringValue(config, "subtitleModel", "default"),
                SubtitleStyle = GetStringValue(config, "subtitleStyle", "boxed"),
                SubtitleFontSize = GetIntValue(config, "subtitleFontSize", 120),
                SubtitleFontFamily = GetStringValue(config, "subtitleFontFamily", "Arial"),
                SubtitlePosition = GetStringValue(config, "subtitlePosition", "bottom-center"),
                SubtitleLineColor = GetStringValue(config, "subtitleLineColor", "#FFFFFF"),
                SubtitleWordColor = GetStringValue(config, "subtitleWordColor", "#FFFF00"),
                SubtitleBoxColor = GetStringValue(config, "subtitleBoxColor", "#000000CC"),
                SubtitleOutlineColor = GetStringValue(config, "subtitleOutlineColor", ""),
                SubtitleOutlineWidth = GetIntValue(config, "subtitleOutlineWidth", 0),
                SubtitleMaxWordsPerLine = GetIntValue(config, "subtitleMaxWordsPerLine", 4),
            };

            // Try to parse input as JSON with scenes
            object moviePayload;
            if (input.TrimStart().StartsWith("{") || input.TrimStart().StartsWith("["))
            {
                moviePayload = BuildFromJsonInput(input, settings);
            }
            else
            {
                moviePayload = BuildFromTextInput(input, config, settings);
            }

            // Submit render job
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.Add("x-api-key", context.ApiKey);

            var jsonPayload = JsonSerializer.Serialize(moviePayload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var submitResp = await http.PostAsync($"{BaseUrl}/movies", requestContent);
            var submitBody = await submitResp.Content.ReadAsStringAsync();

            if (!submitResp.IsSuccessStatusCode)
                return AiResult.Fail($"Json2Video: error al crear video (HTTP {(int)submitResp.StatusCode}): {submitBody}");

            var submitDoc = JsonDocument.Parse(submitBody);
            if (!submitDoc.RootElement.TryGetProperty("project", out var projectId))
                return AiResult.Fail($"Json2Video: respuesta inesperada, no se encontro 'project': {submitBody}");

            var projectIdStr = projectId.GetString()!;

            // Poll for completion (up to 10 min)
            const int maxAttempts = 120;
            const int pollIntervalMs = 5000;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs);

                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/movies?project={projectIdStr}");
                pollRequest.Headers.Add("x-api-key", context.ApiKey);

                var pollResp = await http.SendAsync(pollRequest);
                if (!pollResp.IsSuccessStatusCode)
                    continue;

                var pollBody = await pollResp.Content.ReadAsStringAsync();
                var pollDoc = JsonDocument.Parse(pollBody);
                var root = pollDoc.RootElement;

                var status = root.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() ?? ""
                    : "";

                if (status == "error")
                {
                    var errorMsg = root.TryGetProperty("message", out var msgEl)
                        ? msgEl.GetString() ?? "Error desconocido"
                        : "Error desconocido";
                    return AiResult.Fail($"Json2Video render error: {errorMsg}");
                }

                if (status != "done")
                    continue;

                // Download rendered video
                var videoUrl = root.TryGetProperty("url", out var urlEl)
                    ? urlEl.GetString()
                    : null;

                if (string.IsNullOrEmpty(videoUrl))
                    return AiResult.Fail("Json2Video: render completado pero no se encontro URL del video");

                using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                var videoBytes = await dlClient.GetByteArrayAsync(videoUrl);

                var duration = root.TryGetProperty("duration", out var durEl) ? durEl.GetInt32() : 0;
                var renderTime = root.TryGetProperty("rendering_time", out var rtEl) ? rtEl.GetInt32() : 0;

                var result = AiResult.OkFile(videoBytes, "video/mp4", new Dictionary<string, object>
                {
                    ["source"] = "json2video",
                    ["projectId"] = projectIdStr,
                    ["duration"] = duration,
                    ["renderingTime"] = renderTime,
                    ["resolution"] = settings.Resolution,
                    ["videoUrl"] = videoUrl,
                });
                result.EstimatedCost = PricingCatalog.EstimateVideoEditCost(duration);
                return result;
            }

            return AiResult.Fail($"Json2Video: timeout esperando render (>{maxAttempts * pollIntervalMs / 1000}s)");
        }

        /// <summary>
        /// Build movie payload from structured JSON input.
        /// Expected format: { "scenes": [{ "videoUrl": "...", "script": "..." }, ...] }
        /// </summary>
        private static object BuildFromJsonInput(string jsonInput, VideoSettings s)
        {
            var doc = JsonDocument.Parse(jsonInput);
            var root = doc.RootElement;

            var scenes = new List<object>();

            var scenesArray = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("scenes", out var sa) ? sa : root;

            if (scenesArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var scene in scenesArray.EnumerateArray())
                {
                    var videoUrl = scene.TryGetProperty("videoUrl", out var vu) ? vu.GetString() : null;
                    var script = scene.TryGetProperty("script", out var sc) ? sc.GetString() : null;
                    scenes.Add(BuildScene(videoUrl, script, s));
                }
            }

            if (scenes.Count == 0)
                throw new InvalidOperationException("Json2Video: no se encontraron escenas en el input JSON");

            return BuildMoviePayload(s, scenes);
        }

        /// <summary>
        /// Build movie payload from plain text input (voiceover script).
        /// Video URLs come from configuration.
        /// </summary>
        private static object BuildFromTextInput(string textInput, Dictionary<string, object> config, VideoSettings s)
        {
            // Get video URLs from config (comma-separated or JSON array)
            var videoUrls = new List<string>();
            if (config.TryGetValue("videoUrls", out var urls))
            {
                var urlStr = urls is JsonElement el ? el.GetString() ?? "" : urls?.ToString() ?? "";
                if (urlStr.TrimStart().StartsWith("["))
                {
                    var arr = JsonSerializer.Deserialize<string[]>(urlStr);
                    if (arr is not null) videoUrls.AddRange(arr);
                }
                else
                {
                    videoUrls.AddRange(urlStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            var scenes = new List<object>();

            if (videoUrls.Count > 0)
            {
                var scriptParts = SplitScript(textInput, videoUrls.Count);
                for (var i = 0; i < videoUrls.Count; i++)
                {
                    var script = i < scriptParts.Count ? scriptParts[i] : null;
                    scenes.Add(BuildScene(videoUrls[i], script, s));
                }
            }
            else
            {
                scenes.Add(BuildScene(null, textInput, s));
            }

            return BuildMoviePayload(s, scenes);
        }

        /// <summary>
        /// Build the top-level movie object with all settings.
        /// </summary>
        private static Dictionary<string, object> BuildMoviePayload(VideoSettings s, List<object> scenes)
        {
            var movie = new Dictionary<string, object>
            {
                ["resolution"] = s.Resolution,
                ["quality"] = s.Quality,
                ["scenes"] = scenes,
            };

            if (!s.Cache)
                movie["cache"] = false;

            return movie;
        }

        /// <summary>
        /// Build a single scene with optional video, voice, subtitles and transition.
        /// </summary>
        private static object BuildScene(string? videoUrl, string? script, VideoSettings s)
        {
            var elements = new List<Dictionary<string, object>>();

            // Video element
            if (!string.IsNullOrEmpty(videoUrl))
            {
                elements.Add(new Dictionary<string, object>
                {
                    ["type"] = "video",
                    ["src"] = videoUrl,
                    ["duration"] = -1
                });
            }

            // Voice element
            if (s.EnableVoice && !string.IsNullOrEmpty(script))
            {
                var voice = new Dictionary<string, object>
                {
                    ["type"] = "voice",
                    ["text"] = script,
                    ["voice"] = s.VoiceName,
                    ["model"] = s.VoiceModel
                };

                // ElevenLabs speed setting
                if (s.VoiceModel.StartsWith("elevenlabs") && Math.Abs(s.VoiceSpeed - 1.0) > 0.01)
                {
                    voice["model-settings"] = new Dictionary<string, object>
                    {
                        ["voice_settings"] = new Dictionary<string, object>
                        {
                            ["speed"] = s.VoiceSpeed
                        }
                    };
                }

                elements.Add(voice);
            }

            // Subtitle element (auto-generated from audio)
            if (s.EnableSubtitles)
            {
                var sub = new Dictionary<string, object>
                {
                    ["type"] = "subtitles",
                    ["model"] = s.SubtitleModel,
                    ["language"] = s.SubtitleLanguage,
                    ["font-family"] = s.SubtitleFontFamily,
                    ["font-size"] = s.SubtitleFontSize,
                    ["position"] = s.SubtitlePosition,
                    ["line-color"] = s.SubtitleLineColor,
                    ["word-color"] = s.SubtitleWordColor,
                    ["box-color"] = s.SubtitleBoxColor,
                    ["style"] = s.SubtitleStyle,
                    ["max-words-per-line"] = s.SubtitleMaxWordsPerLine,
                };

                if (s.SubtitleOutlineWidth > 0 && !string.IsNullOrEmpty(s.SubtitleOutlineColor))
                {
                    sub["outline-color"] = s.SubtitleOutlineColor;
                    sub["outline-width"] = s.SubtitleOutlineWidth;
                }

                elements.Add(sub);
            }

            var scene = new Dictionary<string, object>
            {
                ["duration"] = -1,
                ["elements"] = elements
            };

            // Scene transition
            if (s.TransitionStyle != "none" && !string.IsNullOrEmpty(s.TransitionStyle))
            {
                scene["transition"] = new Dictionary<string, object>
                {
                    ["style"] = s.TransitionStyle,
                    ["duration"] = s.TransitionDuration
                };
            }

            return scene;
        }

        /// <summary>
        /// Split a script into roughly equal parts by sentences.
        /// </summary>
        private static List<string> SplitScript(string script, int parts)
        {
            if (parts <= 1) return new List<string> { script };

            var sentences = script.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
            if (sentences.Length <= parts)
            {
                var result = sentences.Select(s => s.TrimEnd('.', '!', '?') + ".").ToList();
                while (result.Count < parts) result.Add("");
                return result;
            }

            var perPart = sentences.Length / parts;
            var remainder = sentences.Length % parts;
            var scriptParts = new List<string>();
            var idx = 0;

            for (var i = 0; i < parts; i++)
            {
                var count = perPart + (i < remainder ? 1 : 0);
                var partSentences = sentences.Skip(idx).Take(count);
                scriptParts.Add(string.Join(". ", partSentences).TrimEnd() + ".");
                idx += count;
            }

            return scriptParts;
        }

        private static string GetStringValue(Dictionary<string, object> config, string key, string fallback)
        {
            if (!config.TryGetValue(key, out var value)) return fallback;
            return value is JsonElement el ? el.GetString() ?? fallback : value?.ToString() ?? fallback;
        }

        private static int GetIntValue(Dictionary<string, object> config, string key, int fallback)
        {
            if (!config.TryGetValue(key, out var value)) return fallback;
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Number) return el.GetInt32();
                if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var n)) return n;
            }
            if (value is int i) return i;
            if (int.TryParse(value?.ToString(), out var parsed)) return parsed;
            return fallback;
        }

        private static bool GetBoolValue(Dictionary<string, object> config, string key, bool fallback)
        {
            if (!config.TryGetValue(key, out var value)) return fallback;
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.True) return true;
                if (el.ValueKind == JsonValueKind.False) return false;
                if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var b)) return b;
            }
            if (value is bool bv) return bv;
            if (bool.TryParse(value?.ToString(), out var parsed)) return parsed;
            return fallback;
        }

        private static double GetDoubleValue(Dictionary<string, object> config, string key, double fallback)
        {
            if (!config.TryGetValue(key, out var value)) return fallback;
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
                if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
            }
            if (value is double dv) return dv;
            if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return fallback;
        }
    }

    internal class VideoSettings
    {
        // Movie-level
        public string Resolution { get; set; } = "full-hd";
        public string Quality { get; set; } = "high";
        public bool Cache { get; set; } = true;
        // Transitions
        public string TransitionStyle { get; set; } = "none";
        public double TransitionDuration { get; set; } = 1.0;
        // Voice
        public bool EnableVoice { get; set; } = true;
        public string VoiceModel { get; set; } = "azure";
        public string VoiceName { get; set; } = "es-ES-AlvaroNeural";
        public double VoiceSpeed { get; set; } = 1.0;
        // Subtitles
        public bool EnableSubtitles { get; set; } = true;
        public string SubtitleLanguage { get; set; } = "es";
        public string SubtitleModel { get; set; } = "default";
        public string SubtitleStyle { get; set; } = "boxed";
        public int SubtitleFontSize { get; set; } = 120;
        public string SubtitleFontFamily { get; set; } = "Arial";
        public string SubtitlePosition { get; set; } = "bottom-center";
        public string SubtitleLineColor { get; set; } = "#FFFFFF";
        public string SubtitleWordColor { get; set; } = "#FFFF00";
        public string SubtitleBoxColor { get; set; } = "#000000CC";
        public string SubtitleOutlineColor { get; set; } = "";
        public int SubtitleOutlineWidth { get; set; }
        public int SubtitleMaxWordsPerLine { get; set; } = 4;
    }
}
