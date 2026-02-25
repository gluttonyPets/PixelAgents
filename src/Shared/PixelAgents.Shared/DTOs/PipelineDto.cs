namespace PixelAgents.Shared.DTOs;

public record PipelineSummaryDto(
    Guid Id,
    string Name,
    string Status,
    int TotalSteps,
    int CompletedSteps
);

public record PipelineDto(
    Guid Id,
    string Name,
    string Description,
    string Status,
    List<PipelineStepDto> Steps,
    DateTime? StartedAt,
    DateTime? CompletedAt
);

public record PipelineStepDto(
    Guid Id,
    int Order,
    string Name,
    string ModuleKey,
    string Status,
    Guid AssignedAgentId,
    string AssignedAgentName
);

public record CreatePipelineRequest(
    string Name,
    string Description,
    Guid WorkspaceId,
    List<CreatePipelineStepRequest> Steps
);

public record CreatePipelineStepRequest(
    int Order,
    string Name,
    string ModuleKey,
    Guid AssignedAgentId,
    Dictionary<string, object>? InputParameters
);

public record ExecutePipelineRequest(
    Guid PipelineId,
    Dictionary<string, object>? InitialParameters
);
