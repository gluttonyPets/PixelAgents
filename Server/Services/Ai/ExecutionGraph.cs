using System.Text.Json;
using Server.Models;

namespace Server.Services.Ai;

/// <summary>
/// Builds and manages the runtime execution graph from ProjectModules and ModuleConnections.
/// Replaces all branch logic, InputMapping, sceneInputs, templateInputs, etc.
/// </summary>
public class ExecutionGraph
{
    public Dictionary<Guid, ModuleNode> Nodes { get; } = [];
    public Guid ProjectId { get; set; }
    public Guid ExecutionId { get; set; }
    public string WorkspacePath { get; set; } = "";
    public string? UserInput { get; set; }

    /// <summary>
    /// Build the execution graph from DB entities.
    /// </summary>
    public static ExecutionGraph Build(
        List<ProjectModule> modules,
        List<ModuleConnection> connections)
    {
        var graph = new ExecutionGraph();

        // 1. Create nodes
        foreach (var pm in modules)
        {
            if (!pm.IsActive) continue;
            var node = new ModuleNode(pm);
            graph.Nodes[pm.Id] = node;
        }

        // 2. Discover ports from connections + known required ports
        // First, register ports that appear in connections
        foreach (var conn in connections)
        {
            if (!graph.Nodes.TryGetValue(conn.FromModuleId, out var fromNode)) continue;
            if (!graph.Nodes.TryGetValue(conn.ToModuleId, out var toNode)) continue;

            // Ensure output port exists on source
            var outputPort = fromNode.OutputPorts.FirstOrDefault(p => p.PortId == conn.FromPort);
            if (outputPort is null)
            {
                outputPort = new OutputPort
                {
                    PortId = conn.FromPort,
                    DataType = InferDataType(conn.FromPort),
                };
                fromNode.OutputPorts.Add(outputPort);
            }

            // Ensure input port exists on target
            var inputPort = toNode.InputPorts.FirstOrDefault(p => p.PortId == conn.ToPort);
            if (inputPort is null)
            {
                inputPort = new InputPort
                {
                    PortId = conn.ToPort,
                    DataType = InferDataType(conn.ToPort),
                    IsRequired = true,
                    AllowMultiple = conn.ToPort.StartsWith("input_scene_") || conn.ToPort.StartsWith("input_tpl_"),
                };
                toNode.InputPorts.Add(inputPort);
            }

            // Wire the connection
            var portConn = new PortConnection
            {
                ConnectionId = conn.Id,
                SourceNode = fromNode,
                SourcePortId = conn.FromPort,
                TargetNode = toNode,
                TargetPortId = conn.ToPort,
            };
            outputPort.Connections.Add(portConn);
            inputPort.Connections.Add(portConn);
        }

        // 3. Add standard output ports for modules with no outgoing connections
        // (e.g., terminal modules still need their output port for StepOutput)
        foreach (var node in graph.Nodes.Values)
        {
            EnsureStandardOutputPorts(node);
        }

        return graph;
    }

    /// <summary>Returns all nodes that are Pending and have all inputs satisfied.</summary>
    public List<ModuleNode> GetReadyNodes()
    {
        return Nodes.Values
            .Where(n => n.Status == NodeStatus.Pending && n.AllInputsSatisfied)
            .ToList();
    }

    /// <summary>
    /// After a node completes, resolve its output ports and deliver data to downstream input ports.
    /// </summary>
    public void PropagateOutputs(ModuleNode completedNode)
    {
        PortDataResolver.ResolveOutputPorts(completedNode);

        foreach (var outputPort in completedNode.OutputPorts)
        {
            if (outputPort.Data is null) continue;

            foreach (var conn in outputPort.Connections)
            {
                var targetPort = conn.TargetNode.InputPorts
                    .FirstOrDefault(p => p.PortId == conn.TargetPortId);
                targetPort?.ReceivedData.Add(outputPort.Data);
            }
        }
    }

    /// <summary>Mark a failed node and cascade failure to all unreachable downstream nodes.</summary>
    public void CascadeFailure(ModuleNode failedNode)
    {
        var visited = new HashSet<Guid>();
        var queue = new Queue<ModuleNode>();

        // Start from all direct downstream nodes
        foreach (var outputPort in failedNode.OutputPorts)
        foreach (var conn in outputPort.Connections)
        {
            if (visited.Add(conn.TargetNode.ModuleId))
                queue.Enqueue(conn.TargetNode);
        }

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.Status is NodeStatus.Completed or NodeStatus.Running)
                continue;

            node.Status = NodeStatus.Failed;

            foreach (var outputPort in node.OutputPorts)
            foreach (var conn in outputPort.Connections)
            {
                if (visited.Add(conn.TargetNode.ModuleId))
                    queue.Enqueue(conn.TargetNode);
            }
        }
    }

    /// <summary>True when all nodes are in a terminal state.</summary>
    public bool IsComplete => Nodes.Values.All(
        n => n.Status is NodeStatus.Completed or NodeStatus.Failed
             or NodeStatus.Skipped or NodeStatus.Paused);

    /// <summary>True when execution is blocked (no ready nodes, nothing running, not complete).</summary>
    public bool IsBlocked => !IsComplete
        && GetReadyNodes().Count == 0
        && Nodes.Values.All(n => n.Status is not NodeStatus.Running);

    /// <summary>Find all nodes downstream of a given node (for retry).</summary>
    public HashSet<Guid> GetDownstreamNodes(Guid moduleId)
    {
        var downstream = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(moduleId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!Nodes.TryGetValue(current, out var node)) continue;

            foreach (var outputPort in node.OutputPorts)
            foreach (var conn in outputPort.Connections)
            {
                if (downstream.Add(conn.TargetNode.ModuleId))
                    queue.Enqueue(conn.TargetNode.ModuleId);
            }
        }

        return downstream;
    }

    // ── Helpers ──

    private static string InferDataType(string portId) => portId switch
    {
        _ when portId.Contains("image") => "image",
        _ when portId.Contains("video") => "video",
        _ when portId.Contains("audio") => "audio",
        _ when portId.Contains("scene") => "scene",
        _ when portId.Contains("file") || portId.Contains("design") => "file",
        _ when portId.Contains("text") || portId.Contains("prompt") || portId.Contains("result")
            || portId.Contains("response") => "text",
        _ => "any",
    };

    private static void EnsureStandardOutputPorts(ModuleNode node)
    {
        // If the node already has output ports from connections, don't add more
        if (node.OutputPorts.Count > 0) return;

        // Add default output port based on module type so StepOutput can be stored
        var (portId, dataType) = node.ModuleType switch
        {
            "Text" or "Coordinator" => ("output_text", "text"),
            "Image" => ("output_image", "image"),
            "Video" => ("output_video", "video"),
            "VideoEdit" => ("output_video", "video"),
            "VideoSearch" => ("output_videos", "video"),
            "Audio" => ("output_audio", "audio"),
            "Transcription" => ("output_text", "text"),
            "Orchestrator" => ("output_plan", "text"),
            "Scene" => ("output_scene", "scene"),
            "StaticText" => ("output_text", "text"),
            "FileUpload" => ("output_file", "any"),
            "Start" => ("output_prompt", "text"),
            "Interaction" => ("output_response", "text"),
            "Design" => ("output_file", "file"),
            "Publish" => ("output_result", "text"),
            "Embeddings" => ("output_embedding", "file"),
            "Checkpoint" => ("output_1", "any"),
            _ => ("output_data", "any"),
        };

        node.OutputPorts.Add(new OutputPort { PortId = portId, DataType = dataType });
    }
}
