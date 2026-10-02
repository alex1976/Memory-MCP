namespace MemoryMcp.Application.Memories;

/// <summary>Graph traversal use case built on top of <see cref="Abstractions.IMemoryEdgeRepository"/>, enriching
/// the raw edge traversal with the related memories' text so callers don't need a second round trip.</summary>
public interface IMemoryGraphService
{
    /// <summary>Neighborhoods of several roots in one go (two edge traversals and one memory lookup in total,
    /// however many roots). Roots with nothing related have no entry in the result.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<RelatedMemoryDto>>> GetRelatedAsync(
        IReadOnlyList<Guid> rootMemoryIds, Guid spaceId, int maxHops = 2, CancellationToken cancellationToken = default);

    /// <summary>Whole-space view (as opposed to <see cref="GetRelatedAsync"/>'s per-root neighborhoods):
    /// the most recent <paramref name="maxNodes"/> memories (any status) plus the edges between them,
    /// for the memory-graph widget.</summary>
    Task<SpaceGraphDto> GetSpaceGraphAsync(Guid spaceId, int maxNodes = 50, CancellationToken cancellationToken = default);
}
