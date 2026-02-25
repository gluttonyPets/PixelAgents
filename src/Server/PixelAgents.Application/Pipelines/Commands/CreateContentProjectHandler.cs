using MediatR;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Commands;

public class CreateContentProjectHandler : IRequestHandler<CreateContentProjectCommand, ContentProjectDto>
{
    private readonly IWorkspaceRepository _workspaceRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IMediator _mediator;

    public CreateContentProjectHandler(
        IWorkspaceRepository workspaceRepo,
        IAgentRepository agentRepo,
        IPipelineRepository pipelineRepo,
        IMediator mediator)
    {
        _workspaceRepo = workspaceRepo;
        _agentRepo = agentRepo;
        _pipelineRepo = pipelineRepo;
        _mediator = mediator;
    }

    public async Task<ContentProjectDto> Handle(CreateContentProjectCommand request, CancellationToken cancellationToken)
    {
        var workspace = await _workspaceRepo.GetWithAgentsAsync(request.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException($"Workspace {request.WorkspaceId} not found.");

        var agents = await _agentRepo.GetByWorkspaceAsync(request.WorkspaceId, cancellationToken);

        var trendScout = agents.FirstOrDefault(a => a.ModuleKey == "trend-scout")
            ?? throw new InvalidOperationException("TrendScout agent not found in workspace.");
        var templateForge = agents.FirstOrDefault(a => a.ModuleKey == "template-forge")
            ?? throw new InvalidOperationException("TemplateForge agent not found in workspace.");
        var contentWeaver = agents.FirstOrDefault(a => a.ModuleKey == "content-weaver")
            ?? throw new InvalidOperationException("ContentWeaver agent not found in workspace.");
        var scheduleMaster = agents.FirstOrDefault(a => a.ModuleKey == "schedule-master")
            ?? throw new InvalidOperationException("ScheduleMaster agent not found in workspace.");

        var pipeline = new Pipeline
        {
            Name = $"Content: {request.Topic}",
            Description = $"Content creation pipeline for topic: {request.Topic}",
            WorkspaceId = request.WorkspaceId,
            Steps =
            [
                new PipelineStep
                {
                    Order = 1,
                    Name = "Research Trending Topics",
                    ModuleKey = "trend-scout",
                    AssignedAgentId = trendScout.Id,
                    InputParameters = new Dictionary<string, object>
                    {
                        ["topic"] = request.Topic,
                        ["platforms"] = request.TargetPlatforms
                    }
                },
                new PipelineStep
                {
                    Order = 2,
                    Name = "Create Visual Template",
                    ModuleKey = "template-forge",
                    AssignedAgentId = templateForge.Id,
                    InputParameters = new Dictionary<string, object>
                    {
                        ["platforms"] = request.TargetPlatforms
                    }
                },
                new PipelineStep
                {
                    Order = 3,
                    Name = "Assemble Content",
                    ModuleKey = "content-weaver",
                    AssignedAgentId = contentWeaver.Id
                },
                new PipelineStep
                {
                    Order = 4,
                    Name = "Schedule Publishing",
                    ModuleKey = "schedule-master",
                    AssignedAgentId = scheduleMaster.Id,
                    InputParameters = new Dictionary<string, object>
                    {
                        ["platforms"] = request.TargetPlatforms
                    }
                }
            ]
        };

        pipeline = await _pipelineRepo.AddAsync(pipeline, cancellationToken);

        var platforms = request.TargetPlatforms.Select(p =>
            Enum.TryParse<SocialPlatform>(p, true, out var sp) ? sp : (SocialPlatform?)null)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .ToList();

        return new ContentProjectDto(
            Guid.NewGuid(),
            $"Content: {request.Topic}",
            request.Topic,
            null,
            null,
            request.TargetPlatforms,
            null,
            pipeline.Id,
            pipeline.Status.ToString()
        );
    }
}
