using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class MemoryEdgeRepository(MemoryDbContext dbContext) : IMemoryEdgeRepository
{
    public void Add(MemoryEdge edge) => dbContext.MemoryEdges.Add(edge);

    public async Task<IReadOnlyList<MemoryEdge>> ListEdgesAsync(Guid spaceId, CancellationToken cancellationToken = default) =>
        await dbContext.MemoryEdges.AsNoTracking().Where(e => e.SpaceId == spaceId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RelatedMemory>> GetRelatedAsync(
        Guid rootMemoryId, int maxHops, CancellationToken cancellationToken = default)
    {
        // No graph extension available in this environment (see VectorSettings for the analogous
        // pgvector constraint), so traversal is a plain Postgres recursive CTE, parameterized via EF
        // Core's Database.SqlQuery<T> (no string concatenation). The visited-node "path" array bounds
        // the recursion and guarantees termination on cycles. Run once per direction: edges only ever
        // point from the newer fact to the older memory it relates to, but callers land on either end.
        var outgoing = await TraverseOutgoingAsync(rootMemoryId, maxHops, cancellationToken);
        var incoming = await TraverseIncomingAsync(rootMemoryId, maxHops, cancellationToken);

        return outgoing.Select(r => new RelatedMemory(r.ToId, (RelationType)r.RelationType, r.Hops, RelatedMemoryDirection.Outgoing))
            .Concat(incoming.Select(r => new RelatedMemory(r.ToId, (RelationType)r.RelationType, r.Hops, RelatedMemoryDirection.Incoming)))
            .ToList();
    }

    private Task<List<GraphRow>> TraverseOutgoingAsync(Guid rootMemoryId, int maxHops, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<GraphRow>(
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

    // Mirror of TraverseOutgoingAsync with From/To swapped, so a memory that only has edges pointing
    // *at* it (the common case: it's the older side of an Updates/Extends/Derives relation) still
    // surfaces those relations when it's the one a search lands on.
    private Task<List<GraphRow>> TraverseIncomingAsync(Guid rootMemoryId, int maxHops, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<GraphRow>(
            $"""
            WITH RECURSIVE graph(to_id, relation_type, hops, path) AS (
                SELECT "FromMemoryId", "RelationType", 1, ARRAY["ToMemoryId", "FromMemoryId"]
                FROM memory_edges WHERE "ToMemoryId" = {rootMemoryId}
                UNION ALL
                SELECT e."FromMemoryId", e."RelationType", g.hops + 1, g.path || e."FromMemoryId"
                FROM memory_edges e
                JOIN graph g ON e."ToMemoryId" = g.to_id
                WHERE g.hops < {maxHops} AND e."FromMemoryId" <> ALL(g.path)
            )
            SELECT to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops"
            FROM graph
            GROUP BY to_id, relation_type
            """).ToListAsync(cancellationToken);

    private sealed record GraphRow(Guid ToId, int RelationType, int Hops);
}
