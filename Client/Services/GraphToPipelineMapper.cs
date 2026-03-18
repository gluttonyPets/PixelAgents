using System.Text.Json;
using Client.Models;

namespace Client.Services;

/// <summary>
/// Converts visual pipeline graph (nodes + connections) into executable pipeline steps
/// by computing topological order and generating InputMapping JSON for each step.
/// </summary>
public static class GraphToPipelineMapper
{
    public record MappedStep(
        Guid ProjectModuleId,
        int StepOrder,
        string? InputMapping,
        string? Configuration,
        string BranchId,
        int? BranchFromStep);

    /// <summary>
    /// Given the current modules and graph, compute the InputMapping and StepOrder
    /// for each module based on visual connections.
    /// </summary>
    public static List<MappedStep> MapGraphToSteps(
        List<ProjectModuleResponse> modules,
        PipelineGraph graph)
    {
        if (modules.Count == 0) return [];

        // Build adjacency: toModuleId -> list of (fromModuleId, fromPort, toPort)
        var incomingConnections = new Dictionary<Guid, List<PipelineConnection>>();
        foreach (var conn in graph.Connections)
        {
            if (!incomingConnections.ContainsKey(conn.ToModuleId))
                incomingConnections[conn.ToModuleId] = [];
            incomingConnections[conn.ToModuleId].Add(conn);
        }

        // Topological sort (Kahn's algorithm)
        var moduleIds = modules.Select(m => m.Id).ToHashSet();
        var inDegree = moduleIds.ToDictionary(id => id, _ => 0);
        foreach (var conn in graph.Connections)
        {
            if (inDegree.ContainsKey(conn.ToModuleId))
                inDegree[conn.ToModuleId]++;
        }

        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<Guid>();

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            order.Add(current);

            foreach (var conn in graph.Connections.Where(c => c.FromModuleId == current))
            {
                if (inDegree.ContainsKey(conn.ToModuleId))
                {
                    inDegree[conn.ToModuleId]--;
                    if (inDegree[conn.ToModuleId] == 0)
                        queue.Enqueue(conn.ToModuleId);
                }
            }
        }

        // Add any unconnected modules that weren't reached
        foreach (var id in moduleIds.Where(id => !order.Contains(id)))
            order.Add(id);

        // Build step order mapping
        var stepOrderMap = new Dictionary<Guid, int>();
        for (int i = 0; i < order.Count; i++)
            stepOrderMap[order[i]] = i + 1;

        // Generate MappedSteps
        var result = new List<MappedStep>();
        foreach (var moduleId in order)
        {
            var module = modules.First(m => m.Id == moduleId);
            var stepOrder = stepOrderMap[moduleId];

            string? inputMapping = null;
            string? config = module.Configuration;

            if (incomingConnections.TryGetValue(moduleId, out var incoming) && incoming.Count > 0)
            {
                // Primary connection (first incoming) determines InputMapping
                var primary = incoming[0];
                var fromStepOrder = stepOrderMap.GetValueOrDefault(primary.FromModuleId, 0);

                if (fromStepOrder > 0)
                {
                    // Determine field type from port name
                    var field = GetFieldFromPort(primary.FromPort);
                    inputMapping = JsonSerializer.Serialize(new
                    {
                        source = "step",
                        stepOrder = fromStepOrder,
                        field
                    });
                }

                // For VideoEdit modules, build scene connections into config
                if (module.ModuleType == "VideoEdit")
                {
                    config = BuildVideoEditConfig(incoming, stepOrderMap, module.Configuration, graph, moduleId);
                }
            }
            else
            {
                // No incoming connections → user input
                inputMapping = JsonSerializer.Serialize(new { source = "user" });
            }

            result.Add(new MappedStep(
                moduleId,
                stepOrder,
                inputMapping,
                config,
                module.BranchId,
                module.BranchFromStep));
        }

        return result;
    }

    private static string GetFieldFromPort(string portId)
    {
        if (portId.Contains("image")) return "file";
        if (portId.Contains("video")) return "file";
        if (portId.Contains("audio")) return "file";
        if (portId.Contains("file") || portId.Contains("design")) return "file";
        return "text";
    }

    /// <summary>
    /// Build Json2Video step configuration from visual connections.
    /// Maps scene_N_video and scene_N_script ports to source step orders.
    /// </summary>
    private static string? BuildVideoEditConfig(
        List<PipelineConnection> incoming,
        Dictionary<Guid, int> stepOrderMap,
        string? existingConfig,
        PipelineGraph graph,
        Guid moduleId)
    {
        var config = new Dictionary<string, object>();

        // Parse existing config
        if (!string.IsNullOrWhiteSpace(existingConfig))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existingConfig);
                if (existing is not null)
                {
                    foreach (var kv in existing)
                        config[kv.Key] = kv.Value;
                }
            }
            catch { }
        }

        // Get scene count from node config
        var nodeState = graph.Nodes.FirstOrDefault(n => n.ModuleId == moduleId);
        var sceneCount = 1;
        if (nodeState?.NodeConfig is not null && nodeState.NodeConfig.TryGetValue("sceneCount", out var scVal))
        {
            if (scVal is JsonElement je && je.TryGetInt32(out var sc)) sceneCount = sc;
            else if (scVal is int si) sceneCount = si;
        }

        config["sceneCount"] = sceneCount;

        // Map scene connections
        var scenes = new List<Dictionary<string, object>>();
        for (int i = 1; i <= sceneCount; i++)
        {
            var scene = new Dictionary<string, object>();

            var videoConn = incoming.FirstOrDefault(c => c.ToPort == $"input_scene_{i}_video");
            if (videoConn is not null && stepOrderMap.TryGetValue(videoConn.FromModuleId, out var videoStep))
            {
                scene["videoSourceStep"] = videoStep;
            }

            var scriptConn = incoming.FirstOrDefault(c => c.ToPort == $"input_scene_{i}_script");
            if (scriptConn is not null && stepOrderMap.TryGetValue(scriptConn.FromModuleId, out var scriptStep))
            {
                scene["scriptSourceStep"] = scriptStep;
            }

            scenes.Add(scene);
        }

        config["scenes"] = scenes;

        return JsonSerializer.Serialize(config);
    }
}
