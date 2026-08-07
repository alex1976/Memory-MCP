using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Spaces;

public sealed class SpaceService(
    ISpaceRepository spaceRepository,
    ICurrentAccessContext accessContext) : ISpaceService
{
    public async Task<IReadOnlyList<SpaceSummaryDto>> ListSpacesAsync(CancellationToken cancellationToken = default)
    {
        var grants = accessContext.Grants;
        var counts = await spaceRepository.GetCountsAsync(grants.Select(g => g.SpaceId).ToList(), cancellationToken);
        var countsBySpace = counts.ToDictionary(c => c.SpaceId);

        return grants
            .Select(g =>
            {
                countsBySpace.TryGetValue(g.SpaceId, out var c);
                return new SpaceSummaryDto(g.SpaceId, g.SpaceKey, g.SpaceName, g.AccessLevel.ToString(), g.IsDefault, c?.DocumentCount ?? 0, c?.MemoryCount ?? 0);
            })
            .ToList();
    }

    public async Task<WhoAmIResult> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var spaces = await ListSpacesAsync(cancellationToken);
        return new WhoAmIResult(accessContext.ApiKeyId, accessContext.OwnerLabel, accessContext.ActiveGrant?.SpaceKey, spaces);
    }
}
