using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Application.Tests.TestSupport;

public sealed class FakeAccessContext : ICurrentAccessContext
{
    public Guid ApiKeyId { get; init; } = Guid.NewGuid();
    public string? OwnerLabel { get; init; }

    public CurrentUser User { get; init; } =
        new(Guid.NewGuid(), "writer@example.test", "Test Writer", UserRole.Writer);

    public IReadOnlyList<SpaceGrant> Grants { get; init; } = [];
    public SpaceGrant? ActiveGrant => Grants.FirstOrDefault(g => g.IsDefault);

    public SpaceGrant? ResolveGrant(string? containerTag) =>
        containerTag is null ? ActiveGrant : Grants.FirstOrDefault(g => g.SpaceKey == containerTag);

    /// <summary>Mirrors the real context: grants arrive already capped by the user's role, so this is a
    /// plain comparison. A test that wants a Reader gives the grant <see cref="AccessLevel.Read"/>.</summary>
    public bool HasAccess(SpaceGrant? grant, AccessLevel required) =>
        grant is not null && grant.AccessLevel >= required;
}
