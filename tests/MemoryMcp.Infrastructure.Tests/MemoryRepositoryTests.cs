using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;
using Memory = MemoryMcp.Domain.Memory;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MemoryRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SearchAsync_orders_by_cosine_similarity_and_is_scoped_to_the_space()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);
        var otherSpaceId = await SeedSpaceAsync(db);

        var close = new Memory(spaceId, "close", TestVectors.Embedding(1f, 0f));
        var far = new Memory(spaceId, "far", TestVectors.Embedding(0f, 1f));
        var otherSpaceMemory = new Memory(otherSpaceId, "other space", TestVectors.Embedding(1f, 0f));

        db.Memories.AddRange(close, far, otherSpaceMemory);
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);
        var results = await repository.SearchAsync(spaceId, TestVectors.Embedding(1f, 0f), topK: 10);

        results.Should().HaveCount(2);
        results[0].Memory.Text.Should().Be("close");
        results[0].Score.Should().BeApproximately(1.0, 0.0001);
        results[1].Memory.Text.Should().Be("far");
    }

    [Fact]
    public async Task SearchAsync_excludes_inactive_memories()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var forgotten = new Memory(spaceId, "forgotten", TestVectors.Embedding(1f, 0f));
        forgotten.Forget();
        db.Memories.Add(forgotten);
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);
        var results = await repository.SearchAsync(spaceId, TestVectors.Embedding(1f, 0f), topK: 10);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_paginates_and_reports_total_count()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        db.Memories.AddRange(
            new Memory(spaceId, "one", embedding: null),
            new Memory(spaceId, "two", embedding: null),
            new Memory(spaceId, "three", embedding: null));
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);
        var (items, totalCount) = await repository.ListAsync(spaceId, page: 1, limit: 2);

        totalCount.Should().Be(3);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListRecentActiveAsync_excludes_forgotten_memories()
    {
        using var db = fixture.CreateDbContext();
        var spaceId = await SeedSpaceAsync(db);

        var active = new Memory(spaceId, "active", embedding: null);
        var forgotten = new Memory(spaceId, "forgotten", embedding: null);
        forgotten.Forget();

        db.Memories.AddRange(active, forgotten);
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);
        var results = await repository.ListRecentActiveAsync(spaceId, take: 10);

        results.Should().ContainSingle(m => m.Text == "active");
    }

    private static async Task<Guid> SeedSpaceAsync(Persistence.MemoryDbContext db)
    {
        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        db.Spaces.Add(space);
        await db.SaveChangesAsync();
        return space.Id;
    }
}
