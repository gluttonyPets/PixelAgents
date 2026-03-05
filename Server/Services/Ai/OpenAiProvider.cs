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
            systemParts.Add(OutputSchemaHelper.GetTextOutputInstruction());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add(sp);
            messages.Add(new SystemChatMessage(string.Join("\n\n", systemParts)));

            messages.Add(new UserChatMessage(context.Input));

            var options = new ChatCompletionOptions();

            if (context.Configuration.TryGetValue("temperature", out var temp))
                options.Temperature = Convert.ToSingle(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                options.MaxOutputTokenCount = Convert.ToInt32(maxTok);

            var completion = await client.CompleteChatAsync(messages, options);

            var text = completion.Value.Content[0].Text;

            return AiResult.Ok(text, new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["inputTokens"] = completion.Value.Usage.InputTokenCount,
                ["outputTokens"] = completion.Value.Usage.OutputTokenCount,
            });
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                var resp = await http.GetAsync("https://api.openai.com/v1/models");
                if (resp.IsSuccessStatusCode)
                    return (true, null);

                var body = await resp.Content.ReadAsStringAsync();
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "API Key de OpenAI invalida o expirada");
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return (false, "Sin creditos o limite de uso alcanzado en OpenAI");
                return (false, $"Error al validar API Key de OpenAI: {resp.StatusCode}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo conectar con OpenAI: {ex.Message}");
            }
        }

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            var client = new ImageClient(model: context.ModelName, apiKey: context.ApiKey);

            var options = new ImageGenerationOptions();

            var isGptImage = context.ModelName.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
            var isDallE2 = context.ModelName.Equals("dall-e-2", StringComparison.OrdinalIgnoreCase);

            // Size: each model family supports different sizes
            if (context.Configuration.TryGetValue("size", out var size) && size is string sizeStr)
            {
                if (isGptImage)
                {
                    // gpt-image: 1024x1024, 1536x1024, 1024x1536
                    options.Size = sizeStr switch
                    {
                        "1024x1024" => GeneratedImageSize.W1024xH1024,
                        "1536x1024" => new GeneratedImageSize(1536, 1024),
                        "1024x1536" => new GeneratedImageSize(1024, 1536),
                        _ => GeneratedImageSize.W1024xH1024
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
                else
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

            var prompt = context.Input;
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"[Contexto: {context.ProjectContext}]\n\n{prompt}";

            // Truncar al máximo del modelo como red de seguridad
            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            var result = await client.GenerateImageAsync(prompt, options);

            byte[] imageBytes;
            if (result.Value.ImageBytes is not null && result.Value.ImageBytes.ToArray().Length > 0)
            {
                imageBytes = result.Value.ImageBytes.ToArray();
            }
            else if (!string.IsNullOrEmpty(result.Value.ImageUri?.ToString()))
            {
                // gpt-image models return a URL — download the image
                using var httpClient = new HttpClient();
                imageBytes = await httpClient.GetByteArrayAsync(result.Value.ImageUri);
            }
            else
            {
                return AiResult.Fail("No se recibieron datos de imagen del modelo");
            }

            return AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["revisedPrompt"] = result.Value.RevisedPrompt ?? ""
            });
        }
    }
}
