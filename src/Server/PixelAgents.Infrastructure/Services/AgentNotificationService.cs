using Microsoft.AspNetCore.SignalR;
using PixelAgents.Application.Common.Interfaces;
using PixelAgents.Domain.Enums;
using PixelAgents.Shared.Contracts;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Infrastructure.Services;

public class AgentNotificationService : IAgentNotificationService
{
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;

    public AgentNotificationService(IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task NotifyAgentStatusChanged(Guid agentId, string agentName, AgentStatus status, string? activity)
    {
        await _hubContext.Clients.All.AgentStatusChanged(
            agentId, agentName, Enum.Parse<AgentStatusDto>(status.ToString()), activity);
    }

    public async Task NotifyPipelineProgress(Guid pipelineId, Guid stepId, string stepName, string status, double progress, string? message)
    {
        await _hubContext.Clients.All.PipelineProgress(pipelineId, stepId, stepName, status, progress, message);
    }

    public async Task NotifyTaskCompleted(Guid taskId, Guid agentId, string agentName, string taskTitle, string? resultSummary)
    {
        await _hubContext.Clients.All.TaskCompleted(taskId, agentId, agentName, taskTitle, resultSummary);
    }

    public async Task NotifyAgentMessage(Guid agentId, string agentName, string message, string messageType)
    {
        await _hubContext.Clients.All.AgentMessage(agentId, agentName, message, messageType);
    }

    public async Task NotifyWorkspaceUpdated(Guid workspaceId)
    {
        await _hubContext.Clients.All.WorkspaceUpdated(workspaceId);
    }
}

public class AgentHub : Hub<IAgentHubClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "office");
        await base.OnConnectedAsync();
    }

    public async Task JoinWorkspace(string workspaceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"workspace-{workspaceId}");
    }

    public async Task LeaveWorkspace(string workspaceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"workspace-{workspaceId}");
    }
}
