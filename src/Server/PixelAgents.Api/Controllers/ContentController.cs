using MediatR;
using Microsoft.AspNetCore.Mvc;
using PixelAgents.Application.Pipelines.Commands;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ContentController : ControllerBase
{
    private readonly IMediator _mediator;

    public ContentController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpPost("projects")]
    public async Task<ActionResult<ContentProjectDto>> CreateProject([FromBody] CreateContentProjectRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new CreateContentProjectCommand(
                request.Topic,
                request.TargetPlatforms,
                request.WorkspaceId,
                request.AdditionalParameters), ct);
        return CreatedAtAction(null, result);
    }
}
