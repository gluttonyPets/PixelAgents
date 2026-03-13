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
        private const string VeoBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

        public string ProviderType => "Google";
        public IEnumerable<string> SupportedModuleTypes => new[] { "Text", "Image", "Video" };

        public async Task<AiResult> ExecuteAsync(AiExecutionContext context)
        {
            try
            {
                return context.ModuleType switch
                {
                    "Text" => await GenerateTextAsync(context),
                    "Image" => await GenerateImageAsync(context),
                    "Video" => await GenerateVideoAsync(context),
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

            // Use the model selected by the user in the module configuration
            var imageModel = context.ModelName;

            var prompt = context.Input;
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                prompt = $"[Contexto: {context.ProjectContext}]\n\n{prompt}";

            // Prepend instruction to ensure Gemini generates an image, not text
            prompt = $"Generate an image based on this description: {prompt}";

            var maxLen = InputAdapter.GetMaxPromptLength(imageModel);
            if (prompt.Length > maxLen)
                prompt = InputAdapter.TruncateAtWord(prompt, maxLen);

            var config = new GenerateContentConfig
            {
                ResponseModalities = ["IMAGE"],
            };

            const int maxRetries = 2;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var response = await client.Models.GenerateContentAsync(
                    model: imageModel,
                    contents: prompt,
                    config: config
                );

                if (response.Candidates is null || response.Candidates.Count == 0)
                {
                    if (attempt < maxRetries) { await Task.Delay(1000 * (attempt + 1)); continue; }
                    return AiResult.Fail("Google Gemini no devolvio respuesta para la imagen");
                }

                var parts = response.Candidates[0].Content?.Parts;
                if (parts is null)
                {
                    if (attempt < maxRetries) { await Task.Delay(1000 * (attempt + 1)); continue; }
                    return AiResult.Fail("Google Gemini no devolvio partes en la respuesta");
                }

                foreach (var part in parts)
                {
                    if (part.InlineData is not null && part.InlineData.MimeType?.StartsWith("image/") == true)
                    {
                        var imageBytes = part.InlineData.Data;
                        return AiResult.OkFile(imageBytes, part.InlineData.MimeType, new Dictionary<string, object>
                        {
                            ["model"] = imageModel,
                            ["revisedPrompt"] = ""
                        });
                    }
                }

                // No image found — collect text reason and retry
                var textReason = string.Join(" ", parts
                    .Where(p => !string.IsNullOrEmpty(p.Text))
                    .Select(p => p.Text));

                if (attempt < maxRetries)
                {
                    await Task.Delay(1000 * (attempt + 1));
                    continue;
                }

                return AiResult.Fail(string.IsNullOrWhiteSpace(textReason)
                    ? "Google Gemini no devolvio imagen tras varios intentos"
                    : $"Google Gemini no genero imagen: {textReason}");
            }

            return AiResult.Fail("Google Gemini no devolvio imagen tras varios intentos");
        }

        private async Task<AiResult> GenerateVideoAsync(AiExecutionContext context)
        {
            var modelName = context.ModelName;

            // Read configuration
            var aspectRatio = "16:9";
            var duration = "8";
            var resolution = "720p";
            var personGeneration = "allow_adult";

            if (context.Configuration.TryGetValue("aspectRatio", out var ar) && ar is string arStr)
                aspectRatio = arStr;
            if (context.Configuration.TryGetValue("duration", out var dur))
                duration = dur.ToString()!;
            if (context.Configuration.TryGetValue("resolution", out var res) && res is string resStr)
                resolution = resStr;
            if (context.Configuration.TryGetValue("personGeneration", out var pg) && pg is string pgStr)
                personGeneration = pgStr;

            // Build instance object
            var instance = new Dictionary<string, object>
            {
                ["prompt"] = context.Input
            };

            // If an input image is provided (image-to-video), add it
            if (context.InputFiles is not null && context.InputFiles.Count > 0)
            {
                var imageBytes = context.InputFiles[0];
                instance["image"] = new Dictionary<string, object>
                {
                    ["inlineData"] = new Dictionary<string, object>
                    {
                        ["mimeType"] = "image/png",
                        ["data"] = Convert.ToBase64String(imageBytes)
                    }
                };
            }

            // Build parameters
            var parameters = new Dictionary<string, object>
            {
                ["aspectRatio"] = aspectRatio,
                ["personGeneration"] = personGeneration
            };

            parameters["durationSeconds"] = int.TryParse(duration, out var d) ? d : 8;

            // Resolution only supported on Veo 3+
            if (!modelName.Contains("veo-2"))
                parameters["resolution"] = resolution;

            var requestBody = new Dictionary<string, object>
            {
                ["instances"] = new[] { instance },
                ["parameters"] = parameters
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(8) };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var url = $"{VeoBaseUrl}/models/{modelName}:predictLongRunning";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("x-goog-api-key", context.ApiKey);
            request.Content = content;

            var submitResp = await http.SendAsync(request);
            if (!submitResp.IsSuccessStatusCode)
            {
                var errorBody = await submitResp.Content.ReadAsStringAsync();
                return AiResult.Fail($"Google Veo rechazo la solicitud (HTTP {(int)submitResp.StatusCode}): {errorBody}");
            }

            var submitJson = await submitResp.Content.ReadAsStringAsync();
            var submitDoc = JsonDocument.Parse(submitJson);

            if (!submitDoc.RootElement.TryGetProperty("name", out var operationNameEl))
                return AiResult.Fail("Google Veo: respuesta inesperada, no se encontro 'name' de operacion");

            var operationName = operationNameEl.GetString()!;

            // Poll for completion (up to ~6 min)
            const int maxAttempts = 120;
            const int pollIntervalMs = 3000;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                await Task.Delay(pollIntervalMs);

                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, $"{VeoBaseUrl}/{operationName}");
                pollRequest.Headers.Add("x-goog-api-key", context.ApiKey);

                var pollResp = await http.SendAsync(pollRequest);
                if (!pollResp.IsSuccessStatusCode)
                    continue;

                var pollJson = await pollResp.Content.ReadAsStringAsync();
                var pollDoc = JsonDocument.Parse(pollJson);

                if (!pollDoc.RootElement.TryGetProperty("done", out var doneEl) || !doneEl.GetBoolean())
                    continue;

                // Check for error
                if (pollDoc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    var errorMsg = errorEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : "Error desconocido";
                    return AiResult.Fail($"Google Veo error: {errorMsg}");
                }

                // Extract video URI
                if (!pollDoc.RootElement.TryGetProperty("response", out var responseEl))
                    return AiResult.Fail("Google Veo: respuesta completa pero sin campo 'response'");

                if (!responseEl.TryGetProperty("generateVideoResponse", out var genVideoResp))
                    return AiResult.Fail("Google Veo: sin campo 'generateVideoResponse'");

                if (!genVideoResp.TryGetProperty("generatedSamples", out var samplesEl)
                    || samplesEl.GetArrayLength() == 0)
                    return AiResult.Fail("Google Veo: no se generaron videos");

                var videoUri = samplesEl[0].GetProperty("video").GetProperty("uri").GetString()!;

                // Download video
                using var dlRequest = new HttpRequestMessage(HttpMethod.Get, videoUri);
                dlRequest.Headers.Add("x-goog-api-key", context.ApiKey);

                var dlResp = await http.SendAsync(dlRequest);
                if (!dlResp.IsSuccessStatusCode)
                    return AiResult.Fail($"Google Veo: error descargando video (HTTP {(int)dlResp.StatusCode})");

                var videoBytes = await dlResp.Content.ReadAsByteArrayAsync();

                return AiResult.OkFile(videoBytes, "video/mp4", new Dictionary<string, object>
                {
                    ["model"] = modelName,
                    ["duration"] = duration,
                    ["aspectRatio"] = aspectRatio,
                    ["resolution"] = resolution
                });
            }

            return AiResult.Fail($"Timeout esperando generacion de video de Google Veo (>{maxAttempts * pollIntervalMs / 1000}s)");
        }
    }
}
