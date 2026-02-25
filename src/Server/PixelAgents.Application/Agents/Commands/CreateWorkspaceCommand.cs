using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Agents.Commands;

public record CreateWorkspaceCommand(string Name, string Description, string Theme) : IRequest<WorkspaceDto>;
