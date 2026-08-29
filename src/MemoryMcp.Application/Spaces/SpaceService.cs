using MemoryMcp.Application.Abstractions;

namespace MemoryMcp.Application.Spaces;

public sealed class SpaceService(
    ISpaceRepository spaceRepository,
    ICurrentAccessContext accessContext,
    IUnitOfWork unitOfWork) : ISpaceService
{
    public async Task<IReadOnlyList<SpaceSummaryDto>> ListSpacesAsync(CancellationToken cancellationToken = default) =>
        await BuildSummariesAsync(accessContext.Grants, cancellationToken);

    public async Task<WhoAmIResult> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        var spaces = await ListSpacesAsync(cancellationToken);
        var user = accessContext.User;
        return new WhoAmIResult(
            accessContext.ApiKeyId,
            accessContext.OwnerLabel,
            user.Id,
            user.Email,
            user.DisplayName,
            user.Role.ToString(),
            accessContext.ActiveGrant?.SpaceKey,
            spaces);
    }

    public async Task<IReadOnlyList<SpaceSummaryDto>> SetActiveSpaceAsync(string spaceKey, CancellationToken cancellationToken = default)
    {
        var target = accessContext.ResolveGrant(spaceKey) ?? throw new SpaceNotFoundException(spaceKey);

        var grantEntities = await spaceRepository.GetGrantsForApiKeyAsync(accessContext.ApiKeyId, cancellationToken);
        foreach (var grant in grantEntities)
        {
            grant.SetAsDefault(grant.SpaceId == target.SpaceId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // accessContext.Grants was populated once at request start, so its IsDefault flags reflect the
        // pre-switch state — override with the just-persisted target instead of re-querying the DB.
        var updatedGrants = accessContext.Grants
            .Select(g => g with { IsDefault = g.SpaceId == target.SpaceId })
            .ToList();

        return await BuildSummariesAsync(updatedGrants, cancellationToken);
    }

    private async Task<IReadOnlyList<SpaceSummaryDto>> BuildSummariesAsync(
        IReadOnlyList<SpaceGrant> grants, CancellationToken cancellationToken)
    {
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
}
