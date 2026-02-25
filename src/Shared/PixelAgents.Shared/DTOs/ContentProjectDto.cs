namespace PixelAgents.Shared.DTOs;

public record ContentProjectDto(
    Guid Id,
    string Title,
    string Topic,
    string? TemplateUrl,
    string? FinalContentUrl,
    List<string> TargetPlatforms,
    DateTime? ScheduledPublishDate,
    Guid PipelineId,
    string PipelineStatus
);

public record CreateContentProjectRequest(
    string Topic,
    List<string> TargetPlatforms,
    Guid WorkspaceId,
    Dictionary<string, object>? AdditionalParameters
);
