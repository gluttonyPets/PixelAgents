using Microsoft.EntityFrameworkCore;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Interfaces;

namespace PixelAgents.Infrastructure.Persistence.Repositories;

public class WorkspaceRepository : IWorkspaceRepository
{
    private readonly ApplicationDbContext _context;

    public WorkspaceRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Workspaces.FindAsync([id], ct);

    public async Task<Workspace?> GetWithAgentsAsync(Guid id, CancellationToken ct = default)
        => await _context.Workspaces
            .Include(w => w.Agents)
            .Include(w => w.Pipelines).ThenInclude(p => p.Steps)
            .FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task<List<Workspace>> GetAllAsync(CancellationToken ct = default)
        => await _context.Workspaces.Include(w => w.Agents).ToListAsync(ct);

    public async Task<Workspace> AddAsync(Workspace workspace, CancellationToken ct = default)
    {
        _context.Workspaces.Add(workspace);
        await _context.SaveChangesAsync(ct);
        return workspace;
    }

    public async Task UpdateAsync(Workspace workspace, CancellationToken ct = default)
    {
        _context.Workspaces.Update(workspace);
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var workspace = await _context.Workspaces.FindAsync([id], ct);
        if (workspace is not null)
        {
            _context.Workspaces.Remove(workspace);
            await _context.SaveChangesAsync(ct);
        }
    }
}
