using Microsoft.EntityFrameworkCore;
using PixelAgents.Domain.Entities;
using PixelAgents.Domain.Interfaces;

namespace PixelAgents.Infrastructure.Persistence.Repositories;

public class PipelineRepository : IPipelineRepository
{
    private readonly ApplicationDbContext _context;

    public PipelineRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Pipeline?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _context.Pipelines.FindAsync([id], ct);

    public async Task<Pipeline?> GetWithStepsAsync(Guid id, CancellationToken ct = default)
        => await _context.Pipelines
            .Include(p => p.Steps).ThenInclude(s => s.AssignedAgent)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<List<Pipeline>> GetByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default)
        => await _context.Pipelines
            .Include(p => p.Steps)
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(ct);

    public async Task<Pipeline> AddAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        _context.Pipelines.Add(pipeline);
        await _context.SaveChangesAsync(ct);
        return pipeline;
    }

    public async Task UpdateAsync(Pipeline pipeline, CancellationToken ct = default)
    {
        _context.Pipelines.Update(pipeline);
        await _context.SaveChangesAsync(ct);
    }
}
