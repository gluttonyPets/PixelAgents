using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Entities;

public class PipelineStep : BaseEntity
{
    public int Order { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ModuleKey { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public Dictionary<string, object> InputParameters { get; set; } = [];
    public Dictionary<string, object> OutputData { get; set; } = [];

    public Guid PipelineId { get; set; }
    public Pipeline Pipeline { get; set; } = null!;

    public Guid AssignedAgentId { get; set; }
    public Agent AssignedAgent { get; set; } = null!;
}
