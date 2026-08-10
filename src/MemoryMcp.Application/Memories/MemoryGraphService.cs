using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Memories;

public sealed class MemoryGraphService(IMemoryEdgeRepository memoryEdgeRepository, IMemoryRepository memoryRepository) : IMemoryGraphService
{
    public async Task<IReadOnlyList<RelatedMemoryDto>> GetRelatedAsync(
        Guid rootMemoryId, int maxHops = 2, CancellationToken cancellationToken = default)
    {
        var related = await memoryEdgeRepository.GetRelatedAsync(rootMemoryId, maxHops, cancellationToken);
        if (related.Count == 0)
        {
            return [];
        }

        var memories = await memoryRepository.GetByIdsAsync(related.Select(r => r.MemoryId).ToList(), cancellationToken);
        var textById = memories.ToDictionary(m => m.Id, m => m.Text);

        return related
            .Select(r => new RelatedMemoryDto(r.MemoryId, textById.GetValueOrDefault(r.MemoryId, string.Empty), r.RelationType, r.Hops))
            .ToList();
    }
}
