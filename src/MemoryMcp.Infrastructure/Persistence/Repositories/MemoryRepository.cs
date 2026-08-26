using MemoryMcp.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Pgvector;

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
        // Ranking happens in Postgres via pgvector's `<=>` cosine-distance operator, served by the HNSW
        // index on Embedding. This previously pulled every embedding in the space into memory and scored
        // in C#, which at 3072 dimensions meant ~6 KB per row materialized on every search *and* every
        // add_memory (extraction candidates go through the same path).
        //
        // Two steps on purpose: the KNN projects only ids and distances — never the embeddings — and the
        // winning rows are then loaded as tracked entities. That keeps ForgetAsync able to soft-delete
        // through change tracking while confining tracking to topK rows rather than the whole space.
        var queryVector = new HalfVector(Array.ConvertAll(queryEmbedding, f => (Half)f));
        // Normalized so a blank category behaves as "no filter", matching the previous
        // string.IsNullOrWhiteSpace guard rather than filtering on an empty string.
        var categoryFilter = string.IsNullOrWhiteSpace(category) ? null : category;

        var ranked = await dbContext.Database.SqlQuery<RankedRow>(
            $"""
            SELECT "Id" AS "Id", ("Embedding" <=> {queryVector}::halfvec) AS "Distance"
            FROM memories
            WHERE "SpaceId" = {spaceId}
              AND "IsActive"
              AND "Embedding" IS NOT NULL
              AND ({categoryFilter}::text IS NULL OR "Category" = {categoryFilter})
            ORDER BY "Embedding" <=> {queryVector}::halfvec
            LIMIT {topK}
            """).ToListAsync(cancellationToken);

        if (ranked.Count == 0)
        {
            return [];
        }

        var ids = ranked.Select(r => r.Id).ToList();
        var byId = await dbContext.Memories
            .Where(m => ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        // Cosine distance is 1 - cosine similarity; callers (and ForgetSimilarityThreshold) speak similarity.
        return ranked
            .Where(r => byId.ContainsKey(r.Id))
            .Select(r => new MemorySearchHit(byId[r.Id], 1.0 - r.Distance))
            .ToList();
    }

    private sealed record RankedRow(Guid Id, double Distance);

    public async Task<IReadOnlyList<Domain.Memory>> SearchByKeywordAsync(
        Guid spaceId, string keyword, int topK, string? category = null, CancellationToken cancellationToken = default)
    {
        // Combines an exact substring match with pg_trgm word-similarity so typos/near-misses
        // (e.g. "sky" vs "skye") are still found; both are backed by the GIN trigram index on
        // Text (see MemoryConfiguration), and results are ranked by similarity so exact/close
        // matches surface before looser fuzzy ones.
        var query = dbContext.Memories
            .AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.IsActive &&
                (EF.Functions.ILike(m.Text, $"%{keyword}%") || EF.Functions.TrigramsAreWordSimilar(keyword, m.Text)));
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(m => m.Category == category);
        }

        return await query
            .OrderByDescending(m => EF.Functions.TrigramsWordSimilarity(keyword, m.Text))
            .ThenByDescending(m => m.CreatedAt)
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
