using MemoryMcp.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class SpaceRepository(MemoryDbContext dbContext) : ISpaceRepository
{
    public async Task<IReadOnlyList<SpaceCounts>> GetCountsAsync(
        IReadOnlyList<Guid> spaceIds, CancellationToken cancellationToken = default)
    {
        if (spaceIds.Count == 0)
        {
            return [];
        }

        var documentCounts = await dbContext.Documents
            .AsNoTracking()
            .Where(d => spaceIds.Contains(d.SpaceId))
            .GroupBy(d => d.SpaceId)
            .Select(g => new { SpaceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SpaceId, x => x.Count, cancellationToken);

        var memoryCounts = await dbContext.Memories
            .AsNoTracking()
            .Where(m => spaceIds.Contains(m.SpaceId) && m.IsActive)
            .GroupBy(m => m.SpaceId)
            .Select(g => new { SpaceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SpaceId, x => x.Count, cancellationToken);

        return spaceIds
            .Select(id => new SpaceCounts(id, documentCounts.GetValueOrDefault(id), memoryCounts.GetValueOrDefault(id)))
            .ToList();
    }
}
