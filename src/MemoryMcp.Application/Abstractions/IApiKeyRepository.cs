namespace MemoryMcp.Application.Abstractions;

/// <summary>
/// Everything authentication needs about a presented key, resolved in one go: the credential, the
/// person who owns it, and the spaces they may touch with their effective access level already capped
/// by <see cref="CurrentUser.Role"/>.
/// </summary>
public sealed record ApiKeyAccessSnapshot(Guid ApiKeyId, string? Label, CurrentUser User, IReadOnlyList<SpaceGrant> Grants);

public interface IApiKeyRepository
{
    /// <summary>Returns null when the key is unknown, revoked, or belongs to a deactivated user.</summary>
    Task<ApiKeyAccessSnapshot?> FindActiveAccessByHashAsync(string keyHash, CancellationToken cancellationToken = default);
}
