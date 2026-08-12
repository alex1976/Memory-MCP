using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Memories;

public sealed class MemoryGraphService(IMemoryEdgeRepository memoryEdgeRepository, IMemoryRepository memoryRepository) : IMemoryGraphService
{
    public async Task<IReadOnlyList<RelatedMemoryDto>> GetRelatedAsync(
        Guid rootMemoryId, Guid spaceId, int maxHops = 2, CancellationToken cancellationToken = default)
    {
        var related = await memoryEdgeRepository.GetRelatedAsync(rootMemoryId, maxHops, cancellationToken);
        if (related.Count == 0)
        {
            return [];
        }

        var memories = await memoryRepository.GetByIdsAsync(spaceId, related.Select(r => r.MemoryId).ToList(), cancellationToken);
        var byId = memories.ToDictionary(m => m.Id);

        return related
            .Where(r => byId.ContainsKey(r.MemoryId))
            .Select(r => new RelatedMemoryDto(r.MemoryId, byId[r.MemoryId].Text, r.RelationType, r.Hops, byId[r.MemoryId].IsActive, r.Direction))
            .ToList();
    }
}
