using System.ComponentModel;
using MemoryMcp.Application.Spaces;
using ModelContextProtocol.Server;

namespace MemoryMcp.Api.Tools;

[McpServerToolType]
public sealed class AccessTools(ISpaceService spaceService)
{
    [McpServerTool(Name = "whoAmI")]
    [Description("Returns the authenticated user (id, email, display name, and role: Writer or Reader), the API key in use, its accessible spaces with effective access level, and the active space.")]
    public Task<WhoAmIResult> WhoAmI(CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => spaceService.WhoAmIAsync(cancellationToken));

    [McpServerTool(Name = "listSpaces")]
    [Description("Lists the spaces accessible to the current API key, with the effective access level (the space grant capped by the user's role) and document/memory counts.")]
    public Task<IReadOnlyList<SpaceSummaryDto>> ListSpaces(CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => spaceService.ListSpacesAsync(cancellationToken));

    [McpServerTool(Name = "setActiveSpace")]
    [Description("Sets which of the current API key's accessible spaces is the active (default) one.")]
    public Task<IReadOnlyList<SpaceSummaryDto>> SetActiveSpace(
        [Description("Key of the space to make active")] string spaceKey, CancellationToken cancellationToken = default) =>
        McpExecution.RunAsync(() => spaceService.SetActiveSpaceAsync(spaceKey, cancellationToken));
}
