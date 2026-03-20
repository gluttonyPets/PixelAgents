using System.Text.Json;
using System.Text.Json.Serialization;
using Server.Models;

namespace Server.Services.Ai
{
    // ═══════════ Fixed-output orchestrator (v2) ═══════════

    /// <summary>
    /// Plan returned by the AI for a fixed-output orchestrator.
    /// Each output has a predefined key — the AI only fills in the content.
    /// </summary>
    public class OrchestratorFixedPlan
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        [JsonPropertyName("outputs")]
        public List<OrchestratorOutputResult> Outputs { get; set; } = new();
    }

    public class OrchestratorOutputResult
    {
        [JsonPropertyName("outputKey")]
        public string OutputKey { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    // ═══════════ Legacy dynamic orchestrator (kept for deserialization of old data) ═══════════

    public class OrchestratorPlan
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        [JsonPropertyName("tasks")]
        public List<OrchestratorTask> Tasks { get; set; } = new();
    }

    public class OrchestratorTask
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("moduleId")]
        public string ModuleId { get; set; } = "";

        [JsonPropertyName("moduleName")]
        public string ModuleName { get; set; } = "";

        [JsonPropertyName("moduleType")]
        public string ModuleType { get; set; } = "";

        [JsonPropertyName("input")]
        public string Input { get; set; } = "";

        [JsonPropertyName("order")]
        public int Order { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    public static class OrchestratorSchemaHelper
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// Builds the system prompt for the fixed-output orchestrator.
        /// The AI must generate content for each predefined output.
        /// </summary>
        public static string BuildFixedOutputPrompt(
            List<OrchestratorOutput> outputs,
            string? projectContext)
        {
            var outputList = string.Join("\n", outputs.Select(o =>
                $"  - outputKey: \"{o.OutputKey}\", label: \"{o.Label}\", prompt: \"{o.Prompt}\""));

            var contextSection = !string.IsNullOrWhiteSpace(projectContext)
                ? $"\n\nPROJECT CONTEXT:\n{projectContext}"
                : "";

            return $@"You are a pipeline orchestrator. Your job is to analyze the input and generate content for each of the predefined outputs below. Each output has a specific prompt that describes what content you must produce.

OUTPUTS TO FILL:
{outputList}
{contextSection}

You MUST respond ONLY with a JSON object matching this exact schema — no markdown, no explanation, no extra text:

{{
  ""summary"": ""Brief description of what was generated"",
  ""outputs"": [
    {{
      ""outputKey"": ""<exact outputKey from the list above>"",
      ""content"": ""<the generated content for this output, following its prompt instructions>""
    }}
  ]
}}

RULES:
1. You MUST produce exactly one entry per output listed above. Do not skip any.
2. The ""outputKey"" must match exactly — do not invent new keys.
3. Each ""content"" must follow the specific prompt instructions for that output.
4. Plain ASCII only — no emojis, no markdown, no special characters in the content.
5. Keep content concise but complete. Each output's content will be sent as a prompt to another AI module.
6. Analyze the input thoroughly and distribute relevant information to each output as needed.";
        }

        /// <summary>
        /// Parses the AI response into a structured OrchestratorFixedPlan.
        /// </summary>
        public static OrchestratorFixedPlan? ParseFixedPlan(string rawText)
        {
            var json = OutputSchemaHelper.ExtractJson(rawText);
            try
            {
                return JsonSerializer.Deserialize<OrchestratorFixedPlan>(json, JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Validates that all outputKeys in the plan match the configured outputs.
        /// </summary>
        public static List<string> ValidateFixedPlan(OrchestratorFixedPlan plan, List<OrchestratorOutput> outputs)
        {
            var errors = new List<string>();
            var expectedKeys = new HashSet<string>(outputs.Select(o => o.OutputKey));

            if (plan.Outputs.Count == 0)
                errors.Add("El plan no contiene ninguna salida");

            foreach (var result in plan.Outputs)
            {
                if (!expectedKeys.Contains(result.OutputKey))
                    errors.Add($"OutputKey '{result.OutputKey}' no existe en las salidas configuradas");

                if (string.IsNullOrWhiteSpace(result.Content))
                    errors.Add($"OutputKey '{result.OutputKey}': el contenido esta vacio");
            }

            var returnedKeys = new HashSet<string>(plan.Outputs.Select(o => o.OutputKey));
            foreach (var expected in expectedKeys)
            {
                if (!returnedKeys.Contains(expected))
                    errors.Add($"Falta contenido para la salida '{expected}'");
            }

            return errors;
        }

        // Legacy methods kept for backward compatibility

        public static OrchestratorPlan? ParsePlan(string rawText)
        {
            var json = OutputSchemaHelper.ExtractJson(rawText);
            try
            {
                return JsonSerializer.Deserialize<OrchestratorPlan>(json, JsonOpts);
            }
            catch
            {
                return null;
            }
        }
    }
}
