using MemoryMcp.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class MemoryRepository(MemoryDbContext dbContext) : IMemoryRepository
{
    public void Add(Domain.Memory memory) => dbContext.Memories.Add(memory);

    public async Task<(IReadOnlyList<Domain.Memory> Items, int TotalCount)> ListAsync(
        Guid spaceId, int page, int limit, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Memories.AsNoTracking().Where(m => m.SpaceId == spaceId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(
        Guid spaceId, float[] queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        // No pgvector extension available in this environment, so candidates are pulled into memory
        // and scored here. Tracked on purpose: MemoryService.ForgetAsync mutates the returned entities
        // and relies on EF change tracking to persist the soft-delete without a redundant round trip.
        var candidates = await dbContext.Memories
            .Where(m => m.SpaceId == spaceId && m.IsActive && m.Embedding != null)
            .ToListAsync(cancellationToken);

        return candidates
            .Select(m => new MemorySearchHit(m, CosineSimilarity(m.Embedding!, queryEmbedding)))
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public async Task<IReadOnlyList<Domain.Memory>> ListRecentActiveAsync(
        Guid spaceId, int take, CancellationToken cancellationToken = default)
    {
        return await dbContext.Memories
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.IsActive)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }
}
