using System.Net;
using System.Threading.RateLimiting;
using MemoryMcp.Domain;
using Microsoft.AspNetCore.RateLimiting;

namespace MemoryMcp.Api;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per window, per caller. An MCP tool call is several HTTP requests
    /// (initialize, notifications, the call itself), so this is not a tool-calls-per-minute figure.</summary>
    public int PermitLimit { get; set; } = 300;

    public int WindowSeconds { get; set; } = 60;
}

internal static class RateLimiting
{
    public const string McpPolicy = "mcp";

    public static IServiceCollection AddMemoryMcpRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new();
        if (options.PermitLimit < 1 || options.WindowSeconds < 1)
        {
            throw new InvalidOperationException(
                $"{RateLimitOptions.SectionName}:PermitLimit and {RateLimitOptions.SectionName}:WindowSeconds must be at least 1.");
        }

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                return ValueTask.CompletedTask;
            };

            limiter.AddPolicy(McpPolicy, httpContext =>
            {
                if (!options.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                return RateLimitPartition.GetFixedWindowLimiter(PartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    Window = TimeSpan.FromSeconds(options.WindowSeconds),
                    QueueLimit = 0,
                });
            });
        });

        return services;
    }

    // Runs before authentication, so the partition can only be what the caller *presented*. A presented key
    // gets its own budget (hashed, so the raw secret never sits in limiter state) — one noisy agent cannot
    // starve the others. Everything without a key shares a per-address budget. Behind a reverse proxy the
    // address is the proxy's unless forwarded headers are trusted, in which case those callers share one.
    private static string PartitionKey(HttpContext httpContext)
    {
        var request = httpContext.Request;
        string? rawKey = null;

        if (request.Headers.TryGetValue("X-Api-Key", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            rawKey = header.ToString();
        }
        else
        {
            var authorization = request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                rawKey = authorization["Bearer ".Length..].Trim();
            }
        }

        return string.IsNullOrEmpty(rawKey)
            ? $"ip:{httpContext.Connection.RemoteIpAddress ?? IPAddress.None}"
            : $"key:{ApiKeyHasher.Hash(rawKey)}";
    }
}
