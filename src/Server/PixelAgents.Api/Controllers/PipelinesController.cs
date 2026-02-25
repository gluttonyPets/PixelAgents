using MediatR;
using Microsoft.AspNetCore.Mvc;
using PixelAgents.Application.Pipelines.Commands;
using PixelAgents.Application.Pipelines.Queries;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PipelinesController : ControllerBase
{
    private readonly IMediator _mediator;

    public PipelinesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PipelineDto>> GetPipeline(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetPipelineQuery(id), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost("execute")]
    public async Task<ActionResult<PipelineDto>> ExecutePipeline([FromBody] ExecutePipelineRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new ExecutePipelineCommand(request.PipelineId, request.InitialParameters), ct);
        return Ok(result);
    }
}
