using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record MemorySearchHit(Memory Memory, double Score);

public interface IMemoryRepository
{
    void Add(Memory memory);

    Task<(IReadOnlyList<Memory> Items, int TotalCount)> ListAsync(
        Guid spaceId, int page, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        Guid spaceId, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Memory>> ListRecentActiveAsync(
        Guid spaceId, int take, CancellationToken cancellationToken = default);
}
