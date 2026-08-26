using MemoryMcp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MemoryMcp.Api.Health;

/// <summary>
/// Verifies the API can actually reach Postgres. Every MCP tool needs the database, so an instance that
/// can't connect is useless — without this the endpoint reported "healthy" purely because the process was
/// up, and Fly.io would keep a broken machine in rotation (see fly.toml's http_service.checks).
/// </summary>
public sealed class DatabaseHealthCheck(MemoryDbContext dbContext) : IHealthCheck
{
    // Fly.io gives the check 5s before it counts as a failure; bound the probe below that so a hung
    // connection is reported as unhealthy by us rather than timing out the whole HTTP request.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            return await dbContext.Database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot connect to the database.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Database did not respond within {ProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database connection failed.", ex);
        }
    }
}
