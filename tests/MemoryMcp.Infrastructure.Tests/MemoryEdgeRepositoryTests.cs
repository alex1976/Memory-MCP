using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;
using Memory = MemoryMcp.Domain.Memory;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MemoryEdgeRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetRelatedAsync_returns_direct_and_multi_hop_related_memories_within_max_hops()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var a = new Memory(spaceId, "a", embedding: null);
        var b = new Memory(spaceId, "b", embedding: null);
        var c = new Memory(spaceId, "c", embedding: null);
        db.Memories.AddRange(a, b, c);
        await db.SaveChangesAsync();

        db.MemoryEdges.AddRange(
            new MemoryEdge(spaceId, a.Id, b.Id, RelationType.Updates),
            new MemoryEdge(spaceId, b.Id, c.Id, RelationType.Extends));
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(a.Id, maxHops: 2);

        related.Should().HaveCount(2);
        related.Should().ContainSingle(r => r.MemoryId == b.Id && r.RelationType == RelationType.Updates && r.Hops == 1);
        related.Should().ContainSingle(r => r.MemoryId == c.Id && r.RelationType == RelationType.Extends && r.Hops == 2);
    }

    [Fact]
    public async Task GetRelatedAsync_respects_the_max_hops_bound()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var a = new Memory(spaceId, "a", embedding: null);
        var b = new Memory(spaceId, "b", embedding: null);
        var c = new Memory(spaceId, "c", embedding: null);
        db.Memories.AddRange(a, b, c);
        await db.SaveChangesAsync();

        db.MemoryEdges.AddRange(
            new MemoryEdge(spaceId, a.Id, b.Id, RelationType.Extends),
            new MemoryEdge(spaceId, b.Id, c.Id, RelationType.Extends));
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(a.Id, maxHops: 1);

        related.Should().ContainSingle(r => r.MemoryId == b.Id);
    }

    [Fact]
    public async Task GetRelatedAsync_terminates_on_cycles()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var a = new Memory(spaceId, "a", embedding: null);
        var b = new Memory(spaceId, "b", embedding: null);
        db.Memories.AddRange(a, b);
        await db.SaveChangesAsync();

        db.MemoryEdges.AddRange(
            new MemoryEdge(spaceId, a.Id, b.Id, RelationType.Extends),
            new MemoryEdge(spaceId, b.Id, a.Id, RelationType.Extends));
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(a.Id, maxHops: 5);

        // a->b and b->a are two distinct stored edges, so both a genuinely outgoing and a genuinely
        // incoming relation to b exist; the point of this test is that traversal still terminates
        // (doesn't hang/stack-overflow) rather than that the two directions collapse into one.
        related.Should().HaveCount(2);
        related.Should().ContainSingle(r => r.MemoryId == b.Id && r.Direction == RelatedMemoryDirection.Outgoing);
        related.Should().ContainSingle(r => r.MemoryId == b.Id && r.Direction == RelatedMemoryDirection.Incoming);
    }

    [Fact]
    public async Task GetRelatedAsync_surfaces_relations_when_the_root_is_the_older_side_of_the_edge()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        // Mirrors how MemoryService actually creates edges: from the new fact to the older memory it
        // relates to. A search that lands on the older memory (b) must still surface the newer one (a).
        var a = new Memory(spaceId, "a", embedding: null);
        var b = new Memory(spaceId, "b", embedding: null);
        db.Memories.AddRange(a, b);
        await db.SaveChangesAsync();

        db.MemoryEdges.Add(new MemoryEdge(spaceId, a.Id, b.Id, RelationType.Updates));
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(b.Id, maxHops: 2);

        related.Should().ContainSingle(r =>
            r.MemoryId == a.Id && r.RelationType == RelationType.Updates && r.Hops == 1 && r.Direction == RelatedMemoryDirection.Incoming);
    }

    [Fact]
    public async Task GetRelatedAsync_carries_the_edge_note_for_direct_relations_only()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var a = new Memory(spaceId, "a", embedding: null);
        var b = new Memory(spaceId, "b", embedding: null);
        var c = new Memory(spaceId, "c", embedding: null);
        db.Memories.AddRange(a, b, c);
        await db.SaveChangesAsync();

        db.MemoryEdges.AddRange(
            new MemoryEdge(spaceId, a.Id, b.Id, RelationType.Updates, "b said Stripe, a says otherwise"),
            new MemoryEdge(spaceId, b.Id, c.Id, RelationType.Extends, "adds the team size to c"));
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(a.Id, maxHops: 2);

        // A note explains a single edge. At two hops the result is a chain collapsed to its shortest
        // path, so there's no one edge to attribute it to and the note is deliberately dropped.
        related.Should().ContainSingle(r => r.MemoryId == b.Id && r.Hops == 1 && r.Note == "b said Stripe, a says otherwise");
        related.Should().ContainSingle(r => r.MemoryId == c.Id && r.Hops == 2 && r.Note == null);
    }

    [Fact]
    public async Task GetRelatedAsync_returns_empty_when_the_memory_has_no_outgoing_edges()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var lonely = new Memory(spaceId, "lonely", embedding: null);
        db.Memories.Add(lonely);
        await db.SaveChangesAsync();

        var repository = new MemoryEdgeRepository(db);
        var related = await repository.GetRelatedAsync(lonely.Id, maxHops: 2);

        related.Should().BeEmpty();
    }

    private static async Task<Guid> SeedSpaceAsync(Persistence.MemoryDbContext db)
    {
        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        db.Spaces.Add(space);
        await db.SaveChangesAsync();
        return space.Id;
    }
}
