using Microsoft.AspNetCore.SignalR;
using Server.Data;
using Server.Models;
using Server.Services;

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
        /// Returns a wrapper that persists logs using an independent DB connection so the logger
        /// never shares a DbContext with the pipeline (avoids concurrent-operation exceptions when
        /// graph nodes run in parallel and all call LogAsync at the same time).
        /// </summary>
        IExecutionLogger WithDb(UserDbContext db);

        IExecutionLogger WithTenant(ITenantDbContextFactory factory, string tenantDbName);
    }

    public class SignalRExecutionLogger : IExecutionLogger
    {
        private readonly IHubContext<ExecutionHub> _hub;
        private readonly ITenantDbContextFactory? _factory;
        private readonly string? _tenantDbName;

        public SignalRExecutionLogger(IHubContext<ExecutionHub> hub)
        {
            _hub = hub;
        }

        private SignalRExecutionLogger(IHubContext<ExecutionHub> hub, ITenantDbContextFactory factory, string tenantDbName)
        {
            _hub = hub;
            _factory = factory;
            _tenantDbName = tenantDbName;
        }

        // Legacy overload kept for compatibility — internally uses the factory path via a shared context.
        // Prefer WithTenant for new call sites.
        public IExecutionLogger WithDb(UserDbContext db) => this;

        public IExecutionLogger WithTenant(ITenantDbContextFactory factory, string tenantDbName)
            => new SignalRExecutionLogger(_hub, factory, tenantDbName);

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

            // Persist using a short-lived independent context so this logger never competes
            // with the pipeline's own DbContext operations (concurrent SaveChangesAsync would
            // corrupt the context state when graph nodes execute in parallel).
            if (_factory is not null && _tenantDbName is not null)
            {
                try
                {
                    await using var db = _factory.Create(_tenantDbName);
                    db.ExecutionLogs.Add(new ExecutionLog
                    {
                        Id = Guid.NewGuid(),
                        ExecutionId = executionId,
                        Level = level,
                        Message = message,
                        ProjectModuleId = projectModuleId,
                        ModuleName = moduleName,
                        Timestamp = timestamp
                    });
                    await db.SaveChangesAsync();
                }
                catch { /* Don't fail the pipeline if log persistence fails */ }
            }
        }
    }
}
