using System.ClientModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using Server.Models;

namespace Server.Services.Ai
{
    public class GrokProvider : IAiProvider
    {
        private const string XaiBaseUrl = "https://api.x.ai/v1";

        public string ProviderType => "xAI";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Text", "Image" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Text" => await GenerateTextAsync(context),
                    "Image" => await GenerateImageAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por xAI Grok")
                };
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error xAI Grok: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                // Use a minimal chat completion to verify the key works.
                var options = new OpenAIClientOptions { Endpoint = new Uri(XaiBaseUrl) };
                var client = new ChatClient(
                    model: "grok-3-mini-fast",
                    credential: new ApiKeyCredential(apiKey),
                    options: options);

                var chatOptions = new ChatCompletionOptions { MaxOutputTokenCount = 5 };
                await client.CompleteChatAsync(
                    [new UserChatMessage("hi")], chatOptions);
                return (true, null);
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                if (msg.Contains("401") || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                    return (false, "API Key de xAI invalida o expirada");
                if (msg.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("429"))
                    return (false, "Sin creditos disponibles en xAI — revisa tu plan y facturacion");
                if (msg.Contains("403") || msg.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
                    return (false, "API Key de xAI sin permisos o cuenta deshabilitada");
                return (false, $"Error al validar xAI: {msg}");
            }
        }

        private async Task<AiResult> GenerateTextAsync(AiExecutionContext context)
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(XaiBaseUrl) };
            var client = new ChatClient(
                model: context.ModelName,
                credential: new ApiKeyCredential(context.ApiKey),
                options: options);

            var messages = new List<ChatMessage>();

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

            var chatOptions = new ChatCompletionOptions();

            if (context.Configuration.TryGetValue("temperature", out var temp))
                chatOptions.Temperature = Convert.ToSingle(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                chatOptions.MaxOutputTokenCount = Convert.ToInt32(maxTok);

            var completion = await client.CompleteChatAsync(messages, chatOptions);

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

        private static readonly Dictionary<string, string> DeprecatedImageModels = new()
        {
            ["grok-2-image"] = "grok-imagine-image",
            ["grok-2-image-1212"] = "grok-imagine-image"
        };

        private async Task<AiResult> GenerateImageAsync(AiExecutionContext context)
        {
            // Remap deprecated model names to their working replacements
            var modelName = DeprecatedImageModels.TryGetValue(context.ModelName, out var replacement)
                ? replacement
                : context.ModelName;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

            var baseInput = context.Input;

            // Include systemPrompt from module config
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp && !string.IsNullOrWhiteSpace(sp))
                baseInput = string.IsNullOrWhiteSpace(baseInput) ? sp : $"{sp}\n\n{baseInput}";

            var prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n{baseInput}";
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"{InputAdapter.GetVisualMediaRule()}\n\n[Contexto: {context.ProjectContext}]\n\n{baseInput}";

            var maxLen = InputAdapter.GetMaxPromptLength(modelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            var n = ReadImageCount(context.Configuration);

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = modelName,
                ["prompt"] = prompt,
                ["n"] = n,
                ["response_format"] = "b64_json"
            };

            if (context.Configuration.TryGetValue("size", out var size) && size is string sizeStr)
                requestBody["size"] = sizeStr;

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await http.PostAsync($"{XaiBaseUrl}/images/generations", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return AiResult.Fail($"xAI Grok Image HTTP {(int)response.StatusCode}: {responseJson}");

            using var doc = JsonDocument.Parse(responseJson);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return AiResult.Fail("xAI Grok Image: no se recibieron imagenes");

            var images = new List<byte[]>();
            var revisedPrompt = "";
            foreach (var item in data.EnumerateArray())
            {
                if (string.IsNullOrEmpty(revisedPrompt) && item.TryGetProperty("revised_prompt", out var rp))
                    revisedPrompt = rp.GetString() ?? "";

                if (item.TryGetProperty("b64_json", out var b64Prop))
                    images.Add(Convert.FromBase64String(b64Prop.GetString()!));
                else if (item.TryGetProperty("url", out var urlProp))
                    images.Add(await http.GetByteArrayAsync(urlProp.GetString()!));
            }

            images = images.Where(b => b.Length > 0).ToList();
            if (images.Count == 0)
                return AiResult.Fail("xAI Grok Image: formato de respuesta inesperado");

            var imgResult = AiResult.OkFiles(images, "image/png", new Dictionary<string, object>
            {
                ["model"] = modelName,
                ["revisedPrompt"] = revisedPrompt,
                ["count"] = images.Count,
            });
            imgResult.EstimatedCost = PricingCatalog.EstimateImageCost(modelName, context.Configuration) * images.Count;
            return imgResult;
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
            if (config.TryGetValue("n", out var nv)) return Math.Max(1, Parse(nv));
            if (config.TryGetValue("numberOfImages", out var niv)) return Math.Max(1, Parse(niv));
            return 1;
        }
    }
}
