using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetByIdsAsync_resolves_several_authors_in_one_query()
    {
        using var db = fixture.CreateDbContext();

        var alice = NewUser("Alice", UserRole.Writer);
        var bob = NewUser("Bob", UserRole.Reader);
        var unrelated = NewUser("Unrelated", UserRole.Writer);
        db.Users.AddRange(alice, bob, unrelated);
        await db.SaveChangesAsync();

        var repository = new UserRepository(db);
        var summaries = await repository.GetByIdsAsync([alice.Id, bob.Id]);

        summaries.Should().HaveCount(2);
        summaries.Should().ContainSingle(u => u.Id == alice.Id && u.DisplayName == "Alice" && u.Role == UserRole.Writer);
        summaries.Should().ContainSingle(u => u.Id == bob.Id && u.Role == UserRole.Reader);
    }

    [Fact]
    public async Task GetByIdsAsync_still_names_a_deactivated_author()
    {
        using var db = fixture.CreateDbContext();

        var departed = NewUser("Departed", UserRole.Writer);
        departed.Deactivate();
        db.Users.Add(departed);
        await db.SaveChangesAsync();

        var repository = new UserRepository(db);

        // Their credentials no longer authenticate, but what they wrote must not become anonymous.
        var summaries = await repository.GetByIdsAsync([departed.Id]);

        summaries.Should().ContainSingle(u => u.DisplayName == "Departed");
    }

    [Fact]
    public async Task GetByIdsAsync_returns_nothing_for_an_empty_id_set()
    {
        using var db = fixture.CreateDbContext();
        var repository = new UserRepository(db);

        var summaries = await repository.GetByIdsAsync([]);

        summaries.Should().BeEmpty();
    }

    [Fact]
    public async Task FindByEmailAsync_matches_regardless_of_the_casing_supplied()
    {
        using var db = fixture.CreateDbContext();

        var user = NewUser("Ada", UserRole.Writer);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new UserRepository(db);
        var found = await repository.FindByEmailAsync(user.Email.ToUpperInvariant());

        found.Should().NotBeNull();
        found!.Id.Should().Be(user.Id);
    }

    private static User NewUser(string displayName, UserRole role) =>
        new($"{displayName.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local", displayName, role);
}
