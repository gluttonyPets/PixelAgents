using PixelAgents.Domain.Enums;
using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.Domain.Entities;

public class Agent : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ModuleKey { get; set; } = string.Empty;
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public AvatarAppearance Appearance { get; set; } = null!;
    public List<AgentSkill> Skills { get; set; } = [];
    public string Personality { get; set; } = string.Empty;

    public Guid WorkspaceId { get; set; }
    public Workspace Workspace { get; set; } = null!;

    public List<AgentTask> AssignedTasks { get; set; } = [];
}
