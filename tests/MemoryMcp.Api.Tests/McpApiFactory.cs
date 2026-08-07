using MemoryMcp.Api.Tests.TestSupport;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MemoryMcp.Api.Tests;

/// <summary>
/// Docker is unavailable in this environment (company policy), so this factory points the app at a
/// real, directly-configured Postgres instance (the same one used for local dev) instead of a
/// Testcontainers-managed one. The seeded space uses a random key so repeated runs against the
/// shared database never collide on the unique space key constraint.
/// </summary>
public sealed class McpApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("MEMORYMCP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException(
            "Set the MEMORYMCP_TEST_CONNECTION_STRING environment variable to a reachable Postgres instance to run these tests.");

    public string SpaceKey { get; } = $"e2e-{Guid.NewGuid():N}";
    public string RawApiKey { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.Database.MigrateAsync();

        var space = new Space(SpaceKey, "E2E Test Space");
        RawApiKey = $"mmcp_{Guid.NewGuid():N}";
        var apiKey = new ApiKey(ApiKeyHasher.Hash(RawApiKey), RawApiKey[..12]);
        var grant = new ApiKeySpaceGrant(apiKey.Id, space.Id, AccessLevel.ReadWrite, isDefault: true);

        db.Spaces.Add(space);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(grant);
        await db.SaveChangesAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEmbeddingProvider>();
            services.AddScoped<IEmbeddingProvider, FakeEmbeddingProvider>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}
