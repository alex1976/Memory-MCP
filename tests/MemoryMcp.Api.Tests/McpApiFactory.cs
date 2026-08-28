using MemoryMcp.Api.Tests.TestSupport;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
        builder.ConfigureServices(services =>
        {
            // The connection string is overridden here, at the DI level, rather than through
            // ConfigureAppConfiguration: an in-memory configuration source added there is applied
            // *before* the app's own appsettings.{Environment}.json, so the dev connection string won
            // and every e2e run wrote into the developer's working database instead of the one named by
            // MEMORYMCP_TEST_CONNECTION_STRING. Replacing the already-built registration is independent
            // of configuration source ordering.
            services.RemoveAll<IDbContextOptionsConfiguration<MemoryDbContext>>();
            services.RemoveAll<DbContextOptions<MemoryDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<MemoryDbContext>();
            services.AddDbContext<MemoryDbContext>(options => options.UseMemoryMcpNpgsql(ConnectionString));

            services.RemoveAll<IEmbeddingProvider>();
            services.AddScoped<IEmbeddingProvider, FakeEmbeddingProvider>();

            services.RemoveAll<IFactExtractor>();
            services.AddScoped<IFactExtractor, FakeFactExtractor>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}
