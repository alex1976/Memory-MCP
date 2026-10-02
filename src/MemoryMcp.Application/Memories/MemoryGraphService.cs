using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Memories;

public sealed class MemoryGraphService(IMemoryEdgeRepository memoryEdgeRepository, IMemoryRepository memoryRepository) : IMemoryGraphService
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RelatedMemoryDto>>> GetRelatedAsync(
        IReadOnlyList<Guid> rootMemoryIds, Guid spaceId, int maxHops = 2, CancellationToken cancellationToken = default)
    {
        var related = await memoryEdgeRepository.GetRelatedAsync(rootMemoryIds, maxHops, cancellationToken);
        if (related.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<RelatedMemoryDto>>();
        }

        var relatedIds = related.Values.SelectMany(r => r).Select(r => r.MemoryId).Distinct().ToList();
        var memories = await memoryRepository.GetByIdsAsync(spaceId, relatedIds, cancellationToken);
        var byId = memories.ToDictionary(m => m.Id);

        return related
            .Select(entry => (
                entry.Key,
                Related: (IReadOnlyList<RelatedMemoryDto>)entry.Value
                    .Where(r => byId.ContainsKey(r.MemoryId))
                    .Select(r => new RelatedMemoryDto(
                        r.MemoryId, byId[r.MemoryId].Text, r.RelationType, r.Hops, byId[r.MemoryId].IsActive, r.Direction, r.Note))
                    .ToList()))
            .Where(x => x.Related.Count > 0)
            .ToDictionary(x => x.Key, x => x.Related);
    }

    public async Task<SpaceGraphDto> GetSpaceGraphAsync(Guid spaceId, int maxNodes = 50, CancellationToken cancellationToken = default)
    {
        var (items, _) = await memoryRepository.ListAsync(spaceId, page: 1, limit: maxNodes, cancellationToken);
        var nodeIds = items.Select(m => m.Id).ToHashSet();

        var edges = await memoryEdgeRepository.ListEdgesAsync(spaceId, cancellationToken);
        var visibleEdges = edges.Where(e => nodeIds.Contains(e.FromMemoryId) && nodeIds.Contains(e.ToMemoryId));

        var nodes = items.Select(m => new GraphNodeDto(m.Id, m.Text, m.Category, m.IsActive, m.CreatedAt)).ToList();
        var graphEdges = visibleEdges.Select(e => new GraphEdgeDto(e.FromMemoryId, e.ToMemoryId, e.RelationType, e.Note)).ToList();

        return new SpaceGraphDto(nodes, graphEdges);
    }
}
