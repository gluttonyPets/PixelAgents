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
                // Image scenes
                ImageDuration = GetDoubleValue(config, "imageDuration", 5.0),
                ImageAnimation = GetStringValue(config, "imageAnimation", "zoom-in"),
                // Subtitles
                EnableSubtitles = GetBoolValue(config, "enableSubtitles", true),
                SubtitleLanguage = GetStringValue(config, "subtitleLanguage", "es"),
                SubtitleModel = GetStringValue(config, "subtitleModel", "default"),
                SubtitleStyle = GetStringValue(config, "subtitleStyle", "boxed-word"),
                SubtitleFontSize = GetIntValue(config, "subtitleFontSize", 120),
                SubtitleFontWeight = GetStringValue(config, "subtitleFontWeight", "700"),
                SubtitleFontFamily = GetStringValue(config, "subtitleFontFamily", "Montserrat"),
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
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });

            // DEBUG: log the final payload for troubleshooting
            Console.WriteLine($"[Json2Video] Final payload ({jsonPayload.Length} chars): {jsonPayload[..Math.Min(jsonPayload.Length, 2000)]}");

            var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var submitResp = await http.PostAsync($"{BaseUrl}/movies", requestContent);
            var submitBody = await submitResp.Content.ReadAsStringAsync();

            if (!submitResp.IsSuccessStatusCode)
                return AiResult.Fail($"Json2Video: error al crear video (HTTP {(int)submitResp.StatusCode}): {submitBody}");

            // Check for API-level errors in the response (can return 200 with error)
            var submitDoc = JsonDocument.Parse(submitBody);
            if (submitDoc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                return AiResult.Fail($"Json2Video: {errEl.GetString()}");
            if (submitDoc.RootElement.TryGetProperty("message", out var msgSubmit) && !submitDoc.RootElement.TryGetProperty("project", out _))
                return AiResult.Fail($"Json2Video: {msgSubmit.GetString() ?? submitBody}");

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
        /// Supports two scene formats (can be mixed):
        ///   - Rich: scene has "elements" array → pass-through to API, auto-inject voice/subtitles if missing
        ///   - Legacy: scene has "videoUrl"/"script" → auto-build video+voice+subtitles
        /// Movie-level overrides: "resolution", "quality", "cache", "audio" (background music)
        /// </summary>
        private static object BuildFromJsonInput(string jsonInput, VideoSettings s)
        {
            var doc = JsonDocument.Parse(jsonInput);
            var root = doc.RootElement;

            // Movie-level overrides from input JSON
            if (root.TryGetProperty("resolution", out var resEl) && resEl.ValueKind == JsonValueKind.String)
                s.Resolution = resEl.GetString()!;
            if (root.TryGetProperty("quality", out var qualEl) && qualEl.ValueKind == JsonValueKind.String)
                s.Quality = qualEl.GetString()!;
            if (root.TryGetProperty("cache", out var cacheEl) && (cacheEl.ValueKind == JsonValueKind.True || cacheEl.ValueKind == JsonValueKind.False))
                s.Cache = cacheEl.GetBoolean();

            var scenes = new List<object>();

            var scenesArray = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("scenes", out var sa) ? sa : root;

            // Pre-scan: detect if voice is concentrated in a single scene while others
            // are image-only. If so, we'll move voice to movie level for a continuous
            // voiceover and set scene durations from estimated voice length.
            Dictionary<string, object>? movieVoice = null;
            double perSceneDuration = -1.0;
            bool moveVoiceToMovieLevel = false;

            if (scenesArray.ValueKind == JsonValueKind.Array)
            {
                int voiceSceneCount = 0;
                int imageOnlySceneCount = 0;
                int totalSceneCount = 0;
                string? voiceText = null;
                string? voiceId = null;
                string? voiceModel = null;
                JsonElement? voiceModelSettings = null;

                foreach (var sc in scenesArray.EnumerateArray())
                {
                    totalSceneCount++;
                    if (!sc.TryGetProperty("elements", out var els) || els.ValueKind != JsonValueKind.Array)
                        continue;

                    bool scHasVoice = false;
                    bool scHasImage = false;

                    foreach (var el in els.EnumerateArray())
                    {
                        var elType = el.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                        if (elType == "voice")
                        {
                            scHasVoice = true;
                            // Extract voice info from the first voice element found
                            if (voiceText is null)
                            {
                                voiceText = el.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                                voiceId = el.TryGetProperty("voice", out var vid) ? vid.GetString() : null;
                                voiceModel = el.TryGetProperty("model", out var vm) ? vm.GetString() : null;
                                if (el.TryGetProperty("model-settings", out var ms))
                                    voiceModelSettings = ms;
                            }
                        }
                        if (elType == "image") scHasImage = true;
                    }

                    if (scHasVoice) voiceSceneCount++;
                    if (!scHasVoice && scHasImage) imageOnlySceneCount++;
                }

                Console.WriteLine($"[Json2Video] Pre-scan: {totalSceneCount} scenes, {voiceSceneCount} with voice, {imageOnlySceneCount} image-only, voiceText={voiceText?.Length ?? 0} chars");

                // Move voice to movie level if exactly 1 scene has voice and others are image-only
                if (voiceSceneCount == 1 && imageOnlySceneCount > 0 && !string.IsNullOrWhiteSpace(voiceText))
                {
                    moveVoiceToMovieLevel = true;
                    Console.WriteLine($"[Json2Video] Moving voice to movie level, perSceneDuration will be calculated");

                    // Build movie-level voice element
                    movieVoice = new Dictionary<string, object>
                    {
                        ["type"] = "voice",
                        ["text"] = voiceText,
                        ["voice"] = voiceId ?? s.VoiceName,
                        ["model"] = voiceModel ?? s.VoiceModel,
                        ["start"] = 0.0,
                        ["extra-time"] = 1.0
                    };
                    if (voiceModelSettings.HasValue)
                        movieVoice["model-settings"] = ConvertJsonElement(voiceModelSettings.Value)!;

                    perSceneDuration = GetImageSceneDuration(s.ImageDuration);
                }
            }

            // Build scenes — skip voice elements if voice is moving to movie level
            if (scenesArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var scene in scenesArray.EnumerateArray())
                {
                    if (scene.TryGetProperty("elements", out var elementsEl) && elementsEl.ValueKind == JsonValueKind.Array)
                    {
                        // Rich mode: pass-through elements with smart defaults
                        scenes.Add(BuildSceneFromElements(scene, s,
                            skipVoice: moveVoiceToMovieLevel,
                            overrideDuration: moveVoiceToMovieLevel ? perSceneDuration : -1.0));
                    }
                    else
                    {
                        // Legacy mode: auto-build from videoUrl/imageUrl + script
                        var videoUrl = scene.TryGetProperty("videoUrl", out var vu) ? vu.GetString() : null;
                        var imageUrl = scene.TryGetProperty("imageUrl", out var iu) ? iu.GetString() : null;
                        var script = scene.TryGetProperty("script", out var sc) ? sc.GetString() : null;
                        var mediaUrl = videoUrl ?? imageUrl;
                        var mediaType = imageUrl is not null && videoUrl is null ? "image" : "video";
                        scenes.Add(BuildScene(mediaUrl, mediaType, script, s));
                    }
                }
            }

            if (scenes.Count == 0)
                throw new InvalidOperationException("Json2Video: no se encontraron escenas en el input JSON");

            var movie = BuildMoviePayload(s, scenes);

            // Add movie-level voice element (continuous voiceover across all scenes)
            if (movieVoice is not null)
            {
                var movieElements = movie.ContainsKey("elements")
                    ? (List<object>)movie["elements"]
                    : new List<object>();
                movieElements.Add(movieVoice);
                movie["elements"] = movieElements;
            }

            // Movie-level audio (background music)
            if (root.TryGetProperty("audio", out var audioEl) && audioEl.ValueKind == JsonValueKind.Object)
                movie["audio"] = ConvertJsonElement(audioEl)!;

            // If input JSON already has movie-level elements (e.g. subtitles from LLM),
            // merge them but avoid duplicate subtitles
            if (root.TryGetProperty("elements", out var inputElements) && inputElements.ValueKind == JsonValueKind.Array)
            {
                var movieElements = movie.ContainsKey("elements")
                    ? (List<object>)movie["elements"]
                    : new List<object>();
                var hasSubtitles = movieElements.Any(e => e is Dictionary<string, object> d && d.TryGetValue("type", out var t) && t?.ToString() == "subtitles");

                foreach (var el in inputElements.EnumerateArray())
                {
                    var elType = el.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                    if (elType == "subtitles" && hasSubtitles) continue; // skip duplicate
                    var converted = ConvertJsonElement(el);
                    if (converted is not null) movieElements.Add(converted);
                }

                movie["elements"] = movieElements;
            }

            return movie;
        }

        /// <summary>
        /// Build a scene from an explicit "elements" array in the input JSON.
        /// Auto-injects voice and subtitles from module config if not present.
        /// When skipVoice=true, voice elements are excluded (they'll be at movie level).
        /// When overrideDuration>0, it overrides the scene duration.
        /// </summary>
        private static Dictionary<string, object> BuildSceneFromElements(
            JsonElement sceneElement, VideoSettings s,
            bool skipVoice = false, double overrideDuration = -1.0)
        {
            var elementsJson = sceneElement.GetProperty("elements");
            var elements = new List<object>();
            var hasVoice = false;

            foreach (var el in elementsJson.EnumerateArray())
            {
                // When voice is being moved to movie level, skip voice elements
                if (skipVoice && el.TryGetProperty("type", out var skipTypeEl) && skipTypeEl.GetString() == "voice")
                    continue;

                var converted = ConvertJsonElement(el);
                if (converted is null) continue;

                // Track which element types the LLM already provided
                if (el.TryGetProperty("type", out var typeEl))
                {
                    var type = typeEl.GetString();
                    if (type == "voice") hasVoice = true;
                }

                elements.Add(converted);
            }

            // Auto-inject voice from module config if script is present but no voice element
            var script = sceneElement.TryGetProperty("script", out var scriptEl) ? scriptEl.GetString() : null;
            if (s.EnableVoice && !skipVoice && !hasVoice && !string.IsNullOrEmpty(script))
            {
                var voice = new Dictionary<string, object>
                {
                    ["type"] = "voice",
                    ["text"] = script,
                    ["voice"] = s.VoiceName,
                    ["model"] = s.VoiceModel
                };
                if (s.VoiceModel.StartsWith("elevenlabs") && Math.Abs(s.VoiceSpeed - 1.0) > 0.01)
                {
                    voice["model-settings"] = new Dictionary<string, object>
                    {
                        ["voice_settings"] = new Dictionary<string, object> { ["speed"] = s.VoiceSpeed }
                    };
                }
                elements.Add(voice);
            }

            // NOTE: subtitles are injected at movie level in BuildMoviePayload, not per-scene.
            // If the LLM included a subtitles element inside scene elements, filter it out
            // so it doesn't conflict with the movie-level subtitles.
            elements.RemoveAll(el => el is Dictionary<string, object?> d && d.TryGetValue("type", out var t) && t?.ToString() == "subtitles");

            // Images don't have intrinsic duration — duration:-1 resolves to ~0 seconds.
            // When voice is at movie level (skipVoice) or in this scene, use -2 (match parent scene).
            // When no voice at all, use a fixed duration from settings.
            var safeDuration = s.ImageDuration > 0 ? s.ImageDuration : 5.0;
            var voiceControlsDuration = hasVoice || skipVoice;
            foreach (var el in elements)
            {
                if (el is Dictionary<string, object?> imgDict &&
                    imgDict.TryGetValue("type", out var elType) && elType?.ToString() == "image" &&
                    imgDict.TryGetValue("duration", out var dur))
                {
                    var durValue = dur switch
                    {
                        int iv => (double)iv,
                        long lv => (double)lv,
                        double dv => dv,
                        _ => -1.0
                    };
                    if (durValue < 0.25)
                        imgDict["duration"] = voiceControlsDuration ? -2 : safeDuration;
                }
            }

            // Build scene dict
            double duration;
            if (overrideDuration > 0)
            {
                // Explicit override (e.g. estimated from voice at movie level)
                duration = overrideDuration;
            }
            else
            {
                duration = sceneElement.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                    ? durEl.GetDouble()
                    : -1.0;

                // Scenes without voice need a positive duration — can't auto-calculate from images alone.
                if (!voiceControlsDuration && duration < 0.25)
                    duration = safeDuration;
            }

            var scene = new Dictionary<string, object>
            {
                ["duration"] = duration,
                ["elements"] = elements
            };

            // Transition: use scene-level if provided, otherwise fall back to module config
            if (sceneElement.TryGetProperty("transition", out var transEl) && transEl.ValueKind == JsonValueKind.Object)
            {
                scene["transition"] = ConvertJsonElement(transEl)!;
            }
            else if (s.TransitionStyle != "none" && !string.IsNullOrEmpty(s.TransitionStyle))
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
        /// Recursively convert a JsonElement into a plain .NET object graph
        /// (Dictionary, List, string, number, bool, null) for serialization.
        /// </summary>
        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject()
                    .ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
                JsonValueKind.Array => element.EnumerateArray()
                    .Select(ConvertJsonElement).ToList<object?>(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l) : (object)element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }

        /// <summary>
        /// Build movie payload from plain text input (voiceover script).
        /// Media URLs (video/image) come from configuration.
        /// Supports per-scene media from connected scene ports (perSceneMedia).
        /// </summary>
        private static object BuildFromTextInput(string textInput, Dictionary<string, object> config, VideoSettings s)
        {
            // Check for per-scene media from explicit port connections
            var perSceneMediaMap = ParsePerSceneMedia(config);

            if (perSceneMediaMap is not null && perSceneMediaMap.Count > 0)
            {
                return BuildFromPerSceneMedia(textInput, perSceneMediaMap, config, s);
            }

            // Try rich media entries first (with type info), fallback to legacy videoUrls
            var mediaList = new List<(string Url, string Type)>();

            if (config.TryGetValue("mediaEntries", out var meVal))
            {
                var meStr = meVal is JsonElement meEl ? meEl.GetString() ?? "" : meVal?.ToString() ?? "";
                if (!string.IsNullOrEmpty(meStr))
                {
                    var entries = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(meStr);
                    if (entries is not null)
                    {
                        foreach (var entry in entries)
                        {
                            var url = entry.GetValueOrDefault("url", "");
                            var type = entry.GetValueOrDefault("type", "video");
                            if (!string.IsNullOrEmpty(url))
                                mediaList.Add((url, type));
                        }
                    }
                }
            }

            // Fallback: legacy videoUrls (all treated as video)
            if (mediaList.Count == 0 && config.TryGetValue("videoUrls", out var urls))
            {
                var urlStr = urls is JsonElement el ? el.GetString() ?? "" : urls?.ToString() ?? "";
                if (urlStr.TrimStart().StartsWith("["))
                {
                    var arr = JsonSerializer.Deserialize<string[]>(urlStr);
                    if (arr is not null)
                        mediaList.AddRange(arr.Select(u => (u, "video")));
                }
                else
                {
                    mediaList.AddRange(urlStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(u => (u, "video")));
                }
            }

            var scenes = new List<object>();
            Dictionary<string, object>? movieLevelVoice = null;

            if (mediaList.Count > 0)
            {
                // Check if all media are images — if so, use movie-level voice for
                // a single continuous voiceover with images cycling underneath.
                bool allImages = mediaList.All(m => m.Type == "image");

                if (allImages && s.EnableVoice && !string.IsNullOrWhiteSpace(textInput))
                {
                    // imageDuration > 0 → fixed duration per scene
                    // imageDuration <= 0 (-1 = auto) → estimate from voice text
                    var perSceneDuration = GetImageSceneDuration(s.ImageDuration);

                    for (var i = 0; i < mediaList.Count; i++)
                    {
                        // Image-only scene with fixed duration — no voice per scene
                        scenes.Add(BuildScene(mediaList[i].Url, "image", null, s,
                            isLast: i == mediaList.Count - 1, overrideSceneDuration: perSceneDuration));
                    }

                    // Voice goes at movie level for continuous narration
                    movieLevelVoice = new Dictionary<string, object>
                    {
                        ["type"] = "voice",
                        ["text"] = textInput,
                        ["voice"] = s.VoiceName,
                        ["model"] = s.VoiceModel
                    };
                    if (s.VoiceModel.StartsWith("elevenlabs") && Math.Abs(s.VoiceSpeed - 1.0) > 0.01)
                    {
                        movieLevelVoice["model-settings"] = new Dictionary<string, object>
                        {
                            ["voice_settings"] = new Dictionary<string, object> { ["speed"] = s.VoiceSpeed }
                        };
                    }
                }
                else
                {
                    // Mixed media or no voice: split script across scenes as before
                    var scriptParts = SplitScript(textInput, mediaList.Count);

                    var voicedCount = scriptParts.Count(p => !string.IsNullOrWhiteSpace(p));
                    var sceneCount = voicedCount > 0 ? Math.Min(mediaList.Count, voicedCount) : mediaList.Count;

                    if (sceneCount < mediaList.Count && sceneCount > 0)
                        scriptParts = SplitScript(textInput, sceneCount);

                    for (var i = 0; i < sceneCount; i++)
                    {
                        var script = i < scriptParts.Count ? scriptParts[i] : null;
                        scenes.Add(BuildScene(mediaList[i].Url, mediaList[i].Type, script, s, isLast: i == sceneCount - 1));
                    }
                }
            }
            else
            {
                scenes.Add(BuildScene(null, "video", textInput, s, isLast: true));
            }

            var movie = BuildMoviePayload(s, scenes);

            // Add movie-level voice for image slideshows
            if (movieLevelVoice is not null)
            {
                var movieElements = movie.ContainsKey("elements")
                    ? (List<object>)movie["elements"]
                    : new List<object>();
                movieElements.Add(movieLevelVoice);
                movie["elements"] = movieElements;
            }

            return movie;
        }

        /// <summary>
        /// Parse per-scene media configuration from connected scene ports.
        /// Returns a map of scene number → list of media entries.
        /// </summary>
        private static Dictionary<int, List<Dictionary<string, string>>>? ParsePerSceneMedia(Dictionary<string, object> config)
        {
            if (!config.TryGetValue("perSceneMedia", out var psmVal))
                return null;

            var psmStr = psmVal is JsonElement psmEl ? psmEl.GetRawText() : psmVal?.ToString() ?? "";
            if (string.IsNullOrEmpty(psmStr))
                return null;

            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, List<Dictionary<string, string>>>>(psmStr);
                if (raw is null || raw.Count == 0)
                    return null;

                // Convert port names like "input_scene_1_media" → scene number 1
                var result = new Dictionary<int, List<Dictionary<string, string>>>();
                foreach (var (portId, entries) in raw)
                {
                    // Extract scene number from port ID: input_scene_X_media
                    var parts = portId.Split('_');
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var sceneNum))
                        result[sceneNum] = entries;
                }
                return result.Count > 0 ? result : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Build movie from per-scene media with support for mixed types (images, text, video, audio).
        /// Each scene port can have multiple inputs: media files + text content.
        /// </summary>
        private static object BuildFromPerSceneMedia(
            string textInput,
            Dictionary<int, List<Dictionary<string, string>>> perSceneMedia,
            Dictionary<string, object> config,
            VideoSettings s)
        {
            var sceneCount = perSceneMedia.Keys.Max();
            var scenes = new List<object>();

            // Check if all media are images
            bool allImages = true;
            for (int i = 1; i <= sceneCount; i++)
            {
                if (!perSceneMedia.TryGetValue(i, out var entries)) continue;
                foreach (var entry in entries)
                {
                    var entryType = entry.GetValueOrDefault("type", "");
                    if (entryType != "text" && entryType != "image" && !string.IsNullOrEmpty(entry.GetValueOrDefault("url", "")))
                        allImages = false;
                }
            }

            // For image slideshows: voice at movie level, fixed scene durations
            bool useMovieLevelVoice = allImages && s.EnableVoice && !string.IsNullOrWhiteSpace(textInput);
            double perSceneDuration = -1.0;

            if (useMovieLevelVoice)
            {
                perSceneDuration = GetImageSceneDuration(s.ImageDuration);
            }

            var scriptParts = useMovieLevelVoice ? new List<string>() : SplitScript(textInput, sceneCount);

            for (int i = 1; i <= sceneCount; i++)
            {
                string? mediaUrl = null;
                string? mediaType = null;
                string? sceneText = null;

                if (perSceneMedia.TryGetValue(i, out var entries))
                {
                    foreach (var entry in entries)
                    {
                        var entryType = entry.GetValueOrDefault("type", "");
                        if (entryType == "text")
                        {
                            sceneText = entry.GetValueOrDefault("content", "");
                        }
                        else if (!string.IsNullOrEmpty(entry.GetValueOrDefault("url", "")))
                        {
                            mediaUrl ??= entry["url"];
                            mediaType ??= entryType;
                        }
                    }
                }

                if (useMovieLevelVoice)
                {
                    // No voice per scene — voice will be at movie level
                    scenes.Add(BuildScene(mediaUrl, mediaType ?? "image", null, s,
                        isLast: i == sceneCount, overrideSceneDuration: perSceneDuration));
                }
                else
                {
                    var voiceScript = !string.IsNullOrWhiteSpace(sceneText)
                        ? sceneText
                        : (i - 1 < scriptParts.Count ? scriptParts[i - 1] : null);
                    scenes.Add(BuildScene(mediaUrl, mediaType ?? "image", voiceScript, s, isLast: i == sceneCount));
                }
            }

            var movie = BuildMoviePayload(s, scenes);

            // Add movie-level voice for image slideshows
            if (useMovieLevelVoice)
            {
                var movieElements = movie.ContainsKey("elements")
                    ? (List<object>)movie["elements"]
                    : new List<object>();

                var movieVoice = new Dictionary<string, object>
                {
                    ["type"] = "voice",
                    ["text"] = textInput,
                    ["voice"] = s.VoiceName,
                    ["model"] = s.VoiceModel
                };
                if (s.VoiceModel.StartsWith("elevenlabs") && Math.Abs(s.VoiceSpeed - 1.0) > 0.01)
                {
                    movieVoice["model-settings"] = new Dictionary<string, object>
                    {
                        ["voice_settings"] = new Dictionary<string, object> { ["speed"] = s.VoiceSpeed }
                    };
                }

                movieElements.Add(movieVoice);
                movie["elements"] = movieElements;
            }

            return movie;
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

            // Subtitles: must be at movie level in the "elements" array (not per-scene).
            // The API transcribes the full audio track after rendering all scenes.
            if (s.EnableSubtitles)
            {
                var subtitleSettings = new Dictionary<string, object>
                {
                    ["style"] = s.SubtitleStyle,
                    ["font-family"] = s.SubtitleFontFamily,
                    ["font-size"] = s.SubtitleFontSize.ToString(),
                    ["font-weight"] = s.SubtitleFontWeight,
                    ["position"] = s.SubtitlePosition,
                    ["line-color"] = s.SubtitleLineColor,
                    ["word-color"] = s.SubtitleWordColor,
                    ["max-words-per-line"] = s.SubtitleMaxWordsPerLine,
                };

                if (!string.IsNullOrEmpty(s.SubtitleBoxColor))
                    subtitleSettings["box-color"] = s.SubtitleBoxColor;

                if (s.SubtitleOutlineWidth > 0 && !string.IsNullOrEmpty(s.SubtitleOutlineColor))
                {
                    subtitleSettings["outline-color"] = s.SubtitleOutlineColor;
                    subtitleSettings["outline-width"] = s.SubtitleOutlineWidth;
                }

                var subtitleElement = new Dictionary<string, object>
                {
                    ["type"] = "subtitles",
                    ["language"] = s.SubtitleLanguage,
                    ["model"] = s.SubtitleModel,
                    ["settings"] = subtitleSettings,
                };

                movie["elements"] = new List<object> { subtitleElement };
            }

            return movie;
        }

        /// <summary>
        /// Build a single scene with optional video/image and voice.
        /// isLast=true skips transition to avoid cutting audio at the end.
        /// overrideSceneDuration>0 sets a fixed scene duration (used when voice is at movie level).
        /// </summary>
        private static object BuildScene(string? mediaUrl, string mediaType, string? script, VideoSettings s, bool isLast = false, double overrideSceneDuration = -1.0)
        {
            var elements = new List<Dictionary<string, object>>();
            var hasVoice = s.EnableVoice && !string.IsNullOrEmpty(script);

            // Media element (video or image)
            if (!string.IsNullOrEmpty(mediaUrl))
            {
                if (mediaType == "image")
                {
                    // When voice is present, use -2 so the image matches the scene duration.
                    // Without voice, pass through configured imageDuration directly.
                    var imgDuration = hasVoice ? -2.0 : s.ImageDuration;

                    var imgElement = new Dictionary<string, object>
                    {
                        ["type"] = "image",
                        ["src"] = mediaUrl,
                        ["duration"] = imgDuration
                    };

                    // Apply Ken Burns animation
                    var anim = s.ImageAnimation;
                    if (anim == "random")
                    {
                        var options = new[] { "zoom-in", "zoom-out", "pan-left", "pan-right" };
                        anim = options[Random.Shared.Next(options.Length)];
                    }
                    switch (anim)
                    {
                        case "zoom-in":
                            imgElement["zoom"] = 3;
                            break;
                        case "zoom-out":
                            imgElement["zoom"] = -3;
                            break;
                        case "pan-left":
                            imgElement["pan"] = "left";
                            imgElement["zoom"] = 2;
                            break;
                        case "pan-right":
                            imgElement["pan"] = "right";
                            imgElement["zoom"] = 2;
                            break;
                    }

                    elements.Add(imgElement);
                }
                else
                {
                    elements.Add(new Dictionary<string, object>
                    {
                        ["type"] = "video",
                        ["src"] = mediaUrl,
                        ["duration"] = -1
                    });
                }
            }

            // Voice element — small start delay for natural pacing
            if (hasVoice)
            {
                var voice = new Dictionary<string, object>
                {
                    ["type"] = "voice",
                    ["text"] = script!,
                    ["voice"] = s.VoiceName,
                    ["model"] = s.VoiceModel,
                    ["start"] = 0.4,
                    // Extra time after voice ends so it doesn't cut abruptly
                    ["extra-time"] = isLast ? 1.0 : 0.3
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

            var scene = new Dictionary<string, object>
            {
                ["duration"] = overrideSceneDuration > 0 ? overrideSceneDuration : -1,
                ["elements"] = elements
            };

            // Transition between scenes — skip on last scene to avoid cutting final audio
            if (!isLast && s.TransitionStyle != "none" && !string.IsNullOrEmpty(s.TransitionStyle))
            {
                scene["transition"] = new Dictionary<string, object>
                {
                    ["style"] = s.TransitionStyle,
                    ["duration"] = Math.Min(s.TransitionDuration, 1.0)
                };
            }

            return scene;
        }

        /// <summary>
        /// Split a script into parts for scenes. Each part should have enough text
        /// for a meaningful voiceover (at least ~2 sentences / ~8 seconds of speech).
        /// The number of parts is capped so each scene has substantial content.
        /// </summary>
        /// <summary>
        /// Get per-scene duration for image slideshows.
        /// Passes through the configured value directly (including -1 for auto).
        /// </summary>
        private static double GetImageSceneDuration(double imageDuration)
        {
            return imageDuration;
        }

        private static List<string> SplitScript(string script, int maxParts)
        {
            if (maxParts <= 1) return new List<string> { script };

            var sentences = script.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
            if (sentences.Length == 0) return new List<string> { script };

            // Use all available parts (one scene per image). If there are fewer
            // sentences than images, group into fewer parts (no empty scenes).
            var effectiveParts = Math.Min(maxParts, sentences.Length);

            var perPart = sentences.Length / effectiveParts;
            var remainder = sentences.Length % effectiveParts;
            var scriptParts = new List<string>();
            var idx = 0;

            for (var i = 0; i < effectiveParts; i++)
            {
                var count = perPart + (i < remainder ? 1 : 0);
                var partSentences = sentences.Skip(idx).Take(count);
                scriptParts.Add(string.Join(". ", partSentences.Select(s => s.TrimEnd('.', '!', '?'))) + ".");
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

        /// <summary>
        /// Parse resource instructions (JSON array) and inject them as movie-level elements.
        /// All resources go into movie["elements"] — they float on top of all scenes.
        /// Timing is controlled via start/duration on each resource.
        /// Properties are passed through to the Json2Video API as-is.
        /// </summary>
        private static void InjectResources(Dictionary<string, object> movie, string resourcesJson)
        {
            if (string.IsNullOrWhiteSpace(resourcesJson)) return;

            // Extract JSON array (LLM might wrap it in markdown or extra text)
            var jsonStart = resourcesJson.IndexOf('[');
            var jsonEnd = resourcesJson.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart) return;

            List<JsonElement>? resources;
            try
            {
                resources = JsonSerializer.Deserialize<List<JsonElement>>(resourcesJson[jsonStart..(jsonEnd + 1)]);
            }
            catch { return; }
            if (resources is null || resources.Count == 0) return;

            // Get or create movie-level elements array (subtitles may already be there)
            var movieElements = movie.ContainsKey("elements")
                ? (List<object>)movie["elements"]
                : new List<object>();

            foreach (var resource in resources)
            {
                var converted = ConvertJsonElement(resource);
                if (converted is not null)
                    movieElements.Add(converted);
            }

            movie["elements"] = movieElements;
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
        // Image scenes: duration in seconds for each image scene
        public double ImageDuration { get; set; } = 5.0;
        // Image animation: none, zoom-in, zoom-out, pan-left, pan-right, random
        public string ImageAnimation { get; set; } = "zoom-in";
        // Subtitles
        public bool EnableSubtitles { get; set; } = true;
        public string SubtitleLanguage { get; set; } = "es";
        public string SubtitleModel { get; set; } = "default";
        public string SubtitleStyle { get; set; } = "boxed-word";
        public int SubtitleFontSize { get; set; } = 120;
        public string SubtitleFontWeight { get; set; } = "700";
        public string SubtitleFontFamily { get; set; } = "Montserrat";
        public string SubtitlePosition { get; set; } = "bottom-center";
        public string SubtitleLineColor { get; set; } = "#FFFFFF";
        public string SubtitleWordColor { get; set; } = "#FFFF00";
        public string SubtitleBoxColor { get; set; } = "#000000CC";
        public string SubtitleOutlineColor { get; set; } = "";
        public int SubtitleOutlineWidth { get; set; }
        public int SubtitleMaxWordsPerLine { get; set; } = 4;
    }
}
