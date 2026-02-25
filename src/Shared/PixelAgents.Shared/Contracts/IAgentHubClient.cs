using PixelAgents.Shared.Enums;

namespace PixelAgents.Shared.Contracts;

public interface IAgentHubClient
{
    Task AgentStatusChanged(Guid agentId, string agentName, AgentStatusDto status, string? activity);
    Task PipelineProgress(Guid pipelineId, Guid stepId, string stepName, string status, double progress, string? message);
    Task TaskCompleted(Guid taskId, Guid agentId, string agentName, string taskTitle, string? resultSummary);
    Task AgentMessage(Guid agentId, string agentName, string message, string messageType);
    Task WorkspaceUpdated(Guid workspaceId);
}
