using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Entities;

public class AgentTask : BaseEntity
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Pending;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public double Progress { get; set; }
    public string? ResultData { get; set; }
    public string? ErrorMessage { get; set; }

    public Guid AgentId { get; set; }
    public Agent Agent { get; set; } = null!;

    public Guid? PipelineStepId { get; set; }
    public PipelineStep? PipelineStep { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
