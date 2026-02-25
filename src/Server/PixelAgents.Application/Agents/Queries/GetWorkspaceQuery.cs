using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Agents.Queries;

public record GetWorkspaceQuery(Guid WorkspaceId) : IRequest<WorkspaceDto?>;
