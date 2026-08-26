using MemoryMcp.Domain;

namespace MemoryMcp.Application.Abstractions;

/// <summary>
/// The single place where "resolve a space, then prove the current API key may touch it" is expressed.
/// Every service entry point that takes a containerTag goes through here, so the resolution order and
/// the failure messages stay identical across tools instead of drifting per service.
/// </summary>
public static class AccessContextExtensions
{
    /// <summary>
    /// Resolves <paramref name="containerTag"/> (or the active space when null) and asserts the key holds
    /// at least <paramref name="required"/> access to it.
    /// </summary>
    /// <exception cref="SpaceNotFoundException">The tag matches no space granted to this key.</exception>
    /// <exception cref="AccessDeniedException">The space is granted, but at a lower access level.</exception>
    public static SpaceGrant RequireSpace(
        this ICurrentAccessContext accessContext, string? containerTag, AccessLevel required)
    {
        var grant = accessContext.ResolveGrant(containerTag) ?? throw new SpaceNotFoundException(containerTag);
        accessContext.RequireAccess(grant, required, $"space '{grant.SpaceKey}'");
        return grant;
    }

    /// <summary>
    /// Same check for an entity reached by id rather than by tag: the space is whichever one already owns
    /// the entity, so a key with no grant to it is denied rather than told the space doesn't exist.
    /// <paramref name="target"/> names the entity in the failure message (e.g. "document '{id}'").
    /// </summary>
    public static void RequireSpaceAccess(
        this ICurrentAccessContext accessContext, Guid spaceId, AccessLevel required, string target)
    {
        var grant = accessContext.Grants.FirstOrDefault(g => g.SpaceId == spaceId);
        accessContext.RequireAccess(grant, required, target);
    }

    private static void RequireAccess(
        this ICurrentAccessContext accessContext, SpaceGrant? grant, AccessLevel required, string target)
    {
        if (!accessContext.HasAccess(grant, required))
        {
            throw new AccessDeniedException(
                $"The current API key does not have {required} access to {target}.");
        }
    }
}
