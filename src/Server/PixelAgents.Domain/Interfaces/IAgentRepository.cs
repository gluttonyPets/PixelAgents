using PixelAgents.Domain.Entities;

namespace PixelAgents.Domain.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Agent>> GetByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Agent?> GetByModuleKeyAsync(string moduleKey, CancellationToken ct = default);
    Task<Agent> AddAsync(Agent agent, CancellationToken ct = default);
    Task UpdateAsync(Agent agent, CancellationToken ct = default);
}
