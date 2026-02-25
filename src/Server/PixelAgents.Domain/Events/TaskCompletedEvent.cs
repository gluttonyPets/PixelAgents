namespace PixelAgents.Domain.Events;

public record TaskCompletedEvent(
    Guid TaskId,
    Guid AgentId,
    string AgentName,
    string TaskTitle,
    string? ResultSummary
);
