using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPipelineExecutor
    {
        Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> RetryFromStepAsync(Guid executionId, int fromStepOrder, string? comment, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromInteractionAsync(Guid executionId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> ResumeFromBranchInteractionAsync(Guid executionId, string branchId, string responseText, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> AbortFromInteractionAsync(Guid executionId, UserDbContext db, string tenantDbName);
    }
}
