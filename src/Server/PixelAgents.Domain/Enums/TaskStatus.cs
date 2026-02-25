namespace PixelAgents.Domain.Enums;

public enum AgentTaskStatus
{
    Pending,
    InProgress,
    WaitingForDependency,
    Completed,
    Failed,
    Cancelled
}
