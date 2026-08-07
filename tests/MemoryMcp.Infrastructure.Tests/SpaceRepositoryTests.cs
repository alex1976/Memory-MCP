using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;
using Memory = MemoryMcp.Domain.Memory;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SpaceRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetCountsAsync_counts_documents_and_only_active_memories()
    {
        using var db = fixture.CreateDbContext();
        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        db.Spaces.Add(space);

        db.Documents.Add(new Document(space.Id, "Doc 1", "note"));
        db.Documents.Add(new Document(space.Id, "Doc 2", "note"));

        var active = new Memory(space.Id, "active", embedding: null);
        var forgotten = new Memory(space.Id, "forgotten", embedding: null);
        forgotten.Forget();
        db.Memories.AddRange(active, forgotten);

        await db.SaveChangesAsync();

        var repository = new SpaceRepository(db);
        var counts = await repository.GetCountsAsync([space.Id]);

        counts.Should().ContainSingle();
        counts[0].DocumentCount.Should().Be(2);
        counts[0].MemoryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetCountsAsync_returns_empty_for_no_space_ids()
    {
        using var db = fixture.CreateDbContext();
        var repository = new SpaceRepository(db);

        var counts = await repository.GetCountsAsync([]);

        counts.Should().BeEmpty();
    }
}
