using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Tests.TestSupport;

public sealed class FakeAccessContext : ICurrentAccessContext
{
    public Guid ApiKeyId { get; init; } = Guid.NewGuid();
    public string? OwnerLabel { get; init; }
    public IReadOnlyList<SpaceGrant> Grants { get; init; } = [];
    public SpaceGrant? ActiveGrant => Grants.FirstOrDefault(g => g.IsDefault);

    public SpaceGrant? ResolveGrant(string? containerTag) =>
        containerTag is null ? ActiveGrant : Grants.FirstOrDefault(g => g.SpaceKey == containerTag);

    public bool HasAccess(SpaceGrant? grant, AccessLevel required) =>
        grant is not null && grant.AccessLevel >= required;
}
