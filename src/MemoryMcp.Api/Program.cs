using MemoryMcp.Api.Auth;
using MemoryMcp.Application;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using MemoryMcp.Infrastructure;
using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
    })
    .WithHttpTransport()
    .WithToolsFromAssembly();

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
