namespace MemoryMcp.Application.Spaces;

public interface ISpaceService
{
    Task<IReadOnlyList<SpaceSummaryDto>> ListSpacesAsync(CancellationToken cancellationToken = default);

    Task<WhoAmIResult> WhoAmIAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes the space identified by <paramref name="spaceKey"/> the API key's active (default) space,
    /// among the spaces it already has a grant for — this cannot grant access to a new space.</summary>
    Task<IReadOnlyList<SpaceSummaryDto>> SetActiveSpaceAsync(string spaceKey, CancellationToken cancellationToken = default);
}
