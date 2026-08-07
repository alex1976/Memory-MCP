using System.ComponentModel;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Memories;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Tools;

[McpServerToolType]
public sealed class MemoryTools(IMemoryService memoryService)
{
    [McpServerTool(Name = "search_memory")]
    [Description("Semantic search over stored memories in a space, optionally including stable/recent profile context.")]
    public Task<SearchMemoryResult> SearchMemory(
        [Description("Natural language search query.")] string query,
        [Description("Include stable/recent profile context alongside matches. Defaults to true.")] bool includeProfile = true,
        [Description("Space key; defaults to the API key's active space.")] string? containerTag = null,
        CancellationToken cancellationToken = default) =>
        ToolExecution.RunAsync(() => memoryService.SearchMemoryAsync(query, includeProfile, containerTag, cancellationToken));

    [McpServerTool(Name = "add_memory")]
    [Description("Saves new information as a memory ('save', default), or forgets a previously saved memory matching the given content ('forget').")]
    public Task<AddMemoryResult> AddMemory(
        [Description("The information to save, or to match against when forgetting.")] string content,
        [Description("'save' (default) or 'forget'.")] string action = "save",
        [Description("Space key; defaults to the API key's active space.")] string? containerTag = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MemoryAction>(action, ignoreCase: true, out var parsedAction))
        {
            throw new McpException($"Unsupported action '{action}'. Use 'save' or 'forget'.");
        }

        return ToolExecution.RunAsync(() => memoryService.AddMemoryAsync(content, parsedAction, containerTag, cancellationToken));
    }

    [McpServerTool(Name = "listMemories")]
    [Description("Lists extracted memory entries in a space, paginated.")]
    public Task<PagedResult<MemorySummaryDto>> ListMemories(
        [Description("Page number, 1-based. Defaults to 1.")] int page = 1,
        [Description("Items per page, max 50. Defaults to 10.")] int limit = 10,
        [Description("Space key; defaults to the API key's active space.")] string? containerTag = null,
        CancellationToken cancellationToken = default) =>
        ToolExecution.RunAsync(() => memoryService.ListMemoriesAsync(containerTag, page, limit, cancellationToken));
}
