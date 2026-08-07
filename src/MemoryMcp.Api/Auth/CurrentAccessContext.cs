using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;

namespace MemoryMcp.Api.Auth;

/// <summary>
/// Scoped per-request context, populated by <see cref="ApiKeyAuthenticationHandler"/> once the
/// API key has been resolved against the database.
/// </summary>
public sealed class CurrentAccessContext : ICurrentAccessContext
{
    public Guid ApiKeyId { get; private set; }
    public string? OwnerLabel { get; private set; }
    public IReadOnlyList<SpaceGrant> Grants { get; private set; } = [];
    public SpaceGrant? ActiveGrant => Grants.FirstOrDefault(g => g.IsDefault);

    public void Initialize(ApiKeyAccessSnapshot snapshot)
    {
        ApiKeyId = snapshot.ApiKeyId;
        OwnerLabel = snapshot.Label;
        Grants = snapshot.Grants;
    }

    public SpaceGrant? ResolveGrant(string? containerTag) =>
        containerTag is null
            ? ActiveGrant
            : Grants.FirstOrDefault(g => g.SpaceKey == containerTag);

    public bool HasAccess(SpaceGrant? grant, AccessLevel required) =>
        grant is not null && grant.AccessLevel >= required;
}
