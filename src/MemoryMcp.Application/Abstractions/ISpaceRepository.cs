namespace MemoryMcp.Application.Abstractions;

public sealed record SpaceCounts(Guid SpaceId, int DocumentCount, int MemoryCount);

public interface ISpaceRepository
{
    Task<IReadOnlyList<SpaceCounts>> GetCountsAsync(IReadOnlyList<Guid> spaceIds, CancellationToken cancellationToken = default);
}
