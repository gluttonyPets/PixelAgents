using System.Text.Json;
using System.Text.Json.Serialization;

namespace Server.Services.Ai
{
    /// <summary>
    /// Rigid JSON plan that the orchestrator AI must fill in.
    /// The structure is predefined — the AI only populates the values.
    /// </summary>
    public class OrchestratorPlan
    {
        /// <summary>Short description of what the orchestrator decided to do.</summary>
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        /// <summary>Ordered list of tasks the orchestrator assigns to modules.</summary>
        [JsonPropertyName("tasks")]
        public List<OrchestratorTask> Tasks { get; set; } = new();
    }

    public class OrchestratorTask
    {
        /// <summary>Sequential task identifier (task_1, task_2, ...).</summary>
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = "";

        /// <summary>What this task must accomplish.</summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        /// <summary>ID of the project module to use for this task.</summary>
        [JsonPropertyName("moduleId")]
        public string ModuleId { get; set; } = "";

        /// <summary>Human-readable name of the assigned module.</summary>
        [JsonPropertyName("moduleName")]
        public string ModuleName { get; set; } = "";

        /// <summary>The module type (Text, Image, Video, Audio, Stock).</summary>
        [JsonPropertyName("moduleType")]
        public string ModuleType { get; set; } = "";

        /// <summary>The prompt/input to send to the module.</summary>
        [JsonPropertyName("input")]
        public string Input { get; set; } = "";

        /// <summary>Execution order (1-based). Tasks with same order run in sequence.</summary>
        [JsonPropertyName("order")]
        public int Order { get; set; }

        /// <summary>Why this module was chosen for this task.</summary>
        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// Feedback from user review stored in ProjectModule.Configuration
    /// to guide future orchestrator executions.
    /// </summary>
    public class OrchestratorFeedback
    {
        [JsonPropertyName("comments")]
        public List<OrchestratorComment> Comments { get; set; } = new();

        [JsonPropertyName("approved")]
        public bool Approved { get; set; }
    }

    public class OrchestratorComment
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }
    }

    public static class OrchestratorSchemaHelper
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// Builds the system prompt that instructs the AI to fill in the rigid orchestrator plan.
        /// Includes the list of available modules so the AI knows what tools it has.
        /// </summary>
        public static string BuildOrchestratorPrompt(
            List<AvailableModule> availableModules,
            OrchestratorFeedback? previousFeedback,
            string? projectContext)
        {
            var moduleList = string.Join("\n", availableModules.Select(m =>
                $"  - ID: {m.ModuleId}, Name: \"{m.Name}\", Type: {m.ModuleType}, Provider: {m.Provider}, Model: {m.Model}, Description: \"{m.Description}\""));

            var feedbackSection = "";
            if (previousFeedback?.Comments.Count > 0)
            {
                var comments = string.Join("\n", previousFeedback.Comments.Select(c =>
                    $"  - [{c.CreatedAt:yyyy-MM-dd}]: {c.Text}"));
                feedbackSection = $@"

IMPORTANT - USER FEEDBACK FROM PREVIOUS EXECUTIONS (you MUST follow these instructions):
{comments}";
            }

            var contextSection = !string.IsNullOrWhiteSpace(projectContext)
                ? $"\n\nPROJECT CONTEXT:\n{projectContext}"
                : "";

            return $@"You are a pipeline orchestrator. Your job is to analyze the input (typically a script, brief, or content plan from a previous step) and create a task plan that assigns work to the available modules.

AVAILABLE MODULES IN THIS PROJECT:
{moduleList}
{contextSection}{feedbackSection}

You MUST respond ONLY with a JSON object matching this exact schema — no markdown, no explanation, no extra text:

{{
  ""summary"": ""Brief description of the plan"",
  ""tasks"": [
    {{
      ""taskId"": ""task_1"",
      ""description"": ""What this task must accomplish"",
      ""moduleId"": ""<exact module ID from the list above>"",
      ""moduleName"": ""<exact module name>"",
      ""moduleType"": ""<Text|Image|Video|Audio|Stock>"",
      ""input"": ""<the prompt or search query to send to the module>"",
      ""order"": 1,
      ""reason"": ""Why this module was chosen""
    }}
  ]
}}

RULES:
1. ONLY use modules from the AVAILABLE MODULES list above. Never invent module IDs.
2. Each task MUST have a valid moduleId that exists in the list.
3. The ""input"" field must contain the complete prompt/query ready to be sent to the module.
4. Order tasks logically — dependencies first.
5. Be efficient — don't create unnecessary tasks.
6. For video content that can be found in stock footage, prefer Stock modules over Video generation modules (cheaper and faster).
7. Plain ASCII only — no emojis, no markdown, no special characters in any field.
8. Keep inputs concise but complete.";
        }

        /// <summary>
        /// Parses the AI response into a structured OrchestratorPlan.
        /// </summary>
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

        /// <summary>
        /// Validates that all module IDs in the plan exist in the available modules.
        /// </summary>
        public static List<string> ValidatePlan(OrchestratorPlan plan, List<AvailableModule> availableModules)
        {
            var errors = new List<string>();
            var moduleIds = new HashSet<string>(availableModules.Select(m => m.ModuleId));

            if (plan.Tasks.Count == 0)
                errors.Add("El plan no contiene ninguna tarea");

            foreach (var task in plan.Tasks)
            {
                if (!moduleIds.Contains(task.ModuleId))
                    errors.Add($"Tarea '{task.TaskId}': modulo '{task.ModuleId}' no existe en el proyecto");

                if (string.IsNullOrWhiteSpace(task.Input))
                    errors.Add($"Tarea '{task.TaskId}': el input esta vacio");
            }

            return errors;
        }
    }

    public class AvailableModule
    {
        public string ModuleId { get; set; } = "";
        public string Name { get; set; } = "";
        public string ModuleType { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
