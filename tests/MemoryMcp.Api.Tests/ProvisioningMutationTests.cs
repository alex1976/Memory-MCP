using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace MemoryMcp.Api.Tests;

/// <summary>
/// The provisioning mutations — revoke a key, deactivate/reactivate a user — checked against what
/// actually matters: whether the credential still authenticates afterwards.
/// </summary>
[Collection(McpApiCollection.Name)]
public sealed class ProvisioningMutationTests(McpApiFactory factory)
{
    private sealed record Person(string Email, string FirstKey, Guid FirstKeyId, string SecondKey);

    private async Task<Person> SeedPersonAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var user = new User($"mut-{Guid.NewGuid():N}@e2e.test", "Mutation Test", UserRole.Writer);
        db.Users.Add(user);

        var first = $"mmcp_{Guid.NewGuid():N}";
        var second = $"mmcp_{Guid.NewGuid():N}";
        var firstKey = new ApiKey(user.Id, ApiKeyHasher.Hash(first), first[..12], "laptop");
        db.ApiKeys.AddRange(firstKey, new ApiKey(user.Id, ApiKeyHasher.Hash(second), second[..12], "ci"));
        await db.SaveChangesAsync();

        return new Person(user.Email, first, firstKey.Id, second);
    }

    private async Task<bool> AuthenticatesAsync(string rawKey)
    {
        using var scope = factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        return await repository.FindActiveAccessByHashAsync(ApiKeyHasher.Hash(rawKey)) is not null;
    }

    private async Task<int> RunAsync(params string[] args) =>
        await ProvisioningCommands.RunAsync(ProvisioningCommands.FindVerb(args)!, args, factory.Services);

    [Fact]
    public async Task Revoking_a_key_stops_only_that_key()
    {
        var person = await SeedPersonAsync();

        var exit = await RunAsync("--revoke-key", "--key", person.FirstKeyId.ToString());

        exit.Should().Be(0);
        (await AuthenticatesAsync(person.FirstKey)).Should().BeFalse();
        (await AuthenticatesAsync(person.SecondKey)).Should().BeTrue("revoking one credential must not take the person's other keys down");
    }

    [Fact]
    public async Task A_key_can_be_revoked_by_its_printed_prefix_and_revoking_twice_is_harmless()
    {
        var person = await SeedPersonAsync();

        (await RunAsync("--revoke-key", "--key", person.FirstKey[..12])).Should().Be(0);
        (await RunAsync("--revoke-key", "--key", person.FirstKey[..12])).Should().Be(0);

        (await AuthenticatesAsync(person.FirstKey)).Should().BeFalse();
    }

    [Fact]
    public async Task Revoking_an_unknown_key_fails()
    {
        (await RunAsync("--revoke-key", "--key", Guid.NewGuid().ToString())).Should().Be(1);
        (await RunAsync("--revoke-key", "--key", "mmcp_nosuchkey")).Should().Be(1);
    }

    [Fact]
    public async Task A_prefix_matching_several_keys_is_refused_and_revokes_nothing()
    {
        var person = await SeedPersonAsync();

        // "mmcp_" is shared by every key, so this is ambiguous by construction.
        (await RunAsync("--revoke-key", "--key", "mmcp_")).Should().Be(1);

        (await AuthenticatesAsync(person.FirstKey)).Should().BeTrue();
        (await AuthenticatesAsync(person.SecondKey)).Should().BeTrue();
    }

    [Fact]
    public async Task Deactivating_a_user_stops_every_key_and_reactivating_restores_the_unrevoked_ones()
    {
        var person = await SeedPersonAsync();
        await RunAsync("--revoke-key", "--key", person.FirstKeyId.ToString());

        (await RunAsync("--deactivate-user", "--email", person.Email.ToUpperInvariant())).Should().Be(0);
        (await AuthenticatesAsync(person.FirstKey)).Should().BeFalse();
        (await AuthenticatesAsync(person.SecondKey)).Should().BeFalse();

        (await RunAsync("--activate-user", "--email", person.Email)).Should().Be(0);
        (await AuthenticatesAsync(person.SecondKey)).Should().BeTrue();
        (await AuthenticatesAsync(person.FirstKey)).Should().BeFalse("a key revoked on its own stays revoked across reactivation");
    }

    [Fact]
    public async Task Deactivating_an_unknown_user_fails()
    {
        (await RunAsync("--deactivate-user", "--email", "nobody@e2e.test")).Should().Be(1);
    }
}
