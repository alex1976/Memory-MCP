using MemoryMcp.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace MemoryMcp.Infrastructure.Persistence.Repositories;

public sealed class ApiKeyRepository(MemoryDbContext dbContext) : IApiKeyRepository
{
    public async Task<ApiKeyAccessSnapshot?> FindActiveAccessByHashAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        var apiKey = await dbContext.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == keyHash && k.IsActive, cancellationToken);

        if (apiKey is null)
        {
            return null;
        }

        var grants = await (
            from grant in dbContext.ApiKeySpaceGrants.AsNoTracking()
            join space in dbContext.Spaces.AsNoTracking() on grant.SpaceId equals space.Id
            where grant.ApiKeyId == apiKey.Id
            select new SpaceGrant(space.Id, space.Key, space.Name, grant.AccessLevel, grant.IsDefault)
        ).ToListAsync(cancellationToken);

        return new ApiKeyAccessSnapshot(apiKey.Id, apiKey.Label, grants);
    }
}
