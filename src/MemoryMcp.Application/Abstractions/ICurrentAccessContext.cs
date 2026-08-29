using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

/// <summary>
/// One space this caller may touch. <paramref name="AccessLevel"/> is the *effective* level — already
/// capped by the owning user's <see cref="UserRole"/> where the snapshot is built — so no consumer has
/// to combine role and grant itself.
/// </summary>
public sealed record SpaceGrant(Guid SpaceId, string SpaceKey, string SpaceName, AccessLevel AccessLevel, bool IsDefault);

/// <summary>The person behind the credential, resolved once per request alongside the grants.</summary>
public sealed record CurrentUser(Guid Id, string Email, string DisplayName, UserRole Role);

public interface ICurrentAccessContext
{
    Guid ApiKeyId { get; }

    /// <summary>Label of the credential in use ("laptop", "ci"), not the owner's name — that is
    /// <see cref="CurrentUser.DisplayName"/>.</summary>
    string? OwnerLabel { get; }

    /// <summary>The authenticated person. Every write is attributed to <see cref="CurrentUser.Id"/>.</summary>
    CurrentUser User { get; }

    IReadOnlyList<SpaceGrant> Grants { get; }
    SpaceGrant? ActiveGrant { get; }

    SpaceGrant? ResolveGrant(string? containerTag);

    bool HasAccess(SpaceGrant? grant, AccessLevel required);
}
