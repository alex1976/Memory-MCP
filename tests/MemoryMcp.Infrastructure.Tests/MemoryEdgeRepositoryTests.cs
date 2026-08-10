using AwesomeAssertions;
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

        related.Should().ContainSingle(r => r.MemoryId == b.Id);
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
