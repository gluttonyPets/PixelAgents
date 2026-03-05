using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPipelineExecutor
    {
        Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, UserDbContext db, string tenantDbName, CancellationToken ct = default);
        Task<ProjectExecution> RetryFromStepAsync(Guid executionId, int fromStepOrder, string? comment, UserDbContext db, CancellationToken ct = default);
    }
}
