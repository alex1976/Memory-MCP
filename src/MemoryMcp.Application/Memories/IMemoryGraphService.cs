namespace MemoryMcp.Application.Memories;

/// <summary>Graph traversal use case built on top of <see cref="Abstractions.IMemoryEdgeRepository"/>, enriching
/// the raw edge traversal with the related memories' text so callers don't need a second round trip.</summary>
public interface IMemoryGraphService
{
    Task<IReadOnlyList<RelatedMemoryDto>> GetRelatedAsync(
        Guid rootMemoryId, Guid spaceId, int maxHops = 2, CancellationToken cancellationToken = default);
}
