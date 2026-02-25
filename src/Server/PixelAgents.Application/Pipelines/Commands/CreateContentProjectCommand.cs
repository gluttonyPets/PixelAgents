using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Commands;

public record CreateContentProjectCommand(
    string Topic,
    List<string> TargetPlatforms,
    Guid WorkspaceId,
    Dictionary<string, object>? AdditionalParameters
) : IRequest<ContentProjectDto>;
