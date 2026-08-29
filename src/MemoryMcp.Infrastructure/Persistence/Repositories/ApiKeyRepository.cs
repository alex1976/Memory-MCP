using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class ApiKeyRepository(MemoryDbContext dbContext) : IApiKeyRepository
{
    public async Task<ApiKeyAccessSnapshot?> FindActiveAccessByHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        // Joined to users so a deactivated person's keys stop working without having to find them one by
        // one — the single offboarding step that covers every credential they ever minted.
        var credential = await (
            from key in dbContext.ApiKeys.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on key.UserId equals user.Id
            where key.KeyHash == keyHash && key.IsActive && user.IsActive
            select new
            {
                key.Id,
                key.Label,
                UserId = user.Id,
                user.Email,
                user.DisplayName,
                user.Role,
            }).FirstOrDefaultAsync(cancellationToken);

        if (credential is null)
        {
            return null;
        }

        // The role caps the grant, and the capping happens here rather than at each call site: every
        // consumer of SpaceGrant.AccessLevel — RequireSpace, listSpaces, whoAmI — then reads one already
        // effective level and none of them can disagree about what a Reader may do.
        var ceiling = credential.Role.MaxAccessLevel();

        var grants = await (
            from grant in dbContext.ApiKeySpaceGrants.AsNoTracking()
            join space in dbContext.Spaces.AsNoTracking() on grant.SpaceId equals space.Id
            where grant.ApiKeyId == credential.Id
            select new
            {
                space.Id,
                space.Key,
                space.Name,
                grant.AccessLevel,
                grant.IsDefault,
            }).ToListAsync(cancellationToken);

        var effectiveGrants = grants
            .Select(g => new SpaceGrant(
                g.Id, g.Key, g.Name, g.AccessLevel < ceiling ? g.AccessLevel : ceiling, g.IsDefault))
            .ToList();

        var currentUser = new CurrentUser(credential.UserId, credential.Email, credential.DisplayName, credential.Role);
        return new ApiKeyAccessSnapshot(credential.Id, credential.Label, currentUser, effectiveGrants);
    }
}
