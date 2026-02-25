namespace PixelAgents.Shared.DTOs;

public record WorkspaceDto(
    Guid Id,
    string Name,
    string Description,
    string Theme,
    List<AgentDto> Agents,
    List<PipelineSummaryDto> Pipelines
);

public record CreateWorkspaceRequest(
    string Name,
    string Description,
    string Theme
);
