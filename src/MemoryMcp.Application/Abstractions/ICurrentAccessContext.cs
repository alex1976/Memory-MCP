using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

public sealed record SpaceGrant(Guid SpaceId, string SpaceKey, string SpaceName, AccessLevel AccessLevel, bool IsDefault);

public interface ICurrentAccessContext
{
    Guid ApiKeyId { get; }
    string? OwnerLabel { get; }
    IReadOnlyList<SpaceGrant> Grants { get; }
    SpaceGrant? ActiveGrant { get; }

    SpaceGrant? ResolveGrant(string? containerTag);

    bool HasAccess(SpaceGrant? grant, AccessLevel required);
}
