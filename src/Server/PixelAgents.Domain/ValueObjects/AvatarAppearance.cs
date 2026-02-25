namespace PixelAgents.Domain.ValueObjects;

public record AvatarAppearance(
    string SpriteSheet,
    string IdleAnimation,
    string WorkingAnimation,
    string ThinkingAnimation,
    int OfficePositionX,
    int OfficePositionY,
    string DeskStyle
);
