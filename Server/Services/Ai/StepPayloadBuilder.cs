using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Server.Models;
using Server.Services.Ai.Handlers;

namespace Server.Services.Ai;

/// <summary>
/// Serializes the exact payload a handler is about to send to the AI provider so
/// the execution-detail UI can display the system prompt + inputs + parameters
/// that were actually used for each step.
/// </summary>
public static class StepPayloadBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        // Drop null fields (e.g. when the user unchecks "No repetir tematicas"
        // the previousExecutionsSummary is null and shouldn't pollute the JSON).
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Keys that are transport/identity — not model parameters. Everything else in
    // Configuration is surfaced under "parameters".
    private static readonly HashSet<string> ExcludedConfigKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemPrompt", "imagePrompt", "videoPrompt", "caption", "accessToken",
        "apiKey", "messageTemplate", "messageType", "waitForResponse", "skipped",
        "autofillData", "useTemplate", "templateId", "templateInputs",
    };

    public static string Serialize(AiExecutionContext aiCtx, string providerType)
    {
        string? systemPrompt = null;
        if (aiCtx.Configuration.TryGetValue("systemPrompt", out var sp)
            && sp is string sps && !string.IsNullOrWhiteSpace(sps))
            systemPrompt = sps;

        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in aiCtx.Configuration)
        {
            if (ExcludedConfigKeys.Contains(k)) continue;
            if (k.StartsWith("fv_", StringComparison.OrdinalIgnoreCase)) continue;
            parameters[k] = Simplify(v);
        }

        var payload = new Dictionary<string, object?>
        {
            ["provider"] = providerType,
            ["model"] = aiCtx.ModelName,
            ["moduleType"] = aiCtx.ModuleType,
            ["systemPrompt"] = systemPrompt,
            ["mandatoryRules"] = string.IsNullOrWhiteSpace(aiCtx.MandatoryRules) ? null : aiCtx.MandatoryRules,
            ["projectContext"] = string.IsNullOrWhiteSpace(aiCtx.ProjectContext) ? null : aiCtx.ProjectContext,
            ["previousExecutionsSummary"] = string.IsNullOrWhiteSpace(aiCtx.PreviousExecutionsSummary) ? null : aiCtx.PreviousExecutionsSummary,
            ["prompt"] = aiCtx.Input,
            ["inputFilesCount"] = aiCtx.InputFiles?.Count ?? 0,
            ["skipOutputSchema"] = aiCtx.SkipOutputSchema,
            ["parameters"] = parameters.Count > 0 ? parameters : null,
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Shortcut: build and persist on the node's StepExecution entity.</summary>
    public static void RecordSentPayload(ModuleExecutionContext ctx, AiExecutionContext aiCtx, string providerType)
    {
        if (ctx.Node.StepExecution is null) return;
        ctx.Node.StepExecution.InputData = Serialize(aiCtx, providerType);
    }

    private static object? Simplify(object? value) => value switch
    {
        null => null,
        JsonElement je => je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => je.GetRawText(),
        },
        _ => value,
    };
}
