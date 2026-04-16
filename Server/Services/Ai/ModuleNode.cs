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

    /// <summary>True when all required input ports are satisfied.</summary>
    public bool AllInputsSatisfied => InputPorts.All(p => p.IsSatisfied);
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

    /// <summary>A port is satisfied if it has data, is not required, or has no connections.</summary>
    public bool IsSatisfied => ReceivedData.Count > 0 || !IsRequired || Connections.Count == 0;
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
