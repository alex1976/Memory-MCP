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
        McpExecution.RunAsync(() => spaceService.WhoAmIAsync(cancellationToken));

    [McpServerTool(Name = "listSpaces")]
    [Description("Lists the spaces accessible to the current API key, with access level and document/memory counts.")]
    public Task<IReadOnlyList<SpaceSummaryDto>> ListSpaces(CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => spaceService.ListSpacesAsync(cancellationToken));

    [McpServerTool(Name = "setActiveSpace")]
    [Description("Sets which of the current API key's accessible spaces is the active (default) one.")]
    public Task<IReadOnlyList<SpaceSummaryDto>> SetActiveSpace(
        [Description("Key of the space to make active")] string spaceKey, CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => spaceService.SetActiveSpaceAsync(spaceKey, cancellationToken));
}
