using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPipelineExecutor
    {
        Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default, bool useHistory = true);
        Task<ProjectExecution> RetryFromModuleAsync(Guid executionId, Guid moduleId, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> RetryFromStepAsync(Guid executionId, int fromStepOrder, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromInteractionAsync(Guid executionId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromBranchInteractionAsync(Guid executionId, string branchId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> AbortFromInteractionAsync(Guid executionId, UserDbContext db, string tenantDbName);
        Task<ProjectExecution> ResumeFromOrchestratorAsync(Guid executionId, bool approved, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromCheckpointAsync(Guid executionId, bool approved, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task SendNextQueuedInteractionAsync(Guid executionId, string chatId);
        Task CancelQueuedInteractionsAsync(Guid executionId);
    }
}
