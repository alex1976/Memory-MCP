using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class MemoryEdgeRepository(MemoryDbContext dbContext) : IMemoryEdgeRepository
{
    public void Add(MemoryEdge edge) => dbContext.MemoryEdges.Add(edge);

    public async Task<IReadOnlyList<RelatedMemory>> GetRelatedAsync(
        Guid rootMemoryId, int maxHops, CancellationToken cancellationToken = default)
    {
        // No graph extension available in this environment (see VectorSettings for the analogous
        // pgvector constraint), so traversal is a plain Postgres recursive CTE, parameterized via EF
        // Core's Database.SqlQuery<T> (no string concatenation). The visited-node "path" array bounds
        // the recursion and guarantees termination on cycles.
        var rows = await dbContext.Database.SqlQuery<GraphRow>(
            $"""
            WITH RECURSIVE graph(to_id, relation_type, hops, path) AS (
                SELECT "ToMemoryId", "RelationType", 1, ARRAY["FromMemoryId", "ToMemoryId"]
                FROM memory_edges WHERE "FromMemoryId" = {rootMemoryId}
                UNION ALL
                SELECT e."ToMemoryId", e."RelationType", g.hops + 1, g.path || e."ToMemoryId"
                FROM memory_edges e
                JOIN graph g ON e."FromMemoryId" = g.to_id
                WHERE g.hops < {maxHops} AND e."ToMemoryId" <> ALL(g.path)
            )
            SELECT to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops"
            FROM graph
            GROUP BY to_id, relation_type
            """).ToListAsync(cancellationToken);

        return rows.Select(r => new RelatedMemory(r.ToId, (RelationType)r.RelationType, r.Hops)).ToList();
    }

    private sealed record GraphRow(Guid ToId, int RelationType, int Hops);
}
