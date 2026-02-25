using PixelAgents.Domain.Enums;

namespace PixelAgents.Application.Common.Interfaces;

public interface IAgentNotificationService
{
    Task NotifyAgentStatusChanged(Guid agentId, string agentName, AgentStatus status, string? activity);
    Task NotifyPipelineProgress(Guid pipelineId, Guid stepId, string stepName, string status, double progress, string? message);
    Task NotifyTaskCompleted(Guid taskId, Guid agentId, string agentName, string taskTitle, string? resultSummary);
    Task NotifyAgentMessage(Guid agentId, string agentName, string message, string messageType);
    Task NotifyWorkspaceUpdated(Guid workspaceId);
}
