using MediatR;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Application.Agents.Queries;

public class GetWorkspaceAgentsHandler : IRequestHandler<GetWorkspaceAgentsQuery, List<AgentDto>>
{
    private readonly IAgentRepository _agentRepo;

    public GetWorkspaceAgentsHandler(IAgentRepository agentRepo)
    {
        _agentRepo = agentRepo;
    }

    public async Task<List<AgentDto>> Handle(GetWorkspaceAgentsQuery request, CancellationToken cancellationToken)
    {
        var agents = await _agentRepo.GetByWorkspaceAsync(request.WorkspaceId, cancellationToken);

        return agents.Select(a => new AgentDto(
            a.Id,
            a.Name,
            a.Role,
            a.Description,
            a.ModuleKey,
            Enum.Parse<AgentStatusDto>(a.Status.ToString()),
            new AvatarAppearanceDto(
                a.Appearance.SpriteSheet,
                a.Appearance.IdleAnimation,
                a.Appearance.WorkingAnimation,
                a.Appearance.ThinkingAnimation,
                a.Appearance.OfficePositionX,
                a.Appearance.OfficePositionY,
                a.Appearance.DeskStyle),
            a.Skills.Select(s => new AgentSkillDto(
                s.Type.ToString(), s.Name, s.Level, s.Description)).ToList(),
            a.Personality,
            a.Status == Domain.Enums.AgentStatus.Working ? "Working..." : null
        )).ToList();
    }
}
