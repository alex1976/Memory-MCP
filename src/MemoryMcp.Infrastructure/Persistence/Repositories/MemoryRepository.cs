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
        Guid spaceId, float[] queryEmbedding, int topK, string? category = null, CancellationToken cancellationToken = default)
    {
        // No pgvector extension available in this environment, so candidates are pulled into memory
        // and scored here. Tracked on purpose: MemoryService.ForgetAsync mutates the returned entities
        // and relies on EF change tracking to persist the soft-delete without a redundant round trip.
        var query = dbContext.Memories.Where(m => m.SpaceId == spaceId && m.IsActive && m.Embedding != null);
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(m => m.Category == category);
        }

        var candidates = await query.ToListAsync(cancellationToken);

        return candidates
            .Select(m => new MemorySearchHit(m, CosineSimilarity(m.Embedding!, queryEmbedding)))
            .OrderByDescending(hit => hit.Score)
            .Take(topK)
            .ToList();
    }

    public async Task<IReadOnlyList<Domain.Memory>> SearchByKeywordAsync(
        Guid spaceId, string keyword, int topK, string? category = null, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Memories
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.IsActive && EF.Functions.ILike(m.Text, $"%{keyword}%"));
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(m => m.Category == category);
        }

        return await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(topK)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Domain.Memory>> ListByCategoryAsync(
        Guid spaceId, string category, int take, CancellationToken cancellationToken = default)
    {
        return await dbContext.Memories
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.IsActive && m.Category == category)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
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

    public async Task<IReadOnlyList<Domain.Memory>> GetByIdsAsync(
        Guid spaceId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        return await dbContext.Memories
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && ids.Contains(m.Id))
            .ToListAsync(cancellationToken);
    }
}
