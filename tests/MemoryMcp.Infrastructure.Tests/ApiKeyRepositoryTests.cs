using AwesomeAssertions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using MemoryMcp.Infrastructure.Persistence.Repositories;

namespace MemoryMcp.Infrastructure.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiKeyRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_snapshot_with_user_and_joined_grants()
    {
        using var db = fixture.CreateDbContext();

        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        var user = NewUser(UserRole.Writer, "Ada Lovelace");
        var apiKey = NewKey(user, label: "laptop");
        var grant = new ApiKeySpaceGrant(apiKey.Id, space.Id, AccessLevel.ReadWrite, isDefault: true);

        db.Spaces.Add(space);
        db.Users.Add(user);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(grant);
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        snapshot.Should().NotBeNull();
        snapshot!.ApiKeyId.Should().Be(apiKey.Id);
        snapshot.Label.Should().Be("laptop");
        snapshot.User.Id.Should().Be(user.Id);
        snapshot.User.Email.Should().Be(user.Email);
        snapshot.User.DisplayName.Should().Be("Ada Lovelace");
        snapshot.User.Role.Should().Be(UserRole.Writer);
        snapshot.Grants.Should().ContainSingle(g =>
            g.SpaceKey == space.Key && g.IsDefault && g.AccessLevel == AccessLevel.ReadWrite);
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_caps_a_readers_grant_to_read_even_when_granted_read_write()
    {
        using var db = fixture.CreateDbContext();

        var space = new Space($"space-{Guid.NewGuid():N}", "Test Space");
        var reader = NewUser(UserRole.Reader, "Read Only");
        var apiKey = NewKey(reader);

        db.Spaces.Add(space);
        db.Users.Add(reader);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, space.Id, AccessLevel.ReadWrite, isDefault: true));
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        // The role is the ceiling: a Reader cannot be granted write access by a grant row.
        snapshot!.Grants.Should().ContainSingle(g => g.AccessLevel == AccessLevel.Read);
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_keeps_a_read_grant_read_for_a_writer()
    {
        using var db = fixture.CreateDbContext();

        var readable = new Space($"space-{Guid.NewGuid():N}", "Readable Space");
        var writable = new Space($"space-{Guid.NewGuid():N}", "Writable Space");
        var writer = NewUser(UserRole.Writer, "Multi Space");
        var apiKey = NewKey(writer);

        db.Spaces.AddRange(readable, writable);
        db.Users.Add(writer);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, readable.Id, AccessLevel.Read, isDefault: false));
        db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, writable.Id, AccessLevel.ReadWrite, isDefault: true));
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        // Capping is a floor operation, not an override: one key spans N spaces, and a Writer still only
        // reads where the grant says Read.
        snapshot!.Grants.Should().HaveCount(2);
        snapshot.Grants.Should().ContainSingle(g => g.SpaceKey == readable.Key && g.AccessLevel == AccessLevel.Read);
        snapshot.Grants.Should().ContainSingle(g => g.SpaceKey == writable.Key && g.AccessLevel == AccessLevel.ReadWrite);
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_null_when_the_owning_user_is_deactivated()
    {
        using var db = fixture.CreateDbContext();

        var user = NewUser(UserRole.Writer, "Departed");
        var apiKey = NewKey(user);
        user.Deactivate();

        db.Users.Add(user);
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync();

        var repository = new ApiKeyRepository(db);
        var snapshot = await repository.FindActiveAccessByHashAsync(apiKey.KeyHash);

        // Offboarding one user must invalidate every credential they hold, without hunting for keys.
        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task FindActiveAccessByHashAsync_returns_null_for_revoked_key()
    {
        using var db = fixture.CreateDbContext();

        var user = NewUser(UserRole.Writer, "Key Rotator");
        var apiKey = NewKey(user);
        apiKey.Revoke();

        db.Users.Add(user);
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

    private static User NewUser(UserRole role, string displayName) =>
        new($"user-{Guid.NewGuid():N}@test.local", displayName, role);

    private static ApiKey NewKey(User user, string? label = null) =>
        new(user.Id, $"hash-{Guid.NewGuid()}", "prefix12345", label);
}
