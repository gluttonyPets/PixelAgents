using PixelAgents.Domain.Entities;

namespace PixelAgents.Domain.Interfaces;

public interface IWorkspaceRepository
{
    Task<Workspace?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Workspace?> GetWithAgentsAsync(Guid id, CancellationToken ct = default);
    Task<List<Workspace>> GetAllAsync(CancellationToken ct = default);
    Task<Workspace> AddAsync(Workspace workspace, CancellationToken ct = default);
    Task UpdateAsync(Workspace workspace, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
