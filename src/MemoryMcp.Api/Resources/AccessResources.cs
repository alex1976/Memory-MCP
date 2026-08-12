using System.ComponentModel;
using System.Text.Json;
using MemoryMcp.Application.Spaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Resources;

[McpServerResourceType]
public sealed class AccessResources(ISpaceService spaceService)
{
    [McpServerResource(UriTemplate = "memory-mcp://spaces", Name = "spaces", MimeType = "application/json")]
    [Description("A compact list of spaces accessible to the current API key, with the active space marked.")]
    public async Task<string> Spaces(CancellationToken cancellationToken = default)
    {
        var spaces = await McpExecution.RunAsync(() => spaceService.ListSpacesAsync(cancellationToken));
        return JsonSerializer.Serialize(spaces, McpJsonUtilities.DefaultOptions);
    }
}
