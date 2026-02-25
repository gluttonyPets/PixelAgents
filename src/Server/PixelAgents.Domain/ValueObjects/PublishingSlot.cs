using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.ValueObjects;

public record PublishingSlot(
    SocialPlatform Platform,
    DayOfWeek Day,
    TimeOnly BestTime,
    double EngagementScore
);
