using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record MemorySearchHit(Memory Memory, double Score);

public interface IMemoryRepository
{
    void Add(Memory memory);

    Task<(IReadOnlyList<Memory> Items, int TotalCount)> ListAsync(
        Guid spaceId, int page, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        Guid spaceId, float[] queryEmbedding, int topK, string? category = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Memory>> SearchByKeywordAsync(
        Guid spaceId, string keyword, int topK, string? category = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Memory>> ListByCategoryAsync(
        Guid spaceId, string category, int take, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Memory>> ListRecentActiveAsync(
        Guid spaceId, int take, CancellationToken cancellationToken = default);
}
