using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record RelatedMemory(Guid MemoryId, RelationType RelationType, int Hops);

public interface IMemoryEdgeRepository
{
    void Add(MemoryEdge edge);

    /// <summary>Traverses outgoing edges from <paramref name="rootMemoryId"/> up to <paramref name="maxHops"/> hops.</summary>
    Task<IReadOnlyList<RelatedMemory>> GetRelatedAsync(
        Guid rootMemoryId, int maxHops, CancellationToken cancellationToken = default);
}
