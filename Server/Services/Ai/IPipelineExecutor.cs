using Server.Data;
using Server.Models;

namespace Server.Services.Ai
{
    public interface IPipelineExecutor
    {
        Task<ProjectExecution> ExecuteAsync(Guid projectId, string? userInput, UserDbContext db, string tenantDbName);
    }
}
