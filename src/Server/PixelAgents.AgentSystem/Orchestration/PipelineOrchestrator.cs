using MediatR;
using Microsoft.Extensions.Logging;
using PixelAgents.AgentSystem.Abstractions;
using PixelAgents.AgentSystem.Discovery;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Enums;
using PixelAgents.Domain.Events;
using PixelAgents.Domain.Interfaces;

namespace PixelAgents.AgentSystem.Orchestration;

public class PipelineOrchestrator
{
    private readonly AgentModuleRegistry _registry;
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IAgentRepository _agentRepo;
    private readonly IMediator _mediator;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        AgentModuleRegistry registry,
        IPipelineRepository pipelineRepo,
        IAgentRepository agentRepo,
        IMediator mediator,
        ILogger<PipelineOrchestrator> logger)
    {
        _registry = registry;
        _pipelineRepo = pipelineRepo;
        _agentRepo = agentRepo;
        _mediator = mediator;
        _logger = logger;
    }

    public async Task ExecutePipelineAsync(Guid pipelineId, CancellationToken ct = default)
    {
        var pipeline = await _pipelineRepo.GetWithStepsAsync(pipelineId, ct)
            ?? throw new InvalidOperationException($"Pipeline {pipelineId} not found.");

        pipeline.Status = PipelineStatus.Running;
        pipeline.StartedAt = DateTime.UtcNow;
        await _pipelineRepo.UpdateAsync(pipeline, ct);

        var pipelineData = new Dictionary<string, object>();

        foreach (var step in pipeline.Steps.OrderBy(s => s.Order))
        {
            var module = _registry.GetModule(step.ModuleKey);
            if (module is null)
            {
                _logger.LogError("Module '{ModuleKey}' not found for step '{StepName}'", step.ModuleKey, step.Name);
                step.Status = AgentTaskStatus.Failed;
                pipeline.Status = PipelineStatus.Failed;
                await _pipelineRepo.UpdateAsync(pipeline, ct);
                return;
            }

            var agent = await _agentRepo.GetByIdAsync(step.AssignedAgentId, ct);
            if (agent is not null)
            {
                agent.Status = AgentStatus.Working;
                await _agentRepo.UpdateAsync(agent, ct);
                await _mediator.Publish(new AgentStatusChangedEvent(
                    agent.Id, agent.Name, AgentStatus.Idle, AgentStatus.Working,
                    $"Working on: {step.Name}"), ct);
            }

            step.Status = AgentTaskStatus.InProgress;
            await _pipelineRepo.UpdateAsync(pipeline, ct);

            await _mediator.Publish(new PipelineProgressEvent(
                pipelineId, step.Id, step.Name, AgentTaskStatus.InProgress, 0,
                $"Agent {agent?.Name ?? "Unknown"} started working"), ct);

            var context = new AgentModuleContext
            {
                TaskId = step.Id,
                PipelineId = pipelineId,
                AgentId = step.AssignedAgentId,
                InputParameters = step.InputParameters,
                PipelineData = pipelineData,
                Progress = new Progress<AgentProgress>(p =>
                {
                    _mediator.Publish(new PipelineProgressEvent(
                        pipelineId, step.Id, step.Name,
                        AgentTaskStatus.InProgress, p.Percentage, p.Message), ct);
                })
            };

            var result = await module.ExecuteAsync(context, ct);

            if (result.Success)
            {
                step.Status = AgentTaskStatus.Completed;
                step.OutputData = result.OutputData;

                foreach (var kv in result.OutputData)
                    pipelineData[kv.Key] = kv.Value;

                await _mediator.Publish(new PipelineProgressEvent(
                    pipelineId, step.Id, step.Name, AgentTaskStatus.Completed, 100,
                    result.Summary ?? "Step completed"), ct);
            }
            else
            {
                step.Status = AgentTaskStatus.Failed;
                pipeline.Status = PipelineStatus.Failed;
                _logger.LogError("Step '{StepName}' failed: {Error}", step.Name, result.ErrorMessage);

                await _mediator.Publish(new PipelineProgressEvent(
                    pipelineId, step.Id, step.Name, AgentTaskStatus.Failed, 0,
                    result.ErrorMessage), ct);

                await _pipelineRepo.UpdateAsync(pipeline, ct);
                return;
            }

            if (agent is not null)
            {
                agent.Status = AgentStatus.Idle;
                await _agentRepo.UpdateAsync(agent, ct);
                await _mediator.Publish(new AgentStatusChangedEvent(
                    agent.Id, agent.Name, AgentStatus.Working, AgentStatus.Idle,
                    $"Completed: {step.Name}"), ct);
            }

            await _pipelineRepo.UpdateAsync(pipeline, ct);
        }

        pipeline.Status = PipelineStatus.Completed;
        pipeline.CompletedAt = DateTime.UtcNow;
        await _pipelineRepo.UpdateAsync(pipeline, ct);
    }
}
