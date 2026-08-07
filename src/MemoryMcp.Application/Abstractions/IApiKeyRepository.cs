namespace MemoryMcp.Application.Abstractions;

public sealed record ApiKeyAccessSnapshot(Guid ApiKeyId, string? Label, IReadOnlyList<SpaceGrant> Grants);

public interface IApiKeyRepository
{
    Task<ApiKeyAccessSnapshot?> FindActiveAccessByHashAsync(string keyHash, CancellationToken cancellationToken = default);
}
