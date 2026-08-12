using System.ComponentModel;
using System.Text.Json;
using MemoryMcp.Application.Memories;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Resources;

[McpServerResourceType]
public sealed class MemoryResources(IMemoryService memoryService)
{
    private const int MemoriesPageSize = 10;

    [McpServerResource(UriTemplate = "memory-mcp://profile", Name = "profile", MimeType = "application/json")]
    [Description("Stable and recent profile context (the same recent-active-memories set attached to search_memory) for the active space.")]
    public async Task<string> Profile(CancellationToken cancellationToken = default)
    {
        var profile = await McpExecution.RunAsync(() => memoryService.GetProfileAsync(containerTag: null, cancellationToken));
        return JsonSerializer.Serialize(profile, McpJsonUtilities.DefaultOptions);
    }

    [McpServerResource(UriTemplate = "memory-mcp://memories", Name = "memories", MimeType = "application/json")]
    [Description("The most recently created memories (any status) in the active space — the first page of listMemories.")]
    public async Task<string> Memories(CancellationToken cancellationToken = default)
    {
        var memories = await McpExecution.RunAsync(
            () => memoryService.ListMemoriesAsync(containerTag: null, page: 1, limit: MemoriesPageSize, cancellationToken));
        return JsonSerializer.Serialize(memories, McpJsonUtilities.DefaultOptions);
    }
}
