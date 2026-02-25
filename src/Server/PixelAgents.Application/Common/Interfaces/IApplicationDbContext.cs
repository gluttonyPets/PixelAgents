using Microsoft.EntityFrameworkCore;
using PixelAgents.Domain.Entities;

namespace PixelAgents.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Workspace> Workspaces { get; }
    DbSet<Agent> Agents { get; }
    DbSet<Pipeline> Pipelines { get; }
    DbSet<PipelineStep> PipelineSteps { get; }
    DbSet<AgentTask> AgentTasks { get; }
    DbSet<ContentProject> ContentProjects { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
