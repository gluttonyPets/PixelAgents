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
        public IEnumerable<string> SupportedModuleTypes => new[] { "Image" };

        // Mapping: friendly model ID → Leonardo API UUID
        private static readonly Dictionary<string, string> ModelIdMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["leonardo-phoenix"] = "de7d3faf-762f-48e0-b3b7-9d0ac3a3fcf3",       // Phoenix 1.0
            ["leonardo-phoenix-0.9"] = "6b645e3a-d64f-4341-a6d8-7a3690fbf042",   // Phoenix 0.9
            ["leonardo-flux-dev"] = "b2614463-296c-462a-9586-aafdb8f00e36",       // Flux Dev
            ["leonardo-flux-schnell"] = "1dd50843-d653-4516-a8e3-f0238ee453ff",   // Flux Schnell
        };

        // Models that require the contrast parameter
        private static readonly HashSet<string> ContrastRequiredModels = new(StringComparer.OrdinalIgnoreCase)
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
                    _ => AiResult.Fail($"ModuleType '{context.ModuleType}' no soportado por Leonardo AI")
                };
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

            // Build prompt
            var prompt = context.Input;
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"{context.ProjectContext}\n\n{prompt}";

            var maxLen = InputAdapter.GetMaxPromptLength(context.ModelName);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);

            // Read contrast (required for Phoenix/Flux models)
            var contrast = 3.5;
            if (context.Configuration.TryGetValue("contrast", out var c))
                contrast = Convert.ToDouble(c);

            // Step 1: Submit generation request
            object requestBody = ContrastRequiredModels.Contains(context.ModelName)
                ? new Dictionary<string, object>
                {
                    ["prompt"] = prompt,
                    ["modelId"] = modelUuid,
                    ["width"] = width,
                    ["height"] = height,
                    ["num_images"] = 1,
                    ["presetStyle"] = presetStyle,
                    ["alchemy"] = true,
                    ["contrast"] = contrast
                }
                : new Dictionary<string, object>
                {
                    ["prompt"] = prompt,
                    ["modelId"] = modelUuid,
                    ["width"] = width,
                    ["height"] = height,
                    ["num_images"] = 1,
                    ["presetStyle"] = presetStyle
                };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var submitResp = await http.PostAsync($"{BaseUrl}/generations", content);

            if (!submitResp.IsSuccessStatusCode)
            {
                var errorBody = await submitResp.Content.ReadAsStringAsync();
                return AiResult.Fail($"Leonardo AI rechazo la solicitud (HTTP {(int)submitResp.StatusCode}): {errorBody}");
            }

            var submitJson = await submitResp.Content.ReadAsStringAsync();
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
                await Task.Delay(pollIntervalMs);

                var pollResp = await http.GetAsync($"{BaseUrl}/generations/{generationId}");
                if (!pollResp.IsSuccessStatusCode)
                    continue;

                var pollJson = await pollResp.Content.ReadAsStringAsync();
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

                    var imageUrl = images[0].GetProperty("url").GetString()!;

                    // Step 3: Download image
                    var imageBytes = await http.GetByteArrayAsync(imageUrl);

                    return AiResult.OkFile(imageBytes, "image/png", new Dictionary<string, object>
                    {
                        ["model"] = context.ModelName,
                        ["revisedPrompt"] = ""
                    });
                }

                if (status == "FAILED")
                {
                    return AiResult.Fail("Leonardo AI: la generacion de imagen fallo");
                }
            }

            return AiResult.Fail($"Timeout esperando generacion de Leonardo AI (>{maxAttempts * pollIntervalMs / 1000}s)");
        }
    }
}
