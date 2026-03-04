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

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            var client = new ImageClient(model: context.ModelName, apiKey: context.ApiKey);

            var options = new ImageGenerationOptions();

            if (context.Configuration.TryGetValue("size", out var size) && size is string sizeStr)
            {
                options.Size = sizeStr switch
                {
                    "256x256" => GeneratedImageSize.W256xH256,
                    "512x512" => GeneratedImageSize.W512xH512,
                    "1024x1024" => GeneratedImageSize.W1024xH1024,
                    "1792x1024" => GeneratedImageSize.W1792xH1024,
                    "1024x1792" => GeneratedImageSize.W1024xH1792,
                    _ => GeneratedImageSize.W1024xH1024
                };
            }

            if (context.Configuration.TryGetValue("quality", out var quality) && quality is string q)
            {
                options.Quality = q switch
                {
                    "hd" => GeneratedImageQuality.High,
                    _ => GeneratedImageQuality.Standard
                };
            }

            options.ResponseFormat = GeneratedImageFormat.Bytes;

            var prompt = context.Input;
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"[Contexto: {context.ProjectContext}]\n\n{prompt}";

            // Truncar al máximo del modelo como red de seguridad
            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            var result = await client.GenerateImageAsync(prompt, options);

            var imageBytes = result.Value.ImageBytes.ToArray();

            return AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["revisedPrompt"] = result.Value.RevisedPrompt ?? ""
            });
        }
    }
}
