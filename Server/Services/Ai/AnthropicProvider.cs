using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Server.Models;
using System.Text;
using System.Text.Json;

namespace Server.Services.Ai
{
    public class AnthropicProvider : IAiProvider
    {
        private static readonly TimeSpan TextTimeout = TimeSpan.FromMinutes(3);

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
            catch (OperationCanceledException)
            {
                return AiResult.Fail("Operación cancelada por el usuario");
            }
            catch (Exception ex)
            {
                return AiResult.Fail($"Error Anthropic: {ex.Message}");
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
            var systemPrompt = BuildSystemPrompt(context);

            // When the user wires file modules into a Text node we go via raw
            // HTTP so we can mix image / document / text content blocks freely.
            // The SDK path is kept for the text-only fast case.
            if (context.InputFiles is { Count: > 0 })
                return await GenerateTextWithAttachmentsAsync(context, systemPrompt);

            var client = new AnthropicClient(context.ApiKey);
            var messages = new List<Message>
            {
                new Message(RoleType.User, context.Input),
            };

            var parameters = new MessageParameters
            {
                Messages = messages,
                Model = context.ModelName,
                MaxTokens = 1024,
                Stream = false,
                System = new List<SystemMessage> { new SystemMessage(systemPrompt) },
            };

            if (context.Configuration.TryGetValue("temperature", out var temp))
                parameters.Temperature = Convert.ToDecimal(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                parameters.MaxTokens = Convert.ToInt32(maxTok);

            var response = await ExecuteWithTimeoutAsync(
                client.Messages.GetClaudeMessageAsync(parameters),
                TextTimeout,
                context.CancellationToken
            );

            var text = response.Message.ToString();

            var inputTokens = response.Usage.InputTokens;
            var outputTokens = response.Usage.OutputTokens;

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

        private static string BuildSystemPrompt(AiExecutionContext context)
        {
            var systemParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(context.MandatoryRules))
                systemParts.Add(context.MandatoryRules!);
            if (context.Configuration.TryGetValue("systemPrompt", out var sysPrompt) && sysPrompt is string sp)
                systemParts.Add($"[INSTRUCCION PRINCIPAL - Esta es tu directiva prioritaria, sigue estas instrucciones por encima de cualquier otra regla]\n{sp}");
            systemParts.Add(OutputSchemaHelper.GetTextContentRules());
            if (!string.IsNullOrWhiteSpace(context.ProjectContext))
                systemParts.Add($"[Contexto del proyecto]\n{context.ProjectContext}");
            if (!string.IsNullOrWhiteSpace(context.PreviousExecutionsSummary))
                systemParts.Add(context.PreviousExecutionsSummary!);
            return string.Join("\n\n", systemParts);
        }

        /// <summary>
        /// Raw /v1/messages call. Builds the user content blocks manually so we
        /// can attach PDFs (document blocks), images (image blocks with the real
        /// MIME) and plain-text files (inline text) in the same request — the
        /// scenarios the SDK helper does not cover uniformly across versions.
        /// </summary>
        private async Task<AiResult> GenerateTextWithAttachmentsAsync(AiExecutionContext context, string systemPrompt)
        {
            var contentBlocks = new List<object>();
            for (int i = 0; i < context.InputFiles!.Count; i++)
            {
                var fileBytes = context.InputFiles[i];
                var meta = context.InputFileMetas is { } metas && i < metas.Count ? metas[i] : null;
                var (kind, mediaType) = ResolveAttachmentKind(meta, fileBytes);

                switch (kind)
                {
                    case AttachmentKind.Pdf:
                        contentBlocks.Add(new
                        {
                            type = "document",
                            source = new
                            {
                                type = "base64",
                                media_type = "application/pdf",
                                data = Convert.ToBase64String(fileBytes),
                            },
                        });
                        break;

                    case AttachmentKind.Image:
                        contentBlocks.Add(new
                        {
                            type = "image",
                            source = new
                            {
                                type = "base64",
                                media_type = mediaType,
                                data = Convert.ToBase64String(fileBytes),
                            },
                        });
                        break;

                    case AttachmentKind.Text:
                        var inlineText = Encoding.UTF8.GetString(fileBytes);
                        var fileName = meta?.FileName ?? $"file_{i + 1}.txt";
                        contentBlocks.Add(new
                        {
                            type = "text",
                            text = $"<file name=\"{fileName}\">\n{inlineText}\n</file>",
                        });
                        break;

                    default:
                        var name = meta?.FileName ?? $"file_{i + 1}";
                        var ct = meta?.ContentType ?? "application/octet-stream";
                        contentBlocks.Add(new
                        {
                            type = "text",
                            text = $"[Adjunto \"{name}\" ({ct}, {fileBytes.Length} B) no soportado por Claude — omitido del payload binario]",
                        });
                        break;
                }
            }
            contentBlocks.Add(new { type = "text", text = context.Input });

            var payload = new Dictionary<string, object?>
            {
                ["model"] = context.ModelName,
                ["max_tokens"] = 1024,
                ["system"] = systemPrompt,
                ["messages"] = new[]
                {
                    new { role = "user", content = contentBlocks }
                }
            };

            if (context.Configuration.TryGetValue("temperature", out var temp))
                payload["temperature"] = Convert.ToDecimal(temp);
            if (context.Configuration.TryGetValue("maxTokens", out var maxTok))
                payload["max_tokens"] = Convert.ToInt32(maxTok);

            using var http = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5),
            };
            http.DefaultRequestHeaders.Add("x-api-key", context.ApiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("https://api.anthropic.com/v1/messages", content, context.CancellationToken);
            var body = await resp.Content.ReadAsStringAsync(context.CancellationToken);

            if (!resp.IsSuccessStatusCode)
                return AiResult.Fail($"Anthropic HTTP {(int)resp.StatusCode}: {Truncate(body, 800)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var textBuilder = new StringBuilder();
            if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "text"
                        && block.TryGetProperty("text", out var textEl))
                    {
                        textBuilder.Append(textEl.GetString());
                    }
                }
            }

