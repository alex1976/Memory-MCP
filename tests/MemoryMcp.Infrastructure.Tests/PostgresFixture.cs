using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Tests;

/// <summary>
/// Docker is unavailable in this environment (company policy), so integration tests connect
/// directly to a real Postgres instance instead of a Testcontainers-managed one.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("MEMORYMCP_TEST_CONNECTION_STRING")
        ?? throw new InvalidOperationException(
            "Set the MEMORYMCP_TEST_CONNECTION_STRING environment variable to a reachable Postgres instance to run these tests.");

    public async Task InitializeAsync()
    {
        using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public MemoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseMemoryMcpNpgsql(ConnectionString)
            .Options;

        return new MemoryDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres collection";
}
