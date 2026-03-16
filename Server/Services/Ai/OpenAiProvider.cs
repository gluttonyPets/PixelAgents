using System.Net.Http.Headers;
using System.Text.Json;
using OpenAI.Chat;
using OpenAI.Images;
using Server.Models;

namespace Server.Services.Ai
{
    public class OpenAiProvider : IAiProvider
    {
        public string ProviderType => "OpenAI";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Text", "Image" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Text" => await GenerateTextAsync(context),
                    "Image" => await GenerateImageAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por OpenAI")
                };
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error OpenAI: {ex.Message}");
            }
        }

        private async Task<AiResult> GenerateTextAsync(AiExecutionContext context)
        {
            var client = new ChatClient(model: context.ModelName, apiKey: context.ApiKey);

            var messages = new List<ChatMessage>();

            var systemParts = new List<string>();
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add($"[INSTRUCCION PRINCIPAL - Esta es tu directiva prioritaria, sigue estas instrucciones por encima de cualquier otra regla]\n{sp}");
            systemParts.Add(OutputSchemaHelper.GetTextOutputInstruction());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (!string.IsNullOrWhiteSpace(context.PreviousExecutionsSummary))
                systemParts.Add(context.PreviousExecutionsSummary);
            messages.Add(new SystemChatMessage(string.Join("\n\n", systemParts)));

            if (context.InputFiles is { Count: > 0 })
            {
                var parts = new List<ChatMessageContentPart>();
                parts.Add(ChatMessageContentPart.CreateTextPart(context.Input));
                foreach (var fileBytes in context.InputFiles)
                {
                    parts.Add(ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(fileBytes), "image/png"));
                }
                messages.Add(new UserChatMessage(parts));
            }
            else
            {
                messages.Add(new UserChatMessage(context.Input));
            }

            var options = new ChatCompletionOptions();

            if (context.Configuration.TryGetValue("temperature", out var temp))
                options.Temperature = Convert.ToSingle(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                options.MaxOutputTokenCount = Convert.ToInt32(maxTok);

            var completion = await client.CompleteChatAsync(messages, options);

            var text = completion.Value.Content[0].Text;

            var inputTokens = completion.Value.Usage.InputTokenCount;
            var outputTokens = completion.Value.Usage.OutputTokenCount;

            return new AiResult
            {
                Success = true,
                TextOutput = text,
                EstimatedCost = PricingCatalog.EstimateTextCost(context.ModelName, inputTokens, outputTokens),
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = context.ModelName,
                    ["inputTokens"] = inputTokens,
                    ["outputTokens"] = outputTokens,
                }
            };
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                // Use a minimal chat completion (max_tokens=1) to verify the key has quota.
                // GET /v1/models returns 200 even with insufficient_quota.
                var client = new ChatClient(model: "gpt-5-nano", apiKey: apiKey);
                var options = new ChatCompletionOptions { MaxOutputTokenCount = 5 };
                await client.CompleteChatAsync(
                    [new UserChatMessage("hi")], options);
                return (true, null);
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("401") || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    return (false, "API Key de OpenAI invalida o expirada");
                if (msg.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("429"))
                    return (false, "Sin creditos disponibles en OpenAI — revisa tu plan y facturacion");
                return (false, $"Error al validar OpenAI: {msg}");
            }
        }

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            var client = new ImageClient(model: context.ModelName, apiKey: context.ApiKey);

            var options = new ImageGenerationOptions();

            var isGptImage = context.ModelName.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
            var isDallE2 = context.ModelName.Equals("dall-e-2", StringComparison.OrdinalIgnoreCase);

            // Size: each model family supports different sizes
            var sizeStr = "auto"; // default for gpt-image
            if (context.Configuration.TryGetValue("size", out var size) && size is string s)
                sizeStr = s;

            if (isGptImage)
            {
                // gpt-image: 1024x1024, 1536x1024, 1024x1536, auto
                // "auto" (or unset) preserves input image dimensions in edit mode
                if (sizeStr != "auto")
                {
                    options.Size = sizeStr switch
                    {
                        "1024x1024" => GeneratedImageSize.W1024xH1024,
                        "1536x1024" => new GeneratedImageSize(1536, 1024),
                        "1024x1536" => new GeneratedImageSize(1024, 1536),
                        _ => GeneratedImageSize.W1024xH1024
                    };
                }
                // When "auto": don't set Size — the API defaults to auto for gpt-image
            }
            else if (isDallE2)
            {
                // dall-e-2: 256x256, 512x512, 1024x1024
                options.Size = sizeStr switch
                {
                    "256x256" => GeneratedImageSize.W256xH256,
                    "512x512" => GeneratedImageSize.W512xH512,
                    "1024x1024" => GeneratedImageSize.W1024xH1024,
                    _ => GeneratedImageSize.W1024xH1024
                };
            }
            else if (context.Configuration.ContainsKey("size"))
            {
                // dall-e-3: 1024x1024, 1792x1024, 1024x1792
                options.Size = sizeStr switch
                {
                    "1024x1024" => GeneratedImageSize.W1024xH1024,
                    "1792x1024" => GeneratedImageSize.W1792xH1024,
                    "1024x1792" => GeneratedImageSize.W1024xH1792,
                    _ => GeneratedImageSize.W1024xH1024
                };
            }

            // Quality: dall-e-2 doesn't support quality; gpt-image uses different values
            if (!isDallE2 && context.Configuration.TryGetValue("quality", out var quality) && quality is string q)
            {
                if (isGptImage)
                {
                    // gpt-image: low, medium, high, auto
                    options.Quality = q switch
                    {
                        "low" => new GeneratedImageQuality("low"),
                        "medium" => new GeneratedImageQuality("medium"),
                        "high" => GeneratedImageQuality.High,
                        "auto" => new GeneratedImageQuality("auto"),
                        "hd" => GeneratedImageQuality.High,
                        _ => GeneratedImageQuality.High
                    };
                }
                else
                {
                    // dall-e-3: standard, hd
                    options.Quality = q switch
                    {
                        "hd" => GeneratedImageQuality.High,
                        _ => GeneratedImageQuality.Standard
                    };
                }
            }

            // gpt-image models don't support response_format — only set for dall-e
            if (!isGptImage)
            {
                options.ResponseFormat = GeneratedImageFormat.Bytes;
            }

            var baseInput = context.Input;

            // Include systemPrompt from module config (e.g. branding instructions for image-to-image editing)
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp && !string.IsNullOrWhiteSpace(sp))
                baseInput = string.IsNullOrWhiteSpace(baseInput) ? sp : $"{sp}\n\n{baseInput}";

            var prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n{baseInput}";
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n[Contexto: {context.ProjectContext}]\n\n{baseInput}";

            // Truncar al máximo del modelo como red de seguridad
            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            byte[] imageBytes;
            string revisedPrompt = "";

            if (context.InputFiles is { Count: > 0 } && isGptImage && sizeStr == "auto")
            {
                // "auto" doesn't reliably preserve aspect ratio — detect input dimensions
                // and pick the closest supported gpt-image size instead.
                var bestSize = DetectBestGptImageSize(context.InputFiles[0]);
                (imageBytes, revisedPrompt) = await CallImageEditRawAsync(
                    context.ApiKey, context.ModelName, context.InputFiles[0], prompt, context.Configuration, bestSize);
            }
            else if (context.InputFiles is { Count: > 0 } && isGptImage)
            {
                // Image editing with an explicit size the SDK supports
                using var imageStream = new MemoryStream(context.InputFiles[0]);
                var editOptions = new ImageEditOptions { Size = options.Size };
                var editResult = await client.GenerateImageEditAsync(
                    imageStream, "input.png", prompt, editOptions);
                var gi = editResult.Value;
                revisedPrompt = gi.RevisedPrompt ?? "";
                imageBytes = await ExtractImageBytesAsync(gi);
            }
            else
            {
                var result = await client.GenerateImageAsync(prompt, options);
                var gi = result.Value;
                revisedPrompt = gi.RevisedPrompt ?? "";
                imageBytes = await ExtractImageBytesAsync(gi);
            }

            if (imageBytes.Length == 0)
                return AiResult.Fail("No se recibieron datos de imagen del modelo");

            var imgResult = AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["revisedPrompt"] = revisedPrompt
            });
            imgResult.EstimatedCost = PricingCatalog.EstimateImageCost(context.ModelName, context.Configuration);
            return imgResult;
        }

        /// <summary>
        /// Extracts image bytes from a GeneratedImage (handles both inline bytes and URL).
        /// </summary>
        private static async Task<byte[]> ExtractImageBytesAsync(GeneratedImage gi)
        {
            if (gi.ImageBytes is not null && gi.ImageBytes.ToArray().Length > 0)
                return gi.ImageBytes.ToArray();

            if (!string.IsNullOrEmpty(gi.ImageUri?.ToString()))
            {
                using var http = new HttpClient();
                return await http.GetByteArrayAsync(gi.ImageUri);
            }

            return Array.Empty<byte>();
        }

        /// <summary>
        /// Detects the aspect ratio of an input image and returns the closest
        /// supported gpt-image size string (e.g. "1024x1536" for portrait).
        /// Falls back to "auto" if dimensions cannot be read.
        /// </summary>
        private static string DetectBestGptImageSize(byte[] imageBytes)
        {
            try
            {
                var (w, h) = ReadImageDimensions(imageBytes);
                if (w <= 0 || h <= 0) return "auto";

                var ratio = (double)w / h;

                // Supported sizes: 1024x1024 (1.0), 1536x1024 (1.5), 1024x1536 (0.667)
                // Pick the closest aspect ratio
                var candidates = new (string Size, double Ratio)[]
                {
                    ("1024x1024", 1.0),
                    ("1536x1024", 1.5),    // landscape 3:2
                    ("1024x1536", 0.6667), // portrait 2:3
                };

                var best = candidates[0];
                var bestDiff = double.MaxValue;
                foreach (var c in candidates)
                {
                    var diff = Math.Abs(ratio - c.Ratio);
                    if (diff < bestDiff) { bestDiff = diff; best = c; }
                }
                return best.Size;
            }
            catch
            {
                return "auto";
            }
        }

        /// <summary>
        /// Reads width and height from a PNG or JPEG image header without loading the full image.
        /// </summary>
        private static (int Width, int Height) ReadImageDimensions(byte[] data)
        {
            if (data.Length < 24) return (0, 0);

            // PNG: bytes 0-7 = signature, IHDR chunk at byte 16: width(4) height(4)
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                var w = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
                var h = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
                return (w, h);
            }

            // JPEG: scan for SOF0 (0xFF 0xC0) or SOF2 (0xFF 0xC2) marker
            if (data[0] == 0xFF && data[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < data.Length)
                {
                    if (data[i] != 0xFF) { i++; continue; }
                    var marker = data[i + 1];
                    if (marker == 0xC0 || marker == 0xC2)
                    {
                        var h = (data[i + 5] << 8) | data[i + 6];
                        var w = (data[i + 7] << 8) | data[i + 8];
                        return (w, h);
                    }
                    // Skip to next marker
                    var len = (data[i + 2] << 8) | data[i + 3];
                    i += 2 + len;
                }
            }

            // WebP: RIFF header, 'WEBP' at byte 8, VP8 at byte 12
            if (data.Length > 30 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            {
                // VP8L (lossless)
                if (data[12] == 0x56 && data[13] == 0x50 && data[14] == 0x38 && data[15] == 0x4C && data.Length > 25)
                {
                    var bits = (uint)data[21] | ((uint)data[22] << 8) | ((uint)data[23] << 16) | ((uint)data[24] << 24);
                    var w = (int)(bits & 0x3FFF) + 1;
                    var h = (int)((bits >> 14) & 0x3FFF) + 1;
                    return (w, h);
                }
                // VP8 (lossy) — frame header at byte 23+
                if (data[12] == 0x56 && data[13] == 0x50 && data[14] == 0x38 && data[15] == 0x20 && data.Length > 29)
                {
                    var w = (data[26] | (data[27] << 8)) & 0x3FFF;
                    var h = (data[28] | (data[29] << 8)) & 0x3FFF;
                    return (w, h);
                }
            }

            return (0, 0);
        }

        /// <summary>
        /// Calls the OpenAI image edit API directly via HttpClient,
        /// passing an explicit size to preserve aspect ratio from the input image.
        /// </summary>
        private static async Task<(byte[] ImageBytes, string RevisedPrompt)> CallImageEditRawAsync(
            string apiKey, string model, byte[] inputImage, string prompt,
            IDictionary<string, object> config, string sizeOverride = "auto")
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(prompt), "prompt");
            form.Add(new StringContent(sizeOverride), "size");

            // Quality
            if (config.TryGetValue("quality", out var q) && q is string qs && !string.IsNullOrWhiteSpace(qs))
                form.Add(new StringContent(qs), "quality");

            // Image file
            var imageContent = new ByteArrayContent(inputImage);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(imageContent, "image[]", "input.png");

            var response = await http.PostAsync("https://api.openai.com/v1/images/edits", form);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI image edit failed ({response.StatusCode}): {json}");

            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data")[0];

            var revisedPrompt = data.TryGetProperty("revised_prompt", out var rp) ? rp.GetString() ?? "" : "";

            // gpt-image returns URL
            if (data.TryGetProperty("url", out var urlProp))
            {
                var url = urlProp.GetString()!;
                var bytes = await http.GetByteArrayAsync(url);
                return (bytes, revisedPrompt);
            }

            // or base64
            if (data.TryGetProperty("b64_json", out var b64Prop))
            {
                var bytes = Convert.FromBase64String(b64Prop.GetString()!);
                return (bytes, revisedPrompt);
            }

            return (Array.Empty<byte>(), revisedPrompt);
        }
    }
}
