using MediatR;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.AgentSystem.Discovery;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Application.Agents.Commands;

public class CreateWorkspaceHandler : IRequestHandler<CreateWorkspaceCommand, WorkspaceDto>
{
    private readonly IWorkspaceRepository _workspaceRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly AgentModuleRegistry _registry;

    public CreateWorkspaceHandler(
        IWorkspaceRepository workspaceRepo,
        IAgentRepository agentRepo,
        AgentModuleRegistry registry)
    {
        _workspaceRepo = workspaceRepo;
        _agentRepo = agentRepo;
        _registry = registry;
    }

    public async Task<WorkspaceDto> Handle(CreateWorkspaceCommand request, CancellationToken cancellationToken)
    {
        var workspace = new Workspace
        {
            Name = request.Name,
            Description = request.Description,
            Theme = request.Theme
        };

        workspace = await _workspaceRepo.AddAsync(workspace, cancellationToken);

        var agents = new List<Agent>();
        foreach (var module in _registry.GetAllModules())
        {
            var agent = new Agent
            {
                Name = module.DisplayName,
                Role = module.Role,
                Description = module.Description,
                ModuleKey = module.ModuleKey,
                Appearance = module.DefaultAppearance,
                Skills = module.Skills,
                Personality = module.Personality,
                WorkspaceId = workspace.Id
            };
            agent = await _agentRepo.AddAsync(agent, cancellationToken);
            agents.Add(agent);
        }

        return new WorkspaceDto(
            workspace.Id,
            workspace.Name,
            workspace.Description,
            workspace.Theme,
            agents.Select(a => new AgentDto(
                a.Id, a.Name, a.Role, a.Description, a.ModuleKey,
                AgentStatusDto.Idle,
                new AvatarAppearanceDto(
                    a.Appearance.SpriteSheet, a.Appearance.IdleAnimation,
                    a.Appearance.WorkingAnimation, a.Appearance.ThinkingAnimation,
                    a.Appearance.OfficePositionX, a.Appearance.OfficePositionY,
                    a.Appearance.DeskStyle),
                a.Skills.Select(s => new AgentSkillDto(
                    s.Type.ToString(), s.Name, s.Level, s.Description)).ToList(),
                a.Personality, null
            )).ToList(),
            []
        );
    }
}
