namespace MemoryMcp.Application.Spaces;

public interface ISpaceService
{
    Task<IReadOnlyList<SpaceSummaryDto>> ListSpacesAsync(CancellationToken cancellationToken = default);

    Task<WhoAmIResult> WhoAmIAsync(CancellationToken cancellationToken = default);
}
