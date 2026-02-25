using MediatR;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Application.Agents.Queries;

public class GetWorkspaceHandler : IRequestHandler<GetWorkspaceQuery, WorkspaceDto?>
{
    private readonly IWorkspaceRepository _workspaceRepo;

    public GetWorkspaceHandler(IWorkspaceRepository workspaceRepo)
    {
        _workspaceRepo = workspaceRepo;
    }

    public async Task<WorkspaceDto?> Handle(GetWorkspaceQuery request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepo.GetWithAgentsAsync(request.WorkspaceId, cancellationToken);
        if (workspace is null) return null;

        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Description,
            workspace.Theme,
            workspace.Agents.Select(a => new AgentDto(
                a.Id, a.Name, a.Role, a.Description, a.ModuleKey,
                Enum.Parse<AgentStatusDto>(a.Status.ToString()),
                new AvatarAppearanceDto(
                    a.Appearance.SpriteSheet, a.Appearance.IdleAnimation,
                    a.Appearance.WorkingAnimation, a.Appearance.ThinkingAnimation,
                    a.Appearance.OfficePositionX, a.Appearance.OfficePositionY,
                    a.Appearance.DeskStyle),
                a.Skills.Select(s => new AgentSkillDto(
                    s.Type.ToString(), s.Name, s.Level, s.Description)).ToList(),
                a.Personality,
                a.Status == AgentStatus.Working ? "Working..." : null
            )).ToList(),
            workspace.Pipelines.Select(p => new PipelineSummaryDto(
                p.Id, p.Name, p.Status.ToString(),
                p.Steps.Count, p.Steps.Count(s => s.Status == AgentTaskStatus.Completed)
            )).ToList()
        );
    }
}
