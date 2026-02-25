namespace PixelAgents.Shared.Events;

public static class HubEvents
{
    public const string AgentStatusChanged = nameof(AgentStatusChanged);
    public const string PipelineProgress = nameof(PipelineProgress);
    public const string TaskCompleted = nameof(TaskCompleted);
    public const string AgentMessage = nameof(AgentMessage);
    public const string WorkspaceUpdated = nameof(WorkspaceUpdated);
}
