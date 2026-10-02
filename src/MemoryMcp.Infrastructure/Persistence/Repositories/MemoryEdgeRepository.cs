using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class MemoryEdgeRepository(MemoryDbContext dbContext) : IMemoryEdgeRepository
{
    public void Add(MemoryEdge edge) => dbContext.MemoryEdges.Add(edge);

    public async Task<IReadOnlyList<MemoryEdge>> ListEdgesAsync(Guid spaceId, CancellationToken cancellationToken = default) =>
        await dbContext.MemoryEdges.AsNoTracking().Where(e => e.SpaceId == spaceId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RelatedMemory>>> GetRelatedAsync(
        IReadOnlyList<Guid> rootMemoryIds, int maxHops, CancellationToken cancellationToken = default)
    {
        if (rootMemoryIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<RelatedMemory>>();
        }

        // No graph extension available in this environment (see VectorSettings for the analogous
        // pgvector constraint), so traversal is a plain Postgres recursive CTE, parameterized via EF
        // Core's Database.SqlQuery<T> (no string concatenation). The visited-node "path" array bounds
        // the recursion and guarantees termination on cycles. Run once per direction: edges only ever
        // point from the newer fact to the older memory it relates to, but callers land on either end.
        // Every root is seeded into the same CTE and carried through as root_id, so N roots cost two
        // round trips rather than two each.
        var roots = rootMemoryIds.Distinct().ToArray();
        var outgoing = await TraverseOutgoingAsync(roots, maxHops, cancellationToken);
        var incoming = await TraverseIncomingAsync(roots, maxHops, cancellationToken);

        return outgoing.Select(r => (r.RootId, Related: new RelatedMemory(r.ToId, (RelationType)r.RelationType, r.Hops, RelatedMemoryDirection.Outgoing, r.Note)))
            .Concat(incoming.Select(r => (r.RootId, Related: new RelatedMemory(r.ToId, (RelationType)r.RelationType, r.Hops, RelatedMemoryDirection.Incoming, r.Note))))
            .GroupBy(x => x.RootId, x => x.Related)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<RelatedMemory>)g.ToList());
    }

    private Task<List<GraphRow>> TraverseOutgoingAsync(Guid[] rootMemoryIds, int maxHops, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<GraphRow>(
            $"""
            WITH RECURSIVE graph(root_id, to_id, relation_type, note, hops, path) AS (
                SELECT "FromMemoryId", "ToMemoryId", "RelationType", "Note", 1, ARRAY["FromMemoryId", "ToMemoryId"]
                FROM memory_edges WHERE "FromMemoryId" = ANY({rootMemoryIds})
                UNION ALL
                SELECT g.root_id, e."ToMemoryId", e."RelationType", e."Note", g.hops + 1, g.path || e."ToMemoryId"
                FROM memory_edges e
                JOIN graph g ON e."FromMemoryId" = g.to_id
                WHERE g.hops < {maxHops} AND e."ToMemoryId" <> ALL(g.path)
            )
            SELECT root_id AS "RootId", to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops",
                   MIN(note) FILTER (WHERE hops = 1) AS "Note"
            FROM graph
            GROUP BY root_id, to_id, relation_type
            """).ToListAsync(cancellationToken);

    // Mirror of TraverseOutgoingAsync with From/To swapped, so a memory that only has edges pointing
    // *at* it (the common case: it's the older side of an Updates/Extends/Derives relation) still
    // surfaces those relations when it's the one a search lands on.
    private Task<List<GraphRow>> TraverseIncomingAsync(Guid[] rootMemoryIds, int maxHops, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQuery<GraphRow>(
            $"""
            WITH RECURSIVE graph(root_id, to_id, relation_type, note, hops, path) AS (
                SELECT "ToMemoryId", "FromMemoryId", "RelationType", "Note", 1, ARRAY["ToMemoryId", "FromMemoryId"]
                FROM memory_edges WHERE "ToMemoryId" = ANY({rootMemoryIds})
                UNION ALL
                SELECT g.root_id, e."FromMemoryId", e."RelationType", e."Note", g.hops + 1, g.path || e."FromMemoryId"
                FROM memory_edges e
                JOIN graph g ON e."ToMemoryId" = g.to_id
                WHERE g.hops < {maxHops} AND e."FromMemoryId" <> ALL(g.path)
            )
            SELECT root_id AS "RootId", to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops",
                   MIN(note) FILTER (WHERE hops = 1) AS "Note"
            FROM graph
            GROUP BY root_id, to_id, relation_type
            """).ToListAsync(cancellationToken);

    // Note is aggregated as MIN(note) FILTER (WHERE hops = 1): the grouping key is (root, node, relation
    // type), so a note can only be attributed unambiguously to a direct edge — see RelatedMemory's remarks.
    private sealed record GraphRow(Guid RootId, Guid ToId, int RelationType, int Hops, string? Note);
}
