using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Entities;

public class Pipeline : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PipelineStatus Status { get; set; } = PipelineStatus.Draft;
    public List<PipelineStep> Steps { get; set; } = [];

    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
