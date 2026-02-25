using PixelAgents.Domain.ValueObjects;

namespace PixelAgents.AgentSystem.Abstractions;

public interface IAgentModule
{
    string ModuleKey { get; }
    string DisplayName { get; }
    string Description { get; }
    string Role { get; }
    AvatarAppearance DefaultAppearance { get; }
    List<AgentSkill> Skills { get; }
    string Personality { get; }

    Task<AgentModuleResult> ExecuteAsync(AgentModuleContext context, CancellationToken ct = default);
    bool CanHandle(string taskType);
}
