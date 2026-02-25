using Microsoft.EntityFrameworkCore;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Interfaces;

namespace PixelAgents.Infrastructure.Persistence.Repositories;

public class AgentRepository : IAgentRepository
{
    private readonly ApplicationDbContext _context;

    public AgentRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Agents.FindAsync([id], ct);

    public async Task<List<Agent>> GetByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await _context.Agents.Where(a => a.WorkspaceId == workspaceId).ToListAsync(ct);

    public async Task<Agent?> GetByModuleKeyAsync(string moduleKey, CancellationToken ct = default)
        => await _context.Agents.FirstOrDefaultAsync(a => a.ModuleKey == moduleKey, ct);

    public async Task<Agent> AddAsync(Agent agent, CancellationToken ct = default)
    {
        _context.Agents.Add(agent);
        await _context.SaveChangesAsync(ct);
        return agent;
    }

    public async Task UpdateAsync(Agent agent, CancellationToken ct = default)
    {
        _context.Agents.Update(agent);
        await _context.SaveChangesAsync(ct);
    }
}
