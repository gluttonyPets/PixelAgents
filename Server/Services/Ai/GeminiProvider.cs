using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.GenAI;
using Google.GenAI.Types;
using Server.Models;

namespace Server.Services.Ai
{
    public class GeminiProvider : IAiProvider
    {
        private static readonly TimeSpan TextTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan ImageTimeout = TimeSpan.FromMinutes(3);

        public string ProviderType => "Google";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Text", "Image" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Text" => await GenerateTextAsync(context),
                    "Image" => await GenerateImageAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Google Gemini")
                };
            }
            catch (OperationCanceledException)
            {
                return AiResult.Fail("Operación cancelada por el usuario");
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Google Gemini: {ex.Message}");
            }
        }

        /// <summary>
        /// Executes an async task with timeout and cancellation support.
        /// </summary>
        private static async Task<T> ExecuteWithTimeoutAsync<T>(
            Task<T> task,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            
            var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, linkedCts.Token));
            
            if (completedTask != task)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException("Operación cancelada por el usuario", cancellationToken);
                throw new TimeoutException($"La operación excedió el tiempo límite de {timeout.TotalMinutes:F1} minutos");
            }
            
            return await task;
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                var client = new Client(apiKey: apiKey);
                await client.Models.GenerateContentAsync(
                    model: "gemini-2.5-flash",
                    contents: "hi"
                );

                return (true, null);
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("401") || msg.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                    return (false, "API Key de Google Gemini invalida o expirada");
                if (msg.Contains("429") || msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase))
                    return (false, "Sin cuota disponible en Google Gemini — revisa tu plan y facturacion");
                if (msg.Contains("403") || msg.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase))
                    return (false, "API Key de Google sin permisos para Gemini API");
                return (false, $"Error al validar Google Gemini: {msg}");
            }
        }

        private async Task<AiResult> GenerateTextAsync(AiExecutionContext context)
        {
            var client = new Client(apiKey: context.ApiKey);

            var systemParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.MandatoryRules))
                systemParts.Add(context.MandatoryRules);
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add($"[INSTRUCCION PRINCIPAL - Esta es tu directiva prioritaria, sigue estas instrucciones por encima de cualquier otra regla]\n{sp}");
            systemParts.Add(OutputSchemaHelper.GetTextContentRules());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (!string.IsNullOrWhiteSpace(context.PreviousExecutionsSummary))
                systemParts.Add(context.PreviousExecutionsSummary);

            var config = new GenerateContentConfig
            {
                SystemInstruction = new Content
                {
                    Parts = [new Part { Text = string.Join("\n\n", systemParts) }]
                }
            };

            if (context.Configuration.TryGetValue("temperature", out var temp))
                config.Temperature = Convert.ToSingle(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                config.MaxOutputTokens = Convert.ToInt32(maxTok);

            GenerateContentResponse response;
            if (context.InputFiles is { Count: > 0 })
            {
                var parts = new List<Part> { new Part { Text = context.Input } };
                foreach (var fileBytes in context.InputFiles)
                {
                    parts.Add(new Part
                    {
                        InlineData = new Blob
                        {
                            MimeType = "image/png",
                            Data = fileBytes
                        }
                    });
                }
                response = await ExecuteWithTimeoutAsync(
                    client.Models.GenerateContentAsync(
                        model: context.ModelName,
                        contents: new Content { Parts = parts, Role = "user" },
                        config: config
                    ),
                    TextTimeout,
                    context.CancellationToken
                );
            }
            else
            {
                response = await ExecuteWithTimeoutAsync(
                    client.Models.GenerateContentAsync(
                        model: context.ModelName,
                        contents: context.Input,
                        config: config
                    ),
                    TextTimeout,
                    context.CancellationToken
                );
            }

            var text = response.Candidates?[0].Content?.Parts?[0].Text
                ?? throw new InvalidOperationException("Gemini no devolvio texto en la respuesta");

            var inputTokens = response.UsageMetadata?.PromptTokenCount ?? 0;
            var outputTokens = response.UsageMetadata?.CandidatesTokenCount ?? 0;

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

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            var client = new Client(apiKey: context.ApiKey);

            // Use the model selected by the user in the module configuration
            var imageModel = context.ModelName;

            var prompt = context.Input;

            // Include systemPrompt from module config (e.g. branding instructions for image-to-image editing)
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp && !string.IsNullOrWhiteSpace(sp))
                prompt = string.IsNullOrWhiteSpace(prompt) ? sp : $"{sp}\n\n{prompt}";

            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"[Contexto: {context.ProjectContext}]\n\n{prompt}";

            // Prepend instruction to ensure Gemini generates an image, not text
            prompt = $"{InputAdapter.GetVisualMediaRule()}\n\nGenerate an image based on this description: {prompt}";

            var maxLen = InputAdapter.GetMaxPromptLength(imageModel);
            string? truncationWarning = null;
            if (prompt.Length > maxLen)
            {
                var originalLength = prompt.Length;
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);
                truncationWarning = InputAdapter.BuildTruncationWarning(imageModel, originalLength, maxLen);
            }

            var requestedN = ReadImageCount(context.Configuration);

            // Read aspect ratio from module configuration
            var aspectRatio = "1:1";
            if (context.Configuration.TryGetValue("aspectRatio", out var ar) && ar is string arStr)
                aspectRatio = arStr;

            var config = new GenerateContentConfig
            {
                ResponseModalities = ["IMAGE"],
                ImageConfig = new ImageConfig
                {
                    AspectRatio = aspectRatio,
                },
            };

            const int maxRetries = 2;
            var collected = new List<byte[]>();
            string? collectedMime = null;
            string? lastFailure = null;

            // Gemini has no native batch parameter for image generation, so we loop
            // internally but append a variation hint on each iteration so the model
            // produces distinct, non-repeated images.
            for (int imageIndex = 0; imageIndex < requestedN; imageIndex++)
            {
                var promptForThis = requestedN > 1
                    ? $"{prompt}\n\n[Variation {imageIndex + 1} of {requestedN}: produce a visually distinct image, different from the previous variations.]"
                    : prompt;

                byte[]? thisImage = null;
                string? thisMime = null;

                for (int attempt = 0; attempt <= maxRetries; attempt++)
                {
                    GenerateContentResponse response;

                    if (context.InputFiles is { Count: > 0 })
                    {
                        var inputParts = new List<Part> { new Part { Text = promptForThis } };
                        foreach (var fileBytes in context.InputFiles)
                        {
                            inputParts.Add(new Part
                            {
                                InlineData = new Blob
                                {
                                    MimeType = "image/png",
                                    Data = fileBytes
                                }
                            });
                        }
                        response = await ExecuteWithTimeoutAsync(
                            client.Models.GenerateContentAsync(
                                model: imageModel,
                                contents: new Content { Parts = inputParts, Role = "user" },
                                config: config
                            ),
                            ImageTimeout,
                            context.CancellationToken
                        );
                    }
                    else
                    {
                        response = await ExecuteWithTimeoutAsync(
                            client.Models.GenerateContentAsync(
                                model: imageModel,
                                contents: promptForThis,
                                config: config
                            ),
                            ImageTimeout,
                            context.CancellationToken
                        );
                    }

                    if (response.Candidates is null || response.Candidates.Count == 0)
                    {
                        lastFailure = "Google Gemini no devolvio respuesta para la imagen";
                        if (attempt < maxRetries) { await Task.Delay(1000 * (attempt + 1), context.CancellationToken); continue; }
                        break;
                    }

                    var parts = response.Candidates[0].Content?.Parts;
                    if (parts is null)
                    {
                        lastFailure = "Google Gemini no devolvio partes en la respuesta";
                        if (attempt < maxRetries) { await Task.Delay(1000 * (attempt + 1), context.CancellationToken); continue; }
                        break;
                    }

                    foreach (var part in parts)
                    {
                        if (part.InlineData is not null && part.InlineData.MimeType?.StartsWith("image/") == true)
                        {
                            thisImage = part.InlineData.Data;
                            thisMime = part.InlineData.MimeType;
                            break;
                        }
                    }

                    if (thisImage is not null) break;

                    var textReason = string.Join(" ", parts
                        .Where(p => !string.IsNullOrEmpty(p.Text))
                        .Select(p => p.Text));
                    lastFailure = string.IsNullOrWhiteSpace(textReason)
                        ? "Google Gemini no devolvio imagen tras varios intentos"
                        : $"Google Gemini no genero imagen: {textReason}";

                    if (attempt < maxRetries) { await Task.Delay(1000 * (attempt + 1), context.CancellationToken); continue; }
                }

                if (thisImage is not null)
                {
                    collected.Add(thisImage);
                    collectedMime ??= thisMime;
                }
            }

            if (collected.Count == 0)
                return AiResult.Fail(lastFailure ?? "Google Gemini no devolvio imagen tras varios intentos");

            var result = AiResult.OkFiles(collected, collectedMime ?? "image/png", new Dictionary<string, object>
            {
                ["model"] = imageModel,
                ["revisedPrompt"] = "",
                ["count"] = collected.Count,
            });
            result.EstimatedCost = PricingCatalog.EstimateImageCost(imageModel) * collected.Count;
            result.TruncationWarning = truncationWarning;
            return result;
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
            if (config.TryGetValue("numberOfImages", out var niv)) return Math.Max(1, Parse(niv));
            if (config.TryGetValue("n", out var nv)) return Math.Max(1, Parse(nv));
            return 1;
        }
    }
}
