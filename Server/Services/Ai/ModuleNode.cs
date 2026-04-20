using Server.Models;

namespace Server.Services.Ai;

public enum NodeStatus { Pending, Ready, Running, Completed, Failed, Paused, Skipped }

/// <summary>Runtime representation of a module in the execution graph.</summary>
public class ModuleNode
{
    public Guid ModuleId { get; }
    public ProjectModule ProjectModule { get; }
    public AiModule AiModule => ProjectModule.AiModule;
    public string ModuleType => AiModule.ModuleType;

    public NodeStatus Status { get; set; } = NodeStatus.Pending;
    public List<InputPort> InputPorts { get; } = [];
    public List<OutputPort> OutputPorts { get; } = [];

    public StepOutput? Output { get; set; }
    public StepExecution? StepExecution { get; set; }
    public decimal Cost { get; set; }

    public ModuleNode(ProjectModule pm)
    {
        ModuleId = pm.Id;
        ProjectModule = pm;
    }

    public bool CanStartWithoutInputs => string.Equals(ModuleType, "Start", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this node can be marked Ready from its current inputs.</summary>
    public bool CanBecomeReady => CanStartWithoutInputs
        || (InputPorts.Count > 0 && InputPorts.All(p => p.IsSatisfied));
}

/// <summary>An input port on a module node.</summary>
public class InputPort
{
    public string PortId { get; set; } = "";
    public string DataType { get; set; } = "any";
    public bool IsRequired { get; set; }
    public bool AllowMultiple { get; set; }

    /// <summary>Connections feeding this port (wired from ModuleConnection).</summary>
    public List<PortConnection> Connections { get; } = [];

    /// <summary>Data received from completed upstream modules.</summary>
    public List<PortData> ReceivedData { get; } = [];

    /// <summary>
    /// Satisfied when every connected upstream has delivered its data (fan-in:
    /// N upstreams → wait for N). Optional ports and ports with no connections
    /// don't block — preserves the original pipeline semantics.
    /// </summary>
    public bool IsSatisfied => ReceivedData.Count >= Connections.Count || !IsRequired;
}

/// <summary>An output port on a module node.</summary>
public class OutputPort
{
    public string PortId { get; set; } = "";
    public string DataType { get; set; } = "any";

    /// <summary>Connections from this port to downstream input ports.</summary>
    public List<PortConnection> Connections { get; } = [];

    /// <summary>Data produced after module completes.</summary>
    public PortData? Data { get; set; }
}

/// <summary>A directed edge between an output port and an input port.</summary>
public class PortConnection
{
    public Guid ConnectionId { get; set; }
    public ModuleNode SourceNode { get; set; } = null!;
    public string SourcePortId { get; set; } = "";
    public ModuleNode TargetNode { get; set; } = null!;
    public string TargetPortId { get; set; } = "";
}

/// <summary>A unit of data flowing between ports.</summary>
public class PortData
{
    public string DataType { get; set; } = "any";
    public string? TextContent { get; set; }
    public List<OutputFile>? Files { get; set; }
    public StepOutput? FullOutput { get; set; }
    public string? SourcePortId { get; set; }
}
