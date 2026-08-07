using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence.Repositories;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiKeyRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_snapshot_with_joined_grants()
    {
        using var db = fixture.CreateDbContext();

        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        var apiKey = new ApiKey($"hash-{Guid.NewGuid()}", "prefix12345", label: "test-key");
        var grant = new ApiKeySpaceGrant(apiKey.Id, space.Id, AccessLevel.ReadWrite, isDefault: true);

        db.Spaces.Add(space);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(grant);
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        snapshot.Should().NotBeNull();
        snapshot!.ApiKeyId.Should().Be(apiKey.Id);
        snapshot.Label.Should().Be("test-key");
        snapshot.Grants.Should().ContainSingle(g =>
            g.SpaceKey == space.Key && g.IsDefault && g.AccessLevel == AccessLevel.ReadWrite);
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_null_for_revoked_key()
    {
        using var db = fixture.CreateDbContext();

        var apiKey = new ApiKey($"hash-{Guid.NewGuid()}", "prefix67890");
        apiKey.Revoke();
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_null_for_unknown_hash()
    {
        using var db = fixture.CreateDbContext();
        var repository = new ApiKeyRepository(db);

        var snapshot = await repository.FindActiveAccessByHashAsync("unknown-hash-value");

        snapshot.Should().BeNull();
    }
}
