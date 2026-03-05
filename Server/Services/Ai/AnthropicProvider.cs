using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Server.Models;

namespace Server.Services.Ai
{
    public class AnthropicProvider : IAiProvider
    {
        public string ProviderType => "Anthropic";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Text" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Text" => await GenerateTextAsync(context),
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Anthropic")
                };
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Anthropic: {ex.Message}");
            }
        }

        public async Task<(bool Valid, string? Error)> ValidateKeyAsync(string apiKey)
        {
            try
            {
                // GET /v1/models?limit=1 — lightweight, validates key + credits.
                // Anthropic is prepaid: if no credits, any API call is rejected.
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Add("x-api-key", apiKey);
                http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
                var resp = await http.GetAsync("https://api.anthropic.com/v1/models?limit=1");

                if (resp.IsSuccessStatusCode)
                    return (true, null);

                var body = await resp.Content.ReadAsStringAsync();

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "API Key de Anthropic invalida o expirada");
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    return (false, "API Key de Anthropic sin permisos o cuenta deshabilitada");
                if ((int)resp.StatusCode == 429)
                    return (false, "Sin creditos disponibles en Anthropic — revisa tu plan y facturacion");

                return (false, $"Error al validar Anthropic (HTTP {(int)resp.StatusCode}): {body}");
            }
            catch (Exception ex)
            {
                return (false, $"No se pudo conectar con Anthropic: {ex.Message}");
            }
        }

        private async Task<AiResult> GenerateTextAsync(AiExecutionContext context)
        {
            var client = new AnthropicClient(context.ApiKey);

            var messages = new List<Message>
            {
                new Message(RoleType.User, context.Input)
            };

            var parameters = new MessageParameters
            {
                Messages = messages,
                Model = context.ModelName,
                MaxTokens = 1024,
                Stream = false,
            };

            var systemParts = new List<string>();
            systemParts.Add(OutputSchemaHelper.GetTextOutputInstruction());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add(sp);
            parameters.System = new List<SystemMessage> { new SystemMessage(string.Join("\n\n", systemParts)) };

            if (context.Configuration.TryGetValue("temperature", out var temp))
                parameters.Temperature = Convert.ToDecimal(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                parameters.MaxTokens = Convert.ToInt32(maxTok);

            var response = await client.Messages.GetClaudeMessageAsync(parameters);

            var text = response.Message.ToString();

            return AiResult.Ok(text, new Dictionary<string, object>
            {
                ["model"] = context.ModelName,
                ["inputTokens"] = response.Usage.InputTokens,
                ["outputTokens"] = response.Usage.OutputTokens,
            });
        }
    }
}
