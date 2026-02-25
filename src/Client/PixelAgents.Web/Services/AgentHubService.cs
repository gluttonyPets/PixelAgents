using Microsoft.AspNetCore.SignalR.Client;
using PixelAgents.Shared.Enums;

namespace PixelAgents.Web.Services;

public class AgentHubService : IAsyncDisposable
{
    private readonly HubConnection _hubConnection;

    public event Action<Guid, string, AgentStatusDto, string?>? OnAgentStatusChanged;
    public event Action<Guid, Guid, string, string, double, string?>? OnPipelineProgress;
    public event Action<Guid, Guid, string, string, string?>? OnTaskCompleted;
    public event Action<Guid, string, string, string>? OnAgentMessage;
    public event Action<Guid>? OnWorkspaceUpdated;

    public bool IsConnected => _hubConnection.State == HubConnectionState.Connected;

    public AgentHubService(string hubUrl)
    {
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<Guid, string, AgentStatusDto, string?>("AgentStatusChanged",
            (agentId, name, status, activity) => OnAgentStatusChanged?.Invoke(agentId, name, status, activity));

        _hubConnection.On<Guid, Guid, string, string, double, string?>("PipelineProgress",
            (pipelineId, stepId, stepName, status, progress, message) =>
                OnPipelineProgress?.Invoke(pipelineId, stepId, stepName, status, progress, message));

        _hubConnection.On<Guid, Guid, string, string, string?>("TaskCompleted",
            (taskId, agentId, agentName, taskTitle, result) =>
                OnTaskCompleted?.Invoke(taskId, agentId, agentName, taskTitle, result));

        _hubConnection.On<Guid, string, string, string>("AgentMessage",
            (agentId, agentName, message, type) =>
                OnAgentMessage?.Invoke(agentId, agentName, message, type));

        _hubConnection.On<Guid>("WorkspaceUpdated",
            workspaceId => OnWorkspaceUpdated?.Invoke(workspaceId));
    }

    public async Task StartAsync()
    {
        if (_hubConnection.State == HubConnectionState.Disconnected)
            await _hubConnection.StartAsync();
    }

    public async Task JoinWorkspaceAsync(string workspaceId)
    {
        await _hubConnection.InvokeAsync("JoinWorkspace", workspaceId);
    }

    public async ValueTask DisposeAsync()
    {
        await _hubConnection.DisposeAsync();
    }
}
