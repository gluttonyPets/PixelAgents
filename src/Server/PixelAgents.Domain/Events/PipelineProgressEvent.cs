using PixelAgents.Domain.Enums;

namespace PixelAgents.Domain.Events;

public record PipelineProgressEvent(
    Guid PipelineId,
    Guid StepId,
    string StepName,
    AgentTaskStatus Status,
    double Progress,
    string? Message
);