            int inputTokens = 0, outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var inTok)) inputTokens = inTok.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var outTok)) outputTokens = outTok.GetInt32();
            }

            return new AiResult
            {
                Success = true,
                TextOutput = textBuilder.ToString(),
                EstimatedCost = PricingCatalog.EstimateTextCost(context.ModelName, inputTokens, outputTokens),
                Metadata = new Dictionary<string, object>
                {
                    ["model"] = context.ModelName,
                    ["inputTokens"] = inputTokens,
                    ["outputTokens"] = outputTokens,
                    ["attachments"] = context.InputFiles.Count,
                }
            };
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        private enum AttachmentKind { Image, Pdf, Text, Unsupported }

        /// <summary>
        /// Decide which Claude content block fits the attachment, using metadata
        /// first and falling back to magic-byte sniffing when ContentType is
        /// missing or generic (some upstream modules only set
        /// "application/octet-stream").
        /// </summary>
        private static (AttachmentKind Kind, string MediaType) ResolveAttachmentKind(InputFileMeta? meta, byte[] bytes)
        {
            var declared = meta?.ContentType?.Trim().ToLowerInvariant() ?? "";
            var fileName = meta?.FileName ?? "";

            if (declared == "application/pdf" || fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || LooksLikePdf(bytes))
                return (AttachmentKind.Pdf, "application/pdf");

            if (declared.StartsWith("image/"))
            {
                var normalized = declared switch
                {
                    "image/jpg" => "image/jpeg",
                    _ => declared,
                };
                return (AttachmentKind.Image, normalized);
            }

            var sniffed = SniffImageMediaType(bytes);
            if (sniffed is not null)
                return (AttachmentKind.Image, sniffed);

            if (declared.StartsWith("text/")
                || declared == "application/json"
                || declared == "application/xml"
                || declared == "application/x-yaml"
                || declared == "application/yaml")
            {
                return (AttachmentKind.Text, declared);
            }

            // No metadata and no recognizable magic bytes — treat as text only
            // if the buffer is valid UTF-8 and reasonably small; otherwise mark
            // unsupported so we don't blow up the request with random binary.
            if (bytes.Length <= 256 * 1024 && LooksLikeText(bytes))
                return (AttachmentKind.Text, "text/plain");

            return (AttachmentKind.Unsupported, declared);
        }

        private static bool LooksLikePdf(byte[] bytes)
            => bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46; // "%PDF"

        private static string? SniffImageMediaType(byte[] bytes)
        {
            if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "image/png";
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";
            if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "image/gif";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";
            return null;
        }

        private static bool LooksLikeText(byte[] bytes)
        {
            // Heuristic: no NUL bytes and most chars are printable ASCII or common
            // UTF-8 continuation bytes. Cheap, good enough to dodge random binary.
            int suspicious = 0;
            foreach (var b in bytes)
            {
                if (b == 0) return false;
                if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
                    suspicious++;
            }
            return suspicious < bytes.Length / 32;
        }
    }
}
