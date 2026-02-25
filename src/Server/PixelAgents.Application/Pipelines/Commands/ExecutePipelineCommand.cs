using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Commands;

public record ExecutePipelineCommand(Guid PipelineId, Dictionary<string, object>? InitialParameters) : IRequest<PipelineDto>;
