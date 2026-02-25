using PixelAgents.Shared.Enums;

namespace PixelAgents.Shared.DTOs;

public record AgentDto(
    Guid Id,
    string Name,
    string Role,
    string Description,
    string ModuleKey,
    AgentStatusDto Status,
    AvatarAppearanceDto Appearance,
    List<AgentSkillDto> Skills,
    string Personality,
    string? CurrentActivity
);

public record AgentSkillDto(
    string Type,
    string Name,
    int Level,
    string Description
);

public record AvatarAppearanceDto(
    string SpriteSheet,
    string IdleAnimation,
    string WorkingAnimation,
    string ThinkingAnimation,
    int OfficePositionX,
    int OfficePositionY,
    string DeskStyle
);
