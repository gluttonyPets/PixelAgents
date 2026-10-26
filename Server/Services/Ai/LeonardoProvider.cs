using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai
{
    public class LeonardoProvider : IAiProvider
    {
        private const string BaseUrl = "https://cloud.leonardo.ai/api/rest/v1";

        public string ProviderType => "LeonardoAI";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Image", "Video" };

        // Mapping: friendly model ID → Leonardo API UUID
        private static readonly Dictionary<string, string> ModelIdMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["leonardo-phoenix"] = "de7d3faf-762f-48e0-b3b7-9d0ac3a3fcf3",       // Phoenix 1.0
            ["leonardo-phoenix-0.9"] = "6b645e3a-d64f-4341-a6d8-7a3690fbf042",   // Phoenix 0.9
            ["leonardo-flux-dev"] = "b2614463-296c-462a-9586-aafdb8f00e36",       // Flux Dev
            ["leonardo-flux-schnell"] = "1dd50843-d653-4516-a8e3-f0238ee453ff",   // Flux Schnell
        };

        // El video va por la API v2 (POST /v2/generations): la v1 solo conoce un
        // puñado de modelos, varios ya retirados, y todo lo nuevo sale en la v2. El
        // resultado se sigue recogiendo con GET /v1/generations/{id}, que es lo que
        // documenta Leonardo para ambas.
        private const string BaseUrlV2 = "https://cloud.leonardo.ai/api/rest/v2";

        // Mapping: id del catalogo -> valor del campo "model" de la v2. Ojo: no son
        // los UUID de ModelIdMap, sino nombres propios de cada modelo de video.
        private static readonly Dictionary<string, string> VideoModelMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["leonardo-motion-2"]         = "motion_2.0",
            ["leonardo-motion-2-fast"]    = "motion_2.0-fast",
            ["leonardo-veo-3.1"]          = "veo-3.1-generate-001",
            ["leonardo-veo-3.1-fast"]     = "veo-3.1-fast-generate-001",
            ["leonardo-veo-3.1-lite"]     = "veo-3.1-lite",
            ["leonardo-kling-3"]          = "kling-3.0",
            ["leonardo-kling-3-turbo"]    = "kling-3.0-turbo",
            ["leonardo-kling-o3"]         = "kling-video-o-3",
            ["leonardo-seedance-2"]       = "seedance-2.0",
            ["leonardo-seedance-2-fast"]  = "seedance-2.0-fast",
            ["leonardo-hailuo-03"]        = "hailuo-03",
            ["leonardo-hailuo-2.3-fast"]  = "hailuo-2_3-fast",
            ["leonardo-wan-3"]            = "alibaba/wan-3.0",
            ["leonardo-ltx-2.3-pro"]      = "ltxv-2.3-pro",
            ["leonardo-ltx-2.3-fast"]     = "ltxv-2.3-fast",
            ["leonardo-grok-imagine-1.5"] = "grok-imagine-1.5",
            ["leonardo-kling-2.6"]           = "kling-2.6",
            ["leonardo-kling-2.5-turbo-pro"] = "kling-2.5",
            ["leonardo-kling-2.5-turbo-std"] = "kling-2.5-turbo-standard",
            ["leonardo-kling-o1"]            = "kling-video-o-1",
            ["leonardo-seedance-2-mini"]     = "seedance-2.0-mini",
            ["leonardo-seedance-2.5"]        = "bytedance/seedance-2.5",
            ["leonardo-seedance-1-pro"]      = "seedance-1.0-pro",
            ["leonardo-seedance-1-pro-fast"] = "seedance-1.0-pro-fast",
            ["leonardo-hailuo-2.3"]          = "hailuo-2_3",
            ["leonardo-wan-2.7"]             = "wan-2.7",
            ["leonardo-wan-2.6"]             = "wan-2.6",
            ["leonardo-flux-3-video"]        = "bfl/flux-3-video",
            ["leonardo-happy-horse-1.1"]     = "happy-horse-1.1",
            ["leonardo-happy-horse"]         = "happy-horse",
            ["leonardo-gemini-omni-1.1-flash"] = "google/gemini-omni-1.1-flash-preview",
            ["leonardo-gemini-omni-flash"]   = "gemini-omni-flash",
        };

        /// <summary>
        /// Prompt cuando el modulo no trae ninguno. La v2 lo exige en todos los
        /// modelos de video, y un clip sin indicaciones es un caso normal: el usuario
        /// solo quiere que la imagen cobre vida.
        /// </summary>
        public const string DefaultVideoPrompt = "Animate this image with natural, subtle motion.";

        /// <summary>Modelos de video que sabe llamar este provider.</summary>
        public static IReadOnlyCollection<string> VideoModelIds => VideoModelMap.Keys;

        // Models that require the contrast parameter
        private static readonly HashSet<string> ContrastRequiredModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "leonardo-phoenix", "leonardo-phoenix-0.9", "leonardo-flux-dev", "leonardo-flux-schnell"
        };

        // Models that do NOT support presetStyle
        private static readonly HashSet<string> NoPresetStyleModels = new(StringComparer.OrdinalIgnoreCase)
        {
            "leonardo-phoenix", "leonardo-phoenix-0.9", "leonardo-flux-dev", "leonardo-flux-schnell"
        };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Image" => await GenerateImageAsync(context),
                    "Video" => await GenerateVideoAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Leonardo AI")
                };
            }
            catch (OperationCanceledException)
            {
                throw; // Propagate cancellation so the executor can stop the pipeline cleanly.
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Leonardo AI: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var resp = await http.GetAsync($"{BaseUrl}/me");

                if (resp.IsSuccessStatusCode)
                    return (true, null);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "API Key de Leonardo AI invalida o expirada");
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, "API Key de Leonardo AI sin permisos o cuenta deshabilitada");

                var body = await resp.Content.ReadAsStringAsync();
                return (false, $"Error al validar Leonardo AI (HTTP {(int)resp.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo conectar con Leonardo AI: {ex.Message}");
            }
        }

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            // Resolve model UUID
            if (!ModelIdMap.TryGetValue(context.ModelName, out var modelUuid))
                return AiResult.Fail($"Modelo '{context.ModelName}' no reconocido en Leonardo AI");

            // Read configuration with defaults
            var width = 1024;
            var height = 1024;
            var presetStyle = "DYNAMIC";

            if (context.Configuration.TryGetValue("width", out var w))
                width = Convert.ToInt32(w);
            if (context.Configuration.TryGetValue("height", out var h))
                height = Convert.ToInt32(h);
            if (context.Configuration.TryGetValue("presetStyle", out var ps) && ps is string psStr)
                presetStyle = psStr;

            // Build prompt with spelling rule for rendered text
            var baseInput = context.Input;

            // Include systemPrompt from module config (e.g. branding instructions for image-to-image editing)
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp && !string.IsNullOrWhiteSpace(sp))
                baseInput = string.IsNullOrWhiteSpace(baseInput) ? sp : $"{sp}\n\n{baseInput}";

            var prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n{baseInput}";
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n{context.ProjectContext}\n\n{baseInput}";

            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            string? truncationWarning = null;
            if (prompt.Length > maxLen)
            {
                var originalLength = prompt.Length;
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);
                truncationWarning = InputAdapter.BuildTruncationWarning(context.ModelName, originalLength, maxLen);
            }

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

            // Read contrast (required for Phoenix/Flux models)
            var contrast = 3.5;
            if (context.Configuration.TryGetValue("contrast", out var c))
                contrast = Convert.ToDouble(c);

            // Upload init image if provided
            string? initImageId = null;
            if (context.InputFiles is { Count: > 0 })
            {
                initImageId = await UploadInitImageAsync(http, context.InputFiles[0]);
            }

            var numImages = ReadImageCount(context.Configuration);

            // Step 1: Submit generation request
            var body = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["modelId"] = modelUuid,
                ["width"] = width,
                ["height"] = height,
                ["num_images"] = numImages,
            };

            if (initImageId is not null)
            {
                body["init_image_id"] = initImageId;
                body["init_strength"] = 0.5;
            }

            // presetStyle is not supported by Phoenix/Flux models
            if (!NoPresetStyleModels.Contains(context.ModelName))
                body["presetStyle"] = presetStyle;

            if (ContrastRequiredModels.Contains(context.ModelName))
            {
                body["alchemy"] = true;
                body["contrast"] = contrast;
            }

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage submitResp;
            try
            {
                submitResp = await http.PostAsync($"{BaseUrl}/generations", content, context.CancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return AiResult.Fail($"Leonardo AI: error de conexion al enviar solicitud: {ex.Message}");
            }

            if (!submitResp.IsSuccessStatusCode)
            {
                var errorBody = await submitResp.Content.ReadAsStringAsync(context.CancellationToken);
                return AiResult.Fail($"Leonardo AI rechazo la solicitud (HTTP {(int)submitResp.StatusCode}): {errorBody}");
            }

            var submitJson = await submitResp.Content.ReadAsStringAsync(context.CancellationToken);
            var submitDoc = JsonDocument.Parse(submitJson);

            if (!submitDoc.RootElement.TryGetProperty("sdGenerationJob", out var job)
                || !job.TryGetProperty("generationId", out var genIdEl))
            {
                return AiResult.Fail("Respuesta inesperada de Leonardo AI: no se encontro generationId");
            }

            var generationId = genIdEl.GetString()!;

            // Step 2: Poll for completion
            const int maxAttempts = 40;
            const int pollIntervalMs = 3000;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs, context.CancellationToken);

                var pollResp = await http.GetAsync($"{BaseUrl}/generations/{generationId}", context.CancellationToken);
                if (!pollResp.IsSuccessStatusCode)
                    continue;

                var pollJson = await pollResp.Content.ReadAsStringAsync(context.CancellationToken);
                var pollDoc = JsonDocument.Parse(pollJson);

                if (!pollDoc.RootElement.TryGetProperty("generations_by_pk", out var generation))
                    continue;

                var status = generation.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() : null;

                if (status == "COMPLETE")
                {
                    if (!generation.TryGetProperty("generated_images", out var images)
                        || images.GetArrayLength() == 0)
                    {
                        return AiResult.Fail("Leonardo AI completo la generacion pero no devolvio imagenes");
                    }

                    // Download all returned images (CDN rejects auth headers — use a clean client)
                    using var dlClient = new HttpClient();
                    var imageBytesList = new List<byte[]>();
                    foreach (var img in images.EnumerateArray())
                    {
                        if (!img.TryGetProperty("url", out var urlEl)) continue;
                        var imgUrl = urlEl.GetString();
                        if (string.IsNullOrWhiteSpace(imgUrl)) continue;
                        var imgResp = await dlClient.GetAsync(imgUrl, context.CancellationToken);
                        if (!imgResp.IsSuccessStatusCode) continue;
                        imageBytesList.Add(await imgResp.Content.ReadAsByteArrayAsync(context.CancellationToken));
                    }

                    if (imageBytesList.Count == 0)
                        return AiResult.Fail("Leonardo AI: no se pudo descargar ninguna imagen");

                    var imgResult = AiResult.OkFiles(imageBytesList, "image/png", new Dictionary<string, object>
                    {
                        ["model"] = context.ModelName,
                        ["revisedPrompt"] = "",
                        ["count"] = imageBytesList.Count,
                    });
                    imgResult.EstimatedCost = PricingCatalog.EstimateImageCost(context.ModelName) * imageBytesList.Count;
                    imgResult.TruncationWarning = truncationWarning;
                    return imgResult;
                }

                if (status == "FAILED")
                {
                    return AiResult.Fail("Leonardo AI: la generacion de imagen fallo");
                }
            }

            return AiResult.Fail($"Timeout esperando generacion de Leonardo AI (>{maxAttempts * pollIntervalMs / 1000}s)");
        }

        private static int ReadImageCount(IDictionary<string, object> config)
        {
            int Parse(object? v) => v switch
            {
                int i => i,
                long l => (int)l,
                double d => (int)d,
                JsonElement je when je.TryGetInt32(out var ji) => ji,
                string s when int.TryParse(s, out var sp) => sp,
                _ => 1,
            };
            if (config.TryGetValue("num_images", out var niv)) return Math.Max(1, Parse(niv));
            if (config.TryGetValue("numberOfImages", out var num)) return Math.Max(1, Parse(num));
            if (config.TryGetValue("n", out var nv)) return Math.Max(1, Parse(nv));
            return 1;
        }

        /// <summary>
        /// Imagen -> video.
        ///
        /// Tres pasos, como la generacion de imagen pero con dos diferencias que
        /// importan: la imagen de partida SIEMPRE se sube (llega de cualquier
        /// modulo del pipeline, no tiene por que haberla generado Leonardo, asi que
        /// va como `UPLOADED` y no como "GENERATED"), y la espera es mucho mas larga
        /// que en imagen, asi que el polling tiene su propio presupuesto de tiempo.
        /// Lo que se manda (duracion, tamano, audio...) sale de
        /// <see cref="VideoModelOptions"/>, ya encajado en lo que admite el modelo.
        /// </summary>
        private async Task<AiResult> GenerateVideoAsync(AiExecutionContext context)
        {
            if (!VideoModelMap.TryGetValue(context.ModelName, out var videoModel)
                || VideoModelOptions.Find(context.ModelName) is not { } spec)
                return AiResult.Fail($"Modelo de video '{context.ModelName}' no reconocido en Leonardo AI");

            if (context.InputFiles is not { Count: > 0 })
                return AiResult.Fail(
                    "El modulo de video necesita una imagen de entrada. Conecta un modulo de "
                    + "imagen (o de archivos) a su puerto 'Imagen'.");

            var baseInput = context.Input;
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt)
                && sysPrompt is string sp && !string.IsNullOrWhiteSpace(sp))
                baseInput = string.IsNullOrWhiteSpace(baseInput) ? sp : $"{sp}\n\n{baseInput}";

            var prompt = baseInput?.Trim() ?? "";
            if (prompt.Length == 0) prompt = DefaultVideoPrompt;
            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            string? truncationWarning = null;
            if (prompt.Length > maxLen)
            {
                var originalLength = prompt.Length;
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);
                truncationWarning = InputAdapter.BuildTruncationWarning(context.ModelName, originalLength, maxLen);
            }

            var image = context.InputFiles[0];
            var (imageWidth, imageHeight) = ImageHeader.ReadDimensions(image);
            var options = VideoModelOptions.Resolve(spec, context.Configuration, (imageWidth, imageHeight));

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

            var imageId = await UploadInitImageAsync(http, image);
            if (imageId is null)
                return AiResult.Fail("Leonardo AI: no se pudo subir la imagen de partida del video");

            var body = BuildVideoRequest(videoModel, spec, options, prompt, imageId);
            var sentPayload = JsonSerializer.Serialize(body);
            var content = new StringContent(sentPayload, Encoding.UTF8, "application/json");

            HttpResponseMessage submitResp;
            try
            {
                submitResp = await http.PostAsync($"{BaseUrlV2}/generations", content, context.CancellationToken);
            }
            catch (HttpRequestException ex)
            {
                return AiResult.Fail($"Leonardo AI: error de conexion al pedir el video: {ex.Message}");
            }

            if (!submitResp.IsSuccessStatusCode)
            {
                var errorBody = await submitResp.Content.ReadAsStringAsync(context.CancellationToken);
                return AiResult.Fail(
                    $"Leonardo AI rechazo la solicitud de video (HTTP {(int)submitResp.StatusCode}): {errorBody}");
            }

            var submitJson = await submitResp.Content.ReadAsStringAsync(context.CancellationToken);
            using var submitDoc = JsonDocument.Parse(submitJson);

            // La v2 devuelve el id en la raiz y la v1 lo envolvia (sdGenerationJob,
            // motionVideoGenerationJob...). Buscar la propiedad por nombre a cualquier
            // profundidad evita atarse a una forma concreta que solo se descubre
            // cuando deja de funcionar en produccion.
            var generationId = FindStringProperty(submitDoc.RootElement, "generationId");
            if (string.IsNullOrWhiteSpace(generationId))
                return AiResult.Fail($"Respuesta inesperada de Leonardo AI: no se encontro generationId. {submitJson}");

            var realCost = ReadSubmitCost(submitDoc.RootElement);

            // Un clip tarda bastante mas que una imagen, y un Veo a 4K o un Seedance de
            // 15 s, mas que un Motion de 5 s. 8 min de presupuesto caben en el timeout
            // de 10 min por modulo del executor con margen para subir y bajar ficheros.
            const int maxAttempts = 96;
            const int pollIntervalMs = 5000;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs, context.CancellationToken);

                var pollResp = await http.GetAsync(
                    $"{BaseUrl}/generations/{generationId}", context.CancellationToken);
                if (!pollResp.IsSuccessStatusCode)
                    continue;

                var pollJson = await pollResp.Content.ReadAsStringAsync(context.CancellationToken);
                using var pollDoc = JsonDocument.Parse(pollJson);

                if (!pollDoc.RootElement.TryGetProperty("generations_by_pk", out var generation)
                    || generation.ValueKind != JsonValueKind.Object)
                    continue;

                var status = generation.TryGetProperty("status", out var statusEl)
                    ? statusEl.GetString() : null;

                if (status == "FAILED")
                    return AiResult.Fail("Leonardo AI: la generacion de video fallo");

                if (status != "COMPLETE")
                    continue;

                var videoUrl = FindVideoUrl(generation);
                if (string.IsNullOrWhiteSpace(videoUrl))
                    return AiResult.Fail("Leonardo AI completo la generacion pero no devolvio ningun video");

                // El CDN rechaza la cabecera de autorizacion: cliente limpio.
                using var dlClient = new HttpClient();
                var videoResp = await dlClient.GetAsync(videoUrl, context.CancellationToken);
                if (!videoResp.IsSuccessStatusCode)
                    return AiResult.Fail($"Leonardo AI: no se pudo descargar el video (HTTP {(int)videoResp.StatusCode})");

                var videoBytes = await videoResp.Content.ReadAsByteArrayAsync(context.CancellationToken);
                if (videoBytes.Length == 0)
                    return AiResult.Fail("Leonardo AI: el video descargado esta vacio");

                // La duracion es la que se pidio, ya encajada en las del modelo: se factura
                // por segundo y el orquestador loop cuenta con ella para calcular los clips.
                var result = AiResult.OkFile(videoBytes, "video/mp4", new Dictionary<string, object>
                {
                    ["model"] = context.ModelName,
                    ["generationId"] = generationId,
                    ["resolution"] = options.Resolution.Id,
                    ["width"] = options.Size.Width,
                    ["height"] = options.Size.Height,
                    ["durationSeconds"] = options.Duration,
                    ["audio"] = options.Audio ?? false,
                });

                // Si la API dijo cuanto costo, ese es el gasto REAL y manda sobre la
                // tarifa estimada del catalogo.
                if (realCost is { } cost)
                {
                    result.Metadata["apiCostUsd"] = cost;
                    result.EstimatedCost = cost;
                }
                else
                {
                    result.EstimatedCost = PricingCatalog.EstimateVideoCost(context.ModelName, options.Duration);
                }

                result.SentPayload = sentPayload;
                result.TruncationWarning = truncationWarning;
                return result;
            }

            return AiResult.Fail(
                $"Timeout esperando el video de Leonardo AI (>{maxAttempts * pollIntervalMs / 1000}s)");
        }

        /// <summary>
        /// Cuerpo de POST /v2/generations. Solo lleva los campos que el modelo admite:
        /// la v2 valida cada modelo contra su propio esquema y un campo de mas (la
        /// duracion en Motion, el audio en Hailuo 2.3) es un 400.
        /// </summary>
        public static Dictionary<string, object> BuildVideoRequest(
            string apiModel, VideoModelOptions.Spec spec, VideoModelOptions.Resolved options,
            string prompt, string imageId)
        {
            // La imagen de entrada es el primer fotograma, salvo en los modelos que no
            // lo tienen (Gemini Omni Flash), que solo aceptan imagenes de referencia.
            var image = new Dictionary<string, object>
            {
                ["image"] = new Dictionary<string, object> { ["id"] = imageId, ["type"] = "UPLOADED" },
            };
            var parameters = new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["quantity"] = 1,
                ["width"] = options.Size.Width,
                ["height"] = options.Size.Height,
                ["guidances"] = new Dictionary<string, object>
                {
                    [spec.ImageAsReference ? "image_reference" : "start_frame"] = new object[] { image },
                },
            };

            if (spec.SendDuration) parameters["duration"] = options.Duration;
            if (options.Audio is { } audio) parameters["motion_has_audio"] = audio;
            if (options.NegativePrompt is { } negative) parameters["negative_prompt"] = negative;
            if (options.Seed is { } seed) parameters["seed"] = seed;
            if (options.PromptEnhance is { } enhance) parameters["prompt_enhance"] = enhance ? "ON" : "OFF";
            if (options.FrameInterpolation is { } interpolation) parameters["frame_interpolation"] = interpolation;
            if (options.Quality is { } quality) parameters["quality"] = quality;

            return new Dictionary<string, object>
            {
                ["model"] = apiModel,
                ["public"] = false,
                ["parameters"] = parameters,
            };
        }

        /// <summary>
        /// Coste real en USD que declara la respuesta, o null si no dice nada. La v1
        /// lo manda en `cost` con su unidad (CREDITS, o DOLLARS en el plan de pago por
        /// uso) y la v2 en `apiCreditCost`, que llega vacio en pago por uso.
        /// </summary>
        public static decimal? ReadSubmitCost(JsonElement root)
        {
            if (FindObjectProperty(root, "cost") is { } cost
                && cost.TryGetProperty("amount", out var amountEl)
                && decimal.TryParse(
                    amountEl.ValueKind == JsonValueKind.Number ? amountEl.GetRawText() : amountEl.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var amount)
                && amount > 0)
            {
                var unit = cost.TryGetProperty("unit", out var unitEl) ? unitEl.GetString() : null;
                if (string.Equals(unit, "DOLLARS", StringComparison.OrdinalIgnoreCase))
                    return amount;
                if (string.Equals(unit, "CREDITS", StringComparison.OrdinalIgnoreCase))
                    return amount * PricingCatalog.LeonardoCreditUsd;
            }

            var credits = FindIntProperty(root, "apiCreditCost");
            return credits > 0 ? PricingCatalog.EstimateCostFromLeonardoCredits(credits) : null;
        }

        /// <summary>Primer objeto con ese nombre, a cualquier profundidad.</summary>
        private static JsonElement? FindObjectProperty(JsonElement element, string name)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.Object)
                            return prop.Value;

                        var nested = FindObjectProperty(prop.Value, name);
                        if (nested is not null) return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var nested = FindObjectProperty(item, name);
                        if (nested is not null) return nested;
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// URL del video dentro de la respuesta de polling. Se miran las tres formas
        /// que usa la API: el array propio de videos, el campo motionMP4URL que
        /// cuelga de cada imagen generada (herencia del Motion original) y, como
        /// ultimo recurso, cualquier URL que apunte a un .mp4.
        /// </summary>
        private static string? FindVideoUrl(JsonElement generation)
        {
            if (generation.TryGetProperty("generated_videos", out var videos)
                && videos.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in videos.EnumerateArray())
                {
                    var url = FindStringProperty(v, "url");
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }

            if (generation.TryGetProperty("generated_images", out var images)
                && images.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in images.EnumerateArray())
                {
                    var url = FindStringProperty(img, "motionMP4URL");
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }

            return FindUrlEndingIn(generation, ".mp4");
        }

        /// <summary>Primera propiedad con ese nombre y valor de cadena, a cualquier profundidad.</summary>
        private static string? FindStringProperty(JsonElement element, string name)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.NameEquals(name) && prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = prop.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) return value;
                        }

                        var nested = FindStringProperty(prop.Value, name);
                        if (nested is not null) return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var nested = FindStringProperty(item, name);
                        if (nested is not null) return nested;
                    }
                    break;
            }

            return null;
        }

        /// <summary>Primera propiedad numerica con ese nombre, a cualquier profundidad. 0 si no esta.</summary>
        private static int FindIntProperty(JsonElement element, string name)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.NameEquals(name)
                            && prop.Value.ValueKind == JsonValueKind.Number
                            && prop.Value.TryGetInt32(out var value))
                            return value;

                        var nested = FindIntProperty(prop.Value, name);
                        if (nested > 0) return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var nested = FindIntProperty(item, name);
                        if (nested > 0) return nested;
                    }
                    break;
            }

            return 0;
        }

        private static string? FindUrlEndingIn(JsonElement element, string suffix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var value = element.GetString();
                    return value is not null && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                        ? value
                        : null;

                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var nested = FindUrlEndingIn(prop.Value, suffix);
                        if (nested is not null) return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        var nested = FindUrlEndingIn(item, suffix);
                        if (nested is not null) return nested;
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// Uploads an init image to Leonardo AI via presigned URL and returns the init_image_id.
        /// </summary>
        private static async Task<string?> UploadInitImageAsync(HttpClient http, byte[] imageBytes)
        {
            // Step 1: Get presigned URL
            var reqBody = JsonSerializer.Serialize(new { extension = "png" });
            var reqContent = new StringContent(reqBody, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{BaseUrl}/init-image", reqContent);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("uploadInitImage", out var upload))
                return null;

            var id = upload.GetProperty("id").GetString();
            var url = upload.GetProperty("url").GetString();
            var fieldsStr = upload.GetProperty("fields").GetString();
            var key = upload.GetProperty("key").GetString();

            if (id is null || url is null || fieldsStr is null || key is null)
                return null;

            // Step 2: Upload to presigned URL (multipart form)
            var fields = JsonDocument.Parse(fieldsStr);
            using var formContent = new MultipartFormDataContent();

            foreach (var field in fields.RootElement.EnumerateObject())
            {
                formContent.Add(new StringContent(field.Value.GetString() ?? ""), field.Name);
            }

            formContent.Add(new ByteArrayContent(imageBytes), "file", "image.png");

            // Use a clean HttpClient without auth headers for S3 upload
            using var uploadClient = new HttpClient();
            var uploadResp = await uploadClient.PostAsync(url, formContent);

            return uploadResp.IsSuccessStatusCode || (int)uploadResp.StatusCode == 204
                ? id
                : null;
        }
    }
}
