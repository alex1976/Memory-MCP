using System.Diagnostics;
using System.Text.Json;
using MemoryMcp.Application.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api;

/// <summary>
/// One structured log line per tool call: which tool, in which space, for which user, how long it took
/// and how it ended. Answers "who did what" in a shared space. Arguments are deliberately never logged —
/// they carry memory text and document content, which belongs in the database, not in the log stream.
/// </summary>
internal static class ToolCallLogging
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Filter => next => async (context, cancellationToken) =>
    {
        var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger("MemoryMcp.ToolCalls");
        if (logger is null)
        {
            return await next(context, cancellationToken);
        }

        var tool = context.Params?.Name ?? "<unknown>";
        var started = Stopwatch.GetTimestamp();
        var outcome = "ok";

        try
        {
            var result = await next(context, cancellationToken);
            if (result.IsError == true)
            {
                outcome = "tool_error";
            }

            return result;
        }
        catch (Exception ex)
        {
            outcome = ex.GetType().Name;
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var (space, userId, role) = Describe(context);

            logger.LogInformation(
                "Tool {Tool} space={Space} user={UserId} role={Role} outcome={Outcome} elapsedMs={ElapsedMs:F0}",
                tool, space, userId, role, outcome, elapsedMs);
        }
    };

    private static (string? Space, Guid? UserId, string? Role) Describe(RequestContext<CallToolRequestParams> context)
    {
        var access = context.Services?.GetService<ICurrentAccessContext>();
        if (access is null)
        {
            return (null, null, null);
        }

        // The space is the one the call asked for, falling back to the key's active space — the same
        // resolution the services perform — so the log names the space actually touched.
        string? requested = null;
        if (context.Params?.Arguments is { } arguments
            && arguments.TryGetValue("containerTag", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            requested = value.GetString();
        }

        var space = requested ?? access.ActiveGrant?.SpaceKey;

        try
        {
            return (space, access.User.Id, access.User.Role.ToString());
        }
        catch (InvalidOperationException)
        {
            // Not authenticated: the context was never initialised. Log the call without an identity
            // rather than failing the request over its own telemetry.
            return (space, null, null);
        }
    }
}
