using MediatR;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Queries;

public class GetPipelineHandler : IRequestHandler<GetPipelineQuery, PipelineDto?>
{
    private readonly IPipelineRepository _pipelineRepo;

    public GetPipelineHandler(IPipelineRepository pipelineRepo)
    {
        _pipelineRepo = pipelineRepo;
    }

    public async Task<PipelineDto?> Handle(GetPipelineQuery request, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineRepo.GetWithStepsAsync(request.PipelineId, cancellationToken);
        if (pipeline is null) return null;

        return new PipelineDto(
            pipeline.Id,
            pipeline.Name,
            pipeline.Description,
            pipeline.Status.ToString(),
            pipeline.Steps.OrderBy(s => s.Order).Select(s => new PipelineStepDto(
                s.Id, s.Order, s.Name, s.ModuleKey, s.Status.ToString(),
                s.AssignedAgentId, s.AssignedAgent?.Name ?? "Unassigned"
            )).ToList(),
            pipeline.StartedAt,
            pipeline.CompletedAt
        );
    }
}
