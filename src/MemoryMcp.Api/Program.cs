using MemoryMcp.Api;
using MemoryMcp.Api.Auth;
using MemoryMcp.Api.Health;
using MemoryMcp.Application;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

if (args.Contains("--stdio"))
{
    await RunStdioAsync(args);
    return;
}

if (args.Contains("--migrate"))
{
    await RunMigrateAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMemoryMcpApplication();
builder.Services.AddMemoryMcpInfrastructure(builder.Configuration);

builder.Services.AddScoped<CurrentAccessContext>();
builder.Services.AddScoped<ICurrentAccessContext>(sp => sp.GetRequiredService<CurrentAccessContext>());

builder.Services
    .AddAuthentication(ApiKeyAuthenticationSchemeOptions.SchemeName)
    .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationSchemeOptions.SchemeName, _ => { });
builder.Services.AddAuthorization();

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

AddMemoryMcpServer(builder.Services, mcp => mcp.WithHttpTransport());

var app = builder.Build();

if (args.Contains("--seed"))
{
    await SeedDevDataAsync(app.Services);
    return;
}

app.UseAuthentication();
app.UseAuthorization();

// Deliberately anonymous: platform probes (fly.toml, docker-compose) have no API key. Keeps the
// original { "status": ... } body so existing probes and README docs stay accurate.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { status = report.Status.ToString().ToLowerInvariant() });
    },
});

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

// Single definition of the server's identity, capabilities, and handler discovery, shared by the HTTP
// and stdio hosts so the two transports can't drift apart (they previously repeated all of this). Only
// the transport differs, supplied by the caller.
static void AddMemoryMcpServer(IServiceCollection services, Func<IMcpServerBuilder, IMcpServerBuilder> withTransport)
{
    var mcp = services.AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "Memory-MCP",
            Version = typeof(McpExecution).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
        };
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
            Resources = new ResourcesCapability(),
        };
    });

    withTransport(mcp)
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly()
        .WithPromptsFromAssembly()
        .WithMcpApps();
}

// Applies pending EF Core migrations and exits, without starting the HTTP host or seeding dev data.
// Meant to run as a platform release step (e.g. Fly.io's release_command) before the new version
// starts serving traffic, since the normal HTTP path never migrates on its own.
static async Task RunMigrateAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddMemoryMcpInfrastructure(builder.Configuration);

    var host = builder.Build();

    using var scope = host.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
    await db.Database.MigrateAsync();
}

// Dev-only helper: creates two spaces and two users — a Writer holding grants on both spaces and a
// Reader holding a grant on one — so the multi-user rules (role ceiling, per-space grants, shared
// reads, attribution) can be exercised manually via MCP Inspector without a full admin API, which
// remains out of scope (TODO T9).
static async Task SeedDevDataAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
    await db.Database.MigrateAsync();

    var personalSpace = new Space("default", "Default Space", "Seeded for local development");
    var teamSpace = new Space("team", "Team Space", "Shared space, seeded for local development");
    db.Spaces.AddRange(personalSpace, teamSpace);

    var writer = new User("writer@memory-mcp.local", "Dev Writer", UserRole.Writer);
    var reader = new User("reader@memory-mcp.local", "Dev Reader", UserRole.Reader);
    db.Users.AddRange(writer, reader);

    // The Reader's grant is deliberately ReadWrite: the role ceiling is what makes them read-only, so
    // seeding it this way exercises the capping instead of hiding it behind a matching grant level.
    var writerKey = AddKey(db, writer, "dev-writer", (personalSpace, AccessLevel.ReadWrite, true), (teamSpace, AccessLevel.ReadWrite, false));
    var readerKey = AddKey(db, reader, "dev-reader", (teamSpace, AccessLevel.ReadWrite, true));

    await db.SaveChangesAsync();

    Console.WriteLine($"Seeded spaces '{personalSpace.Key}' and '{teamSpace.Key}'.");
    Console.WriteLine($"  Writer ({writer.Email}) — spaces {personalSpace.Key}, {teamSpace.Key} — API key: {writerKey}");
    Console.WriteLine($"  Reader ({reader.Email}) — space {teamSpace.Key} (read-only by role) — API key: {readerKey}");

    static string AddKey(
        MemoryDbContext db, User user, string label, params (Space Space, AccessLevel Level, bool IsDefault)[] grants)
    {
        var rawKey = $"mmcp_{Guid.NewGuid():N}";
        var apiKey = new ApiKey(user.Id, ApiKeyHasher.Hash(rawKey), rawKey[..12], label);
        db.ApiKeys.Add(apiKey);

        foreach (var (space, level, isDefault) in grants)
        {
            db.ApiKeySpaceGrants.Add(new ApiKeySpaceGrant(apiKey.Id, space.Id, level, isDefault));
        }

        return rawKey;
    }
}

// Stdio transport: runs as a local subprocess (e.g. launched by Claude Desktop's "command" config)
// instead of the HTTP host. There's no HTTP request to read X-Api-Key from, so the identity is fixed
// for the whole process lifetime, resolved once from MEMORYMCP_API_KEY before the stdio loop starts —
// CurrentAccessContext is registered as a singleton here instead of the HTTP path's per-request scoped
// instance, since IApiKeyRepository/IEmbeddingProvider etc. underneath it don't require a request scope.
static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Stdout is the MCP JSON-RPC transport channel; console logs must never land there or they'd
    // corrupt the protocol stream as seen by the client. Route all console logging to stderr instead.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddMemoryMcpApplication();
    builder.Services.AddMemoryMcpInfrastructure(builder.Configuration);

    builder.Services.AddSingleton<CurrentAccessContext>();
    builder.Services.AddSingleton<ICurrentAccessContext>(sp => sp.GetRequiredService<CurrentAccessContext>());

    AddMemoryMcpServer(builder.Services, mcp => mcp.WithStdioServerTransport());

    var host = builder.Build();

    var rawKey = Environment.GetEnvironmentVariable("MEMORYMCP_API_KEY")
        ?? throw new InvalidOperationException("MEMORYMCP_API_KEY environment variable is required when running with --stdio.");

    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.Database.MigrateAsync();

        var apiKeyRepository = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        var snapshot = await apiKeyRepository.FindActiveAccessByHashAsync(ApiKeyHasher.Hash(rawKey))
            ?? throw new InvalidOperationException("MEMORYMCP_API_KEY is invalid or revoked.");

        host.Services.GetRequiredService<CurrentAccessContext>().Initialize(snapshot);
    }

    await host.RunAsync();
}
