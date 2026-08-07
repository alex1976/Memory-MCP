using System.ComponentModel;
using MemoryMcp.Application.Spaces;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Tools;

[McpServerToolType]
public sealed class AccessTools(ISpaceService spaceService)
{
    [McpServerTool(Name = "whoAmI")]
    [Description("Returns the current API key identity, its accessible spaces, and the active space.")]
    public Task<WhoAmIResult> WhoAmI(CancellationToken cancellationToken = default) =>
        ToolExecution.RunAsync(() => spaceService.WhoAmIAsync(cancellationToken));

    [McpServerTool(Name = "listSpaces")]
    [Description("Lists the spaces accessible to the current API key, with access level and document/memory counts.")]
    public Task<IReadOnlyList<SpaceSummaryDto>> ListSpaces(CancellationToken cancellationToken = default) =>
        ToolExecution.RunAsync(() => spaceService.ListSpacesAsync(cancellationToken));
}
