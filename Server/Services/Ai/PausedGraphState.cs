using System.Text.Json.Serialization;

namespace Server.Services.Ai;

/// <summary>
/// Serializable state for paused graph execution (Interaction/Checkpoint).
/// Stored in ProjectExecution.PausedStepData.
/// </summary>
public class PausedGraphState
{
    [JsonPropertyName("userInput")]
    public string? UserInput { get; set; }

    /// <summary>ModuleId -> serialized StepOutput JSON for each completed node.</summary>
    [JsonPropertyName("completedOutputs")]
    public Dictionary<string, string> CompletedOutputs { get; set; } = [];

    /// <summary>ModuleId -> NodeStatus name for each node.</summary>
    [JsonPropertyName("nodeStatuses")]
    public Dictionary<string, string> NodeStatuses { get; set; } = [];

    /// <summary>The module that is currently paused (waiting for user input/review).</summary>
    [JsonPropertyName("pausedModuleId")]
    public Guid PausedModuleId { get; set; }

    /// <summary>Capture the current graph state into a serializable object.</summary>
    public static PausedGraphState Capture(ExecutionGraph graph, Guid pausedModuleId)
    {
        var state = new PausedGraphState
        {
            UserInput = graph.UserInput,
            PausedModuleId = pausedModuleId,
        };

        foreach (var node in graph.Nodes.Values)
        {
            var key = node.ModuleId.ToString();
            state.NodeStatuses[key] = node.Status.ToString();

            if (node.Status is (NodeStatus.Completed or NodeStatus.Paused) && node.Output is not null)
            {
                state.CompletedOutputs[key] = System.Text.Json.JsonSerializer.Serialize(node.Output);
            }
        }

        return state;
    }

    /// <summary>Restore completed outputs into a rebuilt graph.</summary>
    public void RestoreInto(ExecutionGraph graph)
    {
        graph.UserInput = UserInput;

        foreach (var (key, statusStr) in NodeStatuses)
        {
            if (!Guid.TryParse(key, out var moduleId)) continue;
            if (!graph.Nodes.TryGetValue(moduleId, out var node)) continue;

            if (Enum.TryParse<NodeStatus>(statusStr, out var status))
                node.Status = status;
        }

        foreach (var (key, outputJson) in CompletedOutputs)
        {
            if (!Guid.TryParse(key, out var moduleId)) continue;
            if (!graph.Nodes.TryGetValue(moduleId, out var node)) continue;

            try
            {
                node.Output = System.Text.Json.JsonSerializer.Deserialize<StepOutput>(outputJson);
                if (node.Status == NodeStatus.Completed && node.Output is not null)
                {
                    graph.CompleteNodeAndPrepareDownstream(node);
                }
            }
            catch { /* ignore malformed output */ }
        }
    }
}
