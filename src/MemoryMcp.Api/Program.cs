using MemoryMcp.Api.Auth;
using MemoryMcp.Application;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;

if (args.Contains("--stdio"))
{
    await RunStdioAsync(args);
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

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "Memory-MCP",
            Version = "1.0.0",
        };
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability(),
            Resources = new ResourcesCapability(),
        };
    })
    .WithHttpTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly()
    .WithMcpApps();

var app = builder.Build();

if (args.Contains("--seed"))
{
    await SeedDevDataAsync(app.Services);
    return;
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

// Dev-only helper: creates a Space + ReadWrite API key so the MCP tools can be exercised
// manually (e.g. via MCP Inspector) without a full admin API, which is out of scope for Phase 1.
static async Task SeedDevDataAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
    await db.Database.MigrateAsync();

    var space = new Space("default", "Default Space", "Seeded for local development");

    var rawKey = $"mmcp_{Guid.NewGuid():N}";
    var apiKey = new ApiKey(ApiKeyHasher.Hash(rawKey), rawKey[..12], label: "dev-seed");
    var grant = new ApiKeySpaceGrant(apiKey.Id, space.Id, AccessLevel.ReadWrite, isDefault: true);

    db.Spaces.Add(space);
    db.ApiKeys.Add(apiKey);
    db.ApiKeySpaceGrants.Add(grant);
    await db.SaveChangesAsync();

    Console.WriteLine($"Seeded space '{space.Key}' with API key: {rawKey}");
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

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "Memory-MCP",
                Version = "1.0.0",
            };
            options.Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Resources = new ResourcesCapability(),
            };
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .WithResourcesFromAssembly()
        .WithPromptsFromAssembly()
        .WithMcpApps();

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
