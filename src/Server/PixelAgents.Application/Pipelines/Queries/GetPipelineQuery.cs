using MediatR;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Application.Pipelines.Queries;

public record GetPipelineQuery(Guid PipelineId) : IRequest<PipelineDto?>;
