using MediatR;
using PixelAgents.AgentSystem.Orchestration;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.Interfaces;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Commands;

public class ExecutePipelineHandler : IRequestHandler<ExecutePipelineCommand, PipelineDto>
{
    private readonly PipelineOrchestrator _orchestrator;
    private readonly IPipelineRepository _pipelineRepo;

    public ExecutePipelineHandler(PipelineOrchestrator orchestrator, IPipelineRepository pipelineRepo)
    {
        _orchestrator = orchestrator;
        _pipelineRepo = pipelineRepo;
    }

    public async Task<PipelineDto> Handle(ExecutePipelineCommand request, CancellationToken cancellationToken)
    {
        var pipeline = await _pipelineRepo.GetWithStepsAsync(request.PipelineId, cancellationToken)
            ?? throw new InvalidOperationException($"Pipeline {request.PipelineId} not found.");

        if (request.InitialParameters is not null)
        {
            var firstStep = pipeline.Steps.OrderBy(s => s.Order).FirstOrDefault();
            if (firstStep is not null)
            {
                foreach (var kv in request.InitialParameters)
                    firstStep.InputParameters[kv.Key] = kv.Value;
                await _pipelineRepo.UpdateAsync(pipeline, cancellationToken);
            }
        }

        _ = Task.Run(() => _orchestrator.ExecutePipelineAsync(request.PipelineId, CancellationToken.None), cancellationToken);

        return new PipelineDto(
            pipeline.Id,
            pipeline.Name,
            pipeline.Description,
            PipelineStatus.Running.ToString(),
            pipeline.Steps.OrderBy(s => s.Order).Select(s => new PipelineStepDto(
                s.Id, s.Order, s.Name, s.ModuleKey, s.Status.ToString(),
                s.AssignedAgentId, s.AssignedAgent?.Name ?? "Unassigned"
            )).ToList(),
            DateTime.UtcNow,
            null
        );
    }
}
