using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MemoryMcp.Infrastructure.Tests;

/// <summary>
/// Docker is unavailable in this environment (company policy), so integration tests connect
/// directly to a real Postgres instance instead of a Testcontainers-managed one.
/// </summary>
/// <remarks>
/// The instance is named by the <c>Test</c> connection string of the API's own configuration: the
/// csproj copies <c>appsettings.json</c> and <c>appsettings.Development.json</c> next to the test
/// assembly, so the value lives in one place for both the app and the tests. The Development file
/// is optional because it is gitignored — on a machine without it, only the placeholder from the
/// committed base file is available and these tests cannot connect.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .Build();

    private static string ConnectionString =>
        Configuration.GetConnectionString("Test")
        ?? throw new InvalidOperationException(
            "Set the 'Test' connection string in src/MemoryMcp.Api/appsettings.Development.json to a reachable Postgres instance to run these tests.");

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
