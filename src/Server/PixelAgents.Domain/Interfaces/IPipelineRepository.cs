using PixelAgents.Domain.Entities;

namespace PixelAgents.Domain.Interfaces;

public interface IPipelineRepository
{
    Task<Pipeline?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Pipeline?> GetWithStepsAsync(Guid id, CancellationToken ct = default);
    Task<List<Pipeline>> GetByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    Task<Pipeline> AddAsync(Pipeline pipeline, CancellationToken ct = default);
    Task UpdateAsync(Pipeline pipeline, CancellationToken ct = default);
}
