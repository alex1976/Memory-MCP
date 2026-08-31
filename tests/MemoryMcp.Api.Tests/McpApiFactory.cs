using System.Net.Http.Headers;
using MemoryMcp.Api.Tests.TestSupport;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;

namespace MemoryMcp.Api.Tests;

/// <summary>
/// Docker is unavailable in this environment (company policy), so this factory points the app at the
/// real Postgres named by the app's own <c>Test</c> connection string, instead of a
/// Testcontainers-managed one. The seeded spaces use random keys so repeated runs against the
/// shared database never collide on the unique space key constraint.
/// </summary>
/// <remarks>
/// Seeds a whole small tenancy rather than a single key, because the multi-user rules are only
/// observable with more than one principal: a Writer and a Reader sharing one space (so a Reader's
/// refusal to write, and their ability to read the Writer's memories, are both testable), plus a third
/// space nobody holds a grant on (so cross-space isolation is testable from the outside).
/// </remarks>
public sealed class McpApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string WriterDisplayName = "E2E Writer";
    public const string ReaderDisplayName = "E2E Reader";

    /// <summary>The shared space: both the Writer's and the Reader's key hold a grant on it.</summary>
    public string SpaceKey { get; } = $"e2e-{Guid.NewGuid():N}";

    /// <summary>A space no seeded key has a grant on, holding one memory inserted directly. Nothing the
    /// API exposes may ever reach it.</summary>
    public string UngrantedSpaceKey { get; } = $"e2e-ungranted-{Guid.NewGuid():N}";

    public const string UngrantedSpaceMemoryText = "This fact lives in a space no test key can reach";

    /// <summary>Writer's credential. Named without a prefix because it is the default identity the
    /// pre-existing end-to-end tests authenticate with.</summary>
    public string RawApiKey { get; private set; } = string.Empty;

    public string ReaderRawApiKey { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.Database.MigrateAsync();

        var space = new Space(SpaceKey, "E2E Test Space");
        var ungrantedSpace = new Space(UngrantedSpaceKey, "E2E Ungranted Space");
        db.Spaces.AddRange(space, ungrantedSpace);

        var writer = new User($"writer-{Guid.NewGuid():N}@e2e.test", WriterDisplayName, UserRole.Writer);
        var reader = new User($"reader-{Guid.NewGuid():N}@e2e.test", ReaderDisplayName, UserRole.Reader);
        db.Users.AddRange(writer, reader);

        RawApiKey = AddKey(db, writer, space.Id, AccessLevel.ReadWrite);

        // ReadWrite on purpose: the Reader must come out read-only because of their *role*, so granting
        // them a lower level here would test nothing.
        ReaderRawApiKey = AddKey(db, reader, space.Id, AccessLevel.ReadWrite);

        db.Memories.Add(new Domain.Memory(
            ungrantedSpace.Id, UngrantedSpaceMemoryText, FakeEmbeddingProvider.EmbeddingFor(UngrantedSpaceMemoryText)));

        await db.SaveChangesAsync();
    }

    /// <summary>Opens an MCP session authenticated as the holder of <paramref name="rawApiKey"/>.</summary>
    public async Task<McpClient> CreateMcpClientAsync(string rawApiKey)
    {
        var httpClient = CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", rawApiKey);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        return await McpClient.CreateAsync(transport);
    }

    private static string AddKey(MemoryDbContext db, User user, Guid spaceId, AccessLevel accessLevel)
    {
        var rawKey = $"mmcp_{Guid.NewGuid():N}";
        var apiKey = new ApiKey(user.Id, ApiKeyHasher.Hash(rawKey), rawKey[..12], label: user.DisplayName);
        db.ApiKeys.Add(apiKey);
        db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, spaceId, accessLevel, isDefault: true));
        return rawKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) =>
        {
            // Read through the host's fully-built configuration, so the app's own appsettings files are
            // the single source of the test database — and appsettings.Development.json's 'Test' value
            // wins over the placeholder in the committed base file, as it does for 'Default'.
            var connectionString = context.Configuration.GetConnectionString("Test")
                ?? throw new InvalidOperationException(
                    "Set the 'Test' connection string in src/MemoryMcp.Api/appsettings.Development.json to a reachable Postgres instance to run these tests.");

            // The connection string is overridden here, at the DI level, rather than through
            // ConfigureAppConfiguration: an in-memory configuration source added there is applied
            // *before* the app's own appsettings.{Environment}.json, so 'Default' would keep winning and
            // every e2e run would write into the developer's working database instead of the test one.
            // Replacing the already-built registration is independent of configuration source ordering.
            services.RemoveAll<IDbContextOptionsConfiguration<MemoryDbContext>>();
            services.RemoveAll<DbContextOptions<MemoryDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<MemoryDbContext>();
            services.AddDbContext<MemoryDbContext>(options => options.UseMemoryMcpNpgsql(connectionString));

            services.RemoveAll<IEmbeddingProvider>();
            services.AddScoped<IEmbeddingProvider, FakeEmbeddingProvider>();

            services.RemoveAll<IFactExtractor>();
            services.AddScoped<IFactExtractor, FakeFactExtractor>();
        });
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}
