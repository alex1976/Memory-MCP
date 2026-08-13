using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record SpaceCounts(Guid SpaceId, int DocumentCount, int MemoryCount);

public interface ISpaceRepository
{
    Task<IReadOnlyList<SpaceCounts>> GetCountsAsync(IReadOnlyList<Guid> spaceIds, CancellationToken cancellationToken = default);

    /// <summary>Loads the (tracked) grants for an API key so a caller can flip <see cref="ApiKeySpaceGrant.IsDefault"/>
    /// on them and persist via <see cref="IUnitOfWork"/> — used by select-space to switch the active space.</summary>
    Task<IReadOnlyList<ApiKeySpaceGrant>> GetGrantsForApiKeyAsync(Guid apiKeyId, CancellationToken cancellationToken = default);
}
