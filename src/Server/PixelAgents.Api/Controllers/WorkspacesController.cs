using MediatR;
using Microsoft.AspNetCore.Mvc;
using PixelAgents.Application.Agents.Commands;
using PixelAgents.Application.Agents.Queries;
using PixelAgents.Shared.DTOs;

namespace PixelAgents.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WorkspacesController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkspacesController(IMediator mediator)
    {
        _mediator = mediator;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkspaceDto>> GetWorkspace(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetWorkspaceQuery(id), ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<WorkspaceDto>> CreateWorkspace([FromBody] CreateWorkspaceRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(
            new CreateWorkspaceCommand(request.Name, request.Description, request.Theme), ct);
        return CreatedAtAction(nameof(GetWorkspace), new { id = result.Id }, result);
    }

    [HttpGet("{id:guid}/agents")]
    public async Task<ActionResult<List<AgentDto>>> GetAgents(Guid id, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetWorkspaceAgentsQuery(id), ct);
        return Ok(result);
    }
}
