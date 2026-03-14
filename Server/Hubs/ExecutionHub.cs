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
        int? StepOrder,
        string? StepName,
        DateTime Timestamp
    );

    public interface IExecutionLogger
    {
        Task LogAsync(Guid projectId, Guid executionId, string level, string message,
            int? stepOrder = null, string? stepName = null);

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

        public async Task LogAsync(Guid projectId, Guid executionId, string level, string message,
            int? stepOrder = null, string? stepName = null)
        {
            var timestamp = DateTime.UtcNow;
            var entry = new ExecutionLogEntry(level, message, stepOrder, stepName, timestamp);

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
                        StepOrder = stepOrder,
                        StepName = stepName,
                        Timestamp = timestamp
                    });
                    await _db.SaveChangesAsync();
                }
                catch { /* Don't fail the pipeline if log persistence fails */ }
            }
        }
    }
}
