using Microsoft.AspNetCore.SignalR;

namespace Server.Hubs
{
    public class ExecutionHub : Hub
    {
        /// <summary>
        /// Client joins a group identified by projectId to receive execution logs for that project.
        /// </summary>
        public async Task JoinProject(string projectId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, projectId);
        }

        public async Task LeaveProject(string projectId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, projectId);
        }
    }

    public record ExecutionLogEntry(
        string Level,       // "info", "success", "error", "warning"
        string Message,
        int? StepOrder,
        string? StepName,
        DateTime Timestamp
    );

    /// <summary>
    /// Service injected into PipelineExecutor to send logs via SignalR.
    /// </summary>
    public interface IExecutionLogger
    {
        Task LogAsync(Guid projectId, Guid executionId, string level, string message, int? stepOrder = null, string? stepName = null);
    }

    public class SignalRExecutionLogger : IExecutionLogger
    {
        private readonly IHubContext<ExecutionHub> _hub;

        public SignalRExecutionLogger(IHubContext<ExecutionHub> hub)
        {
            _hub = hub;
        }

        public async Task LogAsync(Guid projectId, Guid executionId, string level, string message, int? stepOrder = null, string? stepName = null)
        {
            var entry = new ExecutionLogEntry(level, message, stepOrder, stepName, DateTime.UtcNow);
            // Broadcast to the project group so clients on the project page receive logs
            await _hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionLog", entry);
        }
    }
}
