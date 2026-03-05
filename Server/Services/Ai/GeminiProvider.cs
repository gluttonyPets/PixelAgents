using Google.GenAI;
using Google.GenAI.Types;
using Server.Models;

namespace Server.Services.Ai
{
    public class GeminiProvider : IAiProvider
    {
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
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Google Gemini: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                var client = new Client(apiKey: apiKey);
                await client.Models.GenerateContentAsync(
                    model: "gemini-2.0-flash",
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
            systemParts.Add(OutputSchemaHelper.GetTextOutputInstruction());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add(sp);

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

            var response = await client.Models.GenerateContentAsync(
                model: context.ModelName,
                contents: context.Input,
                config: config
            );

            var text = response.Candidates?[0].Content?.Parts?[0].Text
                ?? throw new InvalidOperationException("Gemini no devolvio texto en la respuesta");

            var metadata = new Dictionary<string, object>
            {
                ["model"] = context.ModelName
            };

            if (response.UsageMetadata is not null)
            {
                metadata["inputTokens"] = response.UsageMetadata.PromptTokenCount ?? 0;
                metadata["outputTokens"] = response.UsageMetadata.CandidatesTokenCount ?? 0;
            }

            return AiResult.Ok(text, metadata);
        }

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            var client = new Client(apiKey: context.ApiKey);

            var prompt = context.Input;
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"[Contexto: {context.ProjectContext}]\n\n{prompt}";

            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            var aspectRatio = "1:1";
            if (context.Configuration.TryGetValue("aspectRatio", out var ar) && ar is string arStr)
                aspectRatio = arStr;

            var numberOfImages = 1;
            if (context.Configuration.TryGetValue("numberOfImages", out var ni))
                numberOfImages = Convert.ToInt32(ni);

            var config = new GenerateImagesConfig
            {
                NumberOfImages = numberOfImages,
                AspectRatio = aspectRatio,
                PersonGeneration = PersonGeneration.AllowAll,
            };

            var response = await client.Models.GenerateImagesAsync(
                model: context.ModelName,
                prompt: prompt,
                config: config
            );

            if (response.GeneratedImages is null || response.GeneratedImages.Count == 0)
                return AiResult.Fail("Google Imagen no devolvio imagenes");

            var imageData = response.GeneratedImages[0];

            if (imageData.Image?.ImageBytes is null || imageData.Image.ImageBytes.Length == 0)
                return AiResult.Fail("Google Imagen no devolvio datos de imagen");

            var imageBytes = imageData.Image.ImageBytes;

            return AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["revisedPrompt"] = ""
            });
        }
    }
}
