using MemoryMcp.Application.Abstractions;
using ModelContextProtocol;

namespace MemoryMcp.Api.Tools;

/// <summary>
/// Translates known Application-level failures into <see cref="McpException"/>, whose message
/// is surfaced verbatim to the MCP client (isError=true); any other exception is left to the
/// SDK's default handling, which hides internal details behind a generic error.
/// </summary>
internal static class ToolExecution
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is SpaceNotFoundException or AccessDeniedException or EntityNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }
}
