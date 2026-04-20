using Microsoft.AspNetCore.SignalR;
using Server.Data;
using Server.Models;

namespace Server.Hubs
{
    public class ExecutionHub : Hub
    {
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
        string Level,
        string Message,
        Guid? ProjectModuleId,
        string? ModuleName,
        DateTime Timestamp
    );

    public record StepProgressEntry(
        Guid ProjectModuleId,
        string Status   // "Running", "Completed", "Failed", "Cancelled"
    );

    public record OrchestratorTaskProgressEntry(
        string TaskId,
        string Description,
        string ModuleName,
        string ModuleType,
        int Order,
        string Status,        // "running", "completed", "error"
        string? FileUrl,      // relative file path if produced
        string? ContentType,  // mime type of file
        string? ErrorMessage,
        DateTime Timestamp
    );

    public interface IExecutionLogger
    {
        Task LogAsync(Guid projectId, Guid executionId, string level, string message,
            Guid? projectModuleId = null, string? moduleName = null);

        Task LogTaskProgressAsync(Guid projectId, OrchestratorTaskProgressEntry progress);

        Task LogStepProgressAsync(Guid projectId, Guid projectModuleId, string status);

        /// <summary>
        /// Returns a wrapper that persists all logs to the given DB context.
        /// </summary>
        IExecutionLogger WithDb(UserDbContext db);
    }

    public class SignalRExecutionLogger : IExecutionLogger
    {
        private readonly IHubContext<ExecutionHub> _hub;
        private readonly UserDbContext? _db;

        public SignalRExecutionLogger(IHubContext<ExecutionHub> hub)
        {
            _hub = hub;
        }

        private SignalRExecutionLogger(IHubContext<ExecutionHub> hub, UserDbContext db)
        {
            _hub = hub;
            _db = db;
        }

        public IExecutionLogger WithDb(UserDbContext db) => new SignalRExecutionLogger(_hub, db);

        public async Task LogTaskProgressAsync(Guid projectId, OrchestratorTaskProgressEntry progress)
        {
            await _hub.Clients.Group(projectId.ToString())
                .SendAsync("OrchestratorTaskProgress", progress);
        }

        public async Task LogStepProgressAsync(Guid projectId, Guid projectModuleId, string status)
        {
            await _hub.Clients.Group(projectId.ToString())
                .SendAsync("StepProgress", new StepProgressEntry(projectModuleId, status));
        }

        public async Task LogAsync(Guid projectId, Guid executionId, string level, string message,
            Guid? projectModuleId = null, string? moduleName = null)
        {
            var timestamp = DateTime.UtcNow;
            var entry = new ExecutionLogEntry(level, message, projectModuleId, moduleName, timestamp);

            // Broadcast via SignalR (real-time)
            await _hub.Clients.Group(projectId.ToString())
                .SendAsync("ExecutionLog", entry);

            // Persist to DB
            if (_db is not null)
            {
                try
                {
                    _db.ExecutionLogs.Add(new ExecutionLog
                    {
                        Id = Guid.NewGuid(),
                        ExecutionId = executionId,
                        Level = level,
                        Message = message,
                        ProjectModuleId = projectModuleId,
                        ModuleName = moduleName,
                        Timestamp = timestamp
                    });
                    await _db.SaveChangesAsync();
                }
                catch { /* Don't fail the pipeline if log persistence fails */ }
            }
        }
    }
}
