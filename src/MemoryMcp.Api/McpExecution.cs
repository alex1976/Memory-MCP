using MemoryMcp.Application.Abstractions;
using ModelContextProtocol;

namespace MemoryMcp.Api;

/// <summary>
/// Translates known Application-level failures into <see cref="McpException"/>, whose message
/// is surfaced verbatim to the MCP client; any other exception is left to the SDK's default
/// handling, which hides internal details behind a generic error. Shared by tools (where it
/// becomes an isError=true tool result), resources, and prompts alike.
/// </summary>
internal static class McpExecution
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception ex) when (ex is SpaceNotFoundException or AccessDeniedException or EntityNotFoundException
            or DocumentExtractionException or ValidationException)
        {
            throw new McpException(ex.Message);
        }
    }
}
