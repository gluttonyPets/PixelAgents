using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Agents.Queries;

public record GetWorkspaceAgentsQuery(Guid WorkspaceId) : IRequest<List<AgentDto>>;
