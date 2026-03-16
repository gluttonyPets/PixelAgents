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
                // "auto" preserves input image dimensions in edit mode
                options.Size = sizeStr switch
                {
                    "1024x1024" => GeneratedImageSize.W1024xH1024,
                    "1536x1024" => new GeneratedImageSize(1536, 1024),
                    "1024x1536" => new GeneratedImageSize(1024, 1536),
                    "auto" => new GeneratedImageSize("auto"),
                    _ => new GeneratedImageSize("auto")
                };
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

            GeneratedImage generatedImage;

            if (context.InputFiles is { Count: > 0 } && isGptImage)
            {
                // Image editing: use the first input file as reference
                using var imageStream = new MemoryStream(context.InputFiles[0]);
                var editOptions = new ImageEditOptions
                {
                    Size = options.Size,
                };
                var editResult = await client.GenerateImageEditAsync(
                    imageStream, "input.png", prompt, editOptions);
                generatedImage = editResult.Value;
            }
            else
            {
                var result = await client.GenerateImageAsync(prompt, options);
                generatedImage = result.Value;
            }

            byte[] imageBytes;
            if (generatedImage.ImageBytes is not null && generatedImage.ImageBytes.ToArray().Length > 0)
            {
                imageBytes = generatedImage.ImageBytes.ToArray();
            }
            else if (!string.IsNullOrEmpty(generatedImage.ImageUri?.ToString()))
            {
                // gpt-image models return a URL — download the image
                using var httpClient = new HttpClient();
                imageBytes = await httpClient.GetByteArrayAsync(generatedImage.ImageUri);
            }
            else
            {
                return AiResult.Fail("No se recibieron datos de imagen del modelo");
            }

            var imgResult = AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["revisedPrompt"] = generatedImage.RevisedPrompt ?? ""
            });
            imgResult.EstimatedCost = PricingCatalog.EstimateImageCost(context.ModelName, context.Configuration);
            return imgResult;
        }
    }
}
