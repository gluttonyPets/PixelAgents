using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.ValueObjects;

public record AgentSkill(
    SkillType Type,
    string Name,
    int Level,
    string Description
);
