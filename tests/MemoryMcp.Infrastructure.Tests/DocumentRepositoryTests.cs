using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DocumentRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Add_then_GetByIdAsync_round_trips_the_document()
    {
        using var db = fixture.CreateDbContext();
        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        db.Spaces.Add(space);
        await db.SaveChangesAsync();

        var repository = new DocumentRepository(db);
        var document = new Document(space.Id, "Title", "note", "raw content", "summary");
        repository.Add(document);
        await db.SaveChangesAsync();

        var fetched = await repository.GetByIdAsync(document.Id);

        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("Title");
        fetched.RawContent.Should().Be("raw content");
    }

    [Fact]
    public async Task ListAsync_paginates_and_reports_total_count()
    {
        using var db = fixture.CreateDbContext();
        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        db.Spaces.Add(space);
        await db.SaveChangesAsync();

        var repository = new DocumentRepository(db);
        repository.Add(new Document(space.Id, "One", "note"));
        repository.Add(new Document(space.Id, "Two", "note"));
        repository.Add(new Document(space.Id, "Three", "note"));
        await db.SaveChangesAsync();

        var (items, totalCount) = await repository.ListAsync(space.Id, page: 2, limit: 2);

        totalCount.Should().Be(3);
        items.Should().ContainSingle();
    }
}
