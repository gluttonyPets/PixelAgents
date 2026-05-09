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
            string modelName,
            int count,
            string instructions,
            CancellationToken ct = default);

        IReadOnlyList<PromptPlannerModelOption> GetAvailableModels();
    }

    public record PromptPlannerResult(bool Success, List<string> Prompts, string? Error);

    public record PromptPlannerModelOption(string Provider, string ModelName, string DisplayName);

    public class PromptPlannerService : IPromptPlannerService
    {
        private const string PlannerProvider = "OpenAI";

        private static readonly IReadOnlyList<PromptPlannerModelOption> _availableModels = new List<PromptPlannerModelOption>
        {
            new(PlannerProvider, "gpt-4o", "GPT-4o"),
            new(PlannerProvider, "gpt-4o-mini", "GPT-4o mini"),
            new(PlannerProvider, "gpt-4-turbo", "GPT-4 Turbo"),
            new(PlannerProvider, "gpt-4", "GPT-4"),
            new(PlannerProvider, "gpt-3.5-turbo", "GPT-3.5 Turbo"),
        };

        private readonly IAiProviderRegistry _registry;
        private readonly ILogger<PromptPlannerService> _log;

        public PromptPlannerService(IAiProviderRegistry registry, ILogger<PromptPlannerService> log)
        {
            _registry = registry;
            _log = log;
        }

        public IReadOnlyList<PromptPlannerModelOption> GetAvailableModels() => _availableModels;

        public async Task<PromptPlannerResult> GenerateAsync(
            UserDbContext db,
            Guid projectId,
            string modelName,
            int count,
            string instructions,
            CancellationToken ct = default)
        {
            if (count < 1 || count > 50)
                return new PromptPlannerResult(false, new(), "La cantidad debe estar entre 1 y 50");

            if (string.IsNullOrWhiteSpace(instructions))
                return new PromptPlannerResult(false, new(), "Faltan instrucciones para el planificador");

            if (string.IsNullOrWhiteSpace(modelName))
                return new PromptPlannerResult(false, new(), "Falta indicar el modelo");

            var modelOption = _availableModels.FirstOrDefault(m =>
                string.Equals(m.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            if (modelOption is null)
                return new PromptPlannerResult(false, new(), $"Modelo '{modelName}' no soportado por el planificador");

            var project = await db.Projects.FindAsync(new object?[] { projectId }, ct);
            if (project is null)
                return new PromptPlannerResult(false, new(), "Proyecto no encontrado");

            var apiKeyEntity = await db.ApiKeys
                .Where(k => k.ProviderType == modelOption.Provider)
                .OrderBy(k => k.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (apiKeyEntity is null || string.IsNullOrEmpty(apiKeyEntity.EncryptedKey))
                return new PromptPlannerResult(false, new(),
                    $"No hay API Key de {modelOption.Provider} configurada. Anade una en Configuracion > API Keys.");

            var provider = _registry.GetProvider(modelOption.Provider);
            if (provider is null)
                return new PromptPlannerResult(false, new(), $"Proveedor '{modelOption.Provider}' no disponible");

            var systemPrompt = BuildPlannerPrompt(count, instructions, project.Context);

            var aiContext = new AiExecutionContext
            {
                ModuleType = "Text",
                ModelName = modelOption.ModelName,
                ApiKey = apiKeyEntity.EncryptedKey,
                Input = systemPrompt,
                ProjectContext = project.Context,
                Configuration = new(),
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
