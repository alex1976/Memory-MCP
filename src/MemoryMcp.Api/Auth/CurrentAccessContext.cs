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

    private CurrentUser? _user;

    /// <summary>Reading this before authentication has run is a programming error, not a 401: every
    /// service entry point sits behind <c>RequireAuthorization</c>, so an unset user means the context was
    /// resolved outside the request pipeline rather than that the caller was anonymous.</summary>
    public CurrentUser User => _user
        ?? throw new InvalidOperationException("The access context has not been initialized with an authenticated user.");

    public void Initialize(ApiKeyAccessSnapshot snapshot)
    {
        ApiKeyId = snapshot.ApiKeyId;
        OwnerLabel = snapshot.Label;
        _user = snapshot.User;
        Grants = snapshot.Grants;
    }

    public SpaceGrant? ResolveGrant(string? containerTag) =>
        containerTag is null
            ? ActiveGrant
            : Grants.FirstOrDefault(g => g.SpaceKey == containerTag);

    /// <summary>The grant's level is already capped by the caller's <see cref="UserRole"/> (see
    /// <c>ApiKeyRepository</c>), so this comparison alone decides whether a Reader may write.</summary>
    public bool HasAccess(SpaceGrant? grant, AccessLevel required) =>
        grant is not null && grant.AccessLevel >= required;
}
