using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPromptPlannerService
    {
        Task<PromptPlannerResult> GenerateAsync(
            UserDbContext db,
            Guid projectId,
            Guid generatorAiModuleId,
            int count,
            string instructions,
            CancellationToken ct = default);
    }

    public record PromptPlannerResult(bool Success, List<string> Prompts, string? Error);

    public class PromptPlannerService : IPromptPlannerService
    {
        private readonly IAiProviderRegistry _registry;
        private readonly ILogger<PromptPlannerService> _log;

        public PromptPlannerService(IAiProviderRegistry registry, ILogger<PromptPlannerService> log)
        {
            _registry = registry;
            _log = log;
        }

        public async Task<PromptPlannerResult> GenerateAsync(
            UserDbContext db,
            Guid projectId,
            Guid generatorAiModuleId,
            int count,
            string instructions,
            CancellationToken ct = default)
        {
            if (count < 1 || count > 50)
                return new PromptPlannerResult(false, new(), "La cantidad debe estar entre 1 y 50");

            if (string.IsNullOrWhiteSpace(instructions))
                return new PromptPlannerResult(false, new(), "Faltan instrucciones para el planificador");

            var project = await db.Projects.FindAsync(new object?[] { projectId }, ct);
            if (project is null)
                return new PromptPlannerResult(false, new(), "Proyecto no encontrado");

            var aiModule = await db.AiModules
                .Include(m => m.ApiKey)
                .FirstOrDefaultAsync(m => m.Id == generatorAiModuleId, ct);

            if (aiModule is null)
                return new PromptPlannerResult(false, new(), "Modelo generador no encontrado");

            if (!string.Equals(aiModule.ModuleType, "Text", StringComparison.OrdinalIgnoreCase))
                return new PromptPlannerResult(false, new(), "El modelo generador debe ser de tipo Text");

            var apiKey = aiModule.ApiKey?.EncryptedKey;
            if (string.IsNullOrEmpty(apiKey))
                return new PromptPlannerResult(false, new(), "El modelo generador no tiene API Key configurada");

            var provider = _registry.GetProvider(aiModule.ProviderType);
            if (provider is null)
                return new PromptPlannerResult(false, new(), $"Proveedor '{aiModule.ProviderType}' no disponible");

            var systemPrompt = BuildPlannerPrompt(count, instructions, project.Context);

            var aiContext = new AiExecutionContext
            {
                ModuleType = aiModule.ModuleType,
                ModelName = aiModule.ModelName,
                ApiKey = apiKey,
                Input = systemPrompt,
                ProjectContext = project.Context,
                Configuration = ParseConfig(aiModule.Configuration),
            };

            try
            {
                var result = await provider.ExecuteAsync(aiContext);
                if (!result.Success)
                    return new PromptPlannerResult(false, new(), result.Error ?? "Error generando prompts");

                var raw = result.TextOutput ?? "";
                var prompts = ParsePromptList(raw, count);
                if (prompts.Count == 0)
                    return new PromptPlannerResult(false, new(), "El modelo no devolvio ningun prompt valido");

                return new PromptPlannerResult(true, prompts, null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "PromptPlannerService failed for project {ProjectId}", projectId);
                return new PromptPlannerResult(false, new(), ex.Message);
            }
        }

        private static string BuildPlannerPrompt(int count, string instructions, string? projectContext)
        {
            var ctxBlock = string.IsNullOrWhiteSpace(projectContext)
                ? ""
                : $"\n\nContexto del proyecto:\n{projectContext}\n";

            return
$@"Eres un planificador que genera una lista de {count} prompts independientes para un pipeline de IA.
Cada prompt debe ser autocontenido, claro y listo para ejecutarse en una corrida del pipeline.{ctxBlock}

Necesidades / instrucciones del usuario:
{instructions}

Devuelve EXCLUSIVAMENTE un JSON valido con este formato exacto, sin texto adicional, sin comentarios, sin markdown:
{{
  ""prompts"": [
    ""primer prompt"",
    ""segundo prompt""
  ]
}}
La lista debe contener exactamente {count} prompts ordenados.";
        }

        private static Dictionary<string, object> ParseConfig(string? config)
        {
            if (string.IsNullOrWhiteSpace(config)) return new();
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(config);
                return dict ?? new();
            }
            catch
            {
                return new();
            }
        }

        private static List<string> ParsePromptList(string raw, int expected)
        {
            var json = ExtractJsonObject(raw);
            if (json is null) return new();

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("prompts", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return new();

                var list = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var v = item.GetString();
                        if (!string.IsNullOrWhiteSpace(v)) list.Add(v.Trim());
                    }
                }

                if (list.Count > expected) list = list.Take(expected).ToList();
                return list;
            }
            catch
            {
                return new();
            }
        }

        private static string? ExtractJsonObject(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            // Strip markdown fences if present
            var cleaned = raw.Trim();
            if (cleaned.StartsWith("```"))
            {
                var firstNewline = cleaned.IndexOf('\n');
                if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
                var fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
                if (fenceEnd >= 0) cleaned = cleaned[..fenceEnd];
                cleaned = cleaned.Trim();
            }

            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            return cleaned.Substring(start, end - start + 1);
        }
    }
}
