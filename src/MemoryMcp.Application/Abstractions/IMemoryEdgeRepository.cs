using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

/// <summary>Whether <see cref="RelatedMemory.RelationType"/> was traversed following an edge that starts at the
/// root memory (Outgoing) or one that ends at it (Incoming) — the two require opposite phrasing when displayed,
/// e.g. Outgoing Updates means "root updates this", Incoming Updates means "this updates root".</summary>
public enum RelatedMemoryDirection
{
    Outgoing,
    Incoming,
}

public sealed record RelatedMemory(Guid MemoryId, RelationType RelationType, int Hops, RelatedMemoryDirection Direction);

public interface IMemoryEdgeRepository
{
    void Add(MemoryEdge edge);

    /// <summary>Traverses edges connected to <paramref name="rootMemoryId"/> up to <paramref name="maxHops"/> hops,
    /// in both directions (see <see cref="RelatedMemoryDirection"/>), since an edge's From/To reflects only which
    /// memory was newer at the time it was created, not which one a caller will end up searching for.</summary>
    Task<IReadOnlyList<RelatedMemory>> GetRelatedAsync(
        Guid rootMemoryId, int maxHops, CancellationToken cancellationToken = default);

    /// <summary>All edges in a space, unbounded by any root memory — used by the memory-graph widget to
    /// render a whole-space view rather than a single memory's neighborhood.</summary>
    Task<IReadOnlyList<MemoryEdge>> ListEdgesAsync(Guid spaceId, CancellationToken cancellationToken = default);
}
