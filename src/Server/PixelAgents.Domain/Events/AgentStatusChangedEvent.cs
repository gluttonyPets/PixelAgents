using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Events;

public record AgentStatusChangedEvent(
    Guid AgentId,
    string AgentName,
    AgentStatus PreviousStatus,
    AgentStatus NewStatus,
    string? ActivityDescription
);
